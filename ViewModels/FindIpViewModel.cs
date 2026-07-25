using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using M1Scan.Models;
using M1Scan.Services;
using M1Scan.Utils;

namespace M1Scan.ViewModels
{
    /// <summary>
    /// "Find IP"-fanen: passiv, Wireshark-agtig lytter der finder enheder på samme
    /// fysiske switch/L2-segment — også dem med en IP uden for dit eget subnet.
    /// Fanger broadcast/multicast "wakeup calls" (DHCP, mDNS, SSDP, NetBIOS m.fl.).
    /// </summary>
    public class FindIpViewModel : ObservableObject, IDisposable
    {
        private readonly IFindIpService _findIpService;
        private readonly INetworkService _networkService;
        private readonly DispatcherTimer _flushTimer;

        // Producer (sniffer-tråd) skriver her; UI-timeren læser og flusher til collection.
        private readonly ConcurrentDictionary<string, PacketAgg> _pending = new();
        // Kilde-IP'er vi allerede har startet MAC-opslag for (undgå gentagne ARP).
        private readonly HashSet<string> _macResolveStarted = new();
        // O(1)-opslag fra IP til den viste enhed (UI-tråd only) — undgår lineær scan pr. tick.
        private readonly Dictionary<string, SniffedDevice> _deviceByIp = new();

        private ObservableCollection<SniffedDevice> _devices = new();
        private ObservableCollection<NetworkAdapter> _availableAdapters = new();
        private NetworkAdapter? _selectedAdapter;
        private bool _isCapturing;
        private bool _onlyOutsideSubnet;
        private string _statusMessage = "Vælg en adapter og tryk Start for at lytte på segmentet.";
        private long _totalPackets;

        // Lokalt subnet — bruges til at afgøre om en kilde-IP er "uden for subnet".
        private uint _localNetwork;
        private uint _localMask;
        private readonly HashSet<string> _localIps = new(StringComparer.OrdinalIgnoreCase);

        public ObservableCollection<SniffedDevice> Devices
        {
            get => _devices;
            set => SetProperty(ref _devices, value);
        }

        public ICollectionView DevicesView { get; }

        public ObservableCollection<NetworkAdapter> AvailableAdapters
        {
            get => _availableAdapters;
            set => SetProperty(ref _availableAdapters, value);
        }

        public NetworkAdapter? SelectedAdapter
        {
            get => _selectedAdapter;
            set
            {
                if (SetProperty(ref _selectedAdapter, value))
                    OnPropertyChanged(nameof(SelectedAdapterLabel));
            }
        }

        public string SelectedAdapterLabel
        {
            get
            {
                if (_selectedAdapter == null) return "Vælg adapter";
                var ip = _selectedAdapter.IpAddresses.Length > 0 ? _selectedAdapter.IpAddresses[0] : "";
                return string.IsNullOrEmpty(ip)
                    ? _selectedAdapter.Description
                    : $"{_selectedAdapter.Description} — {ip}";
            }
        }

        public bool IsCapturing
        {
            get => _isCapturing;
            private set
            {
                if (SetProperty(ref _isCapturing, value))
                    System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool OnlyForeignOnSegment
        {
            get => _onlyOutsideSubnet;
            set { if (SetProperty(ref _onlyOutsideSubnet, value)) DevicesView.Refresh(); }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public long TotalPackets
        {
            get => _totalPackets;
            set => SetProperty(ref _totalPackets, value);
        }

        public RelayCommand StartCommand { get; }
        public RelayCommand StopCommand { get; }
        public RelayCommand ClearCommand { get; }
        public AsyncRelayCommand RefreshAdaptersCommand { get; }

        public FindIpViewModel(IFindIpService findIpService, INetworkService networkService)
        {
            _findIpService = findIpService;
            _networkService = networkService;

            _findIpService.PacketCaptured += OnPacketCaptured;
            _findIpService.CaptureStopped += OnCaptureStopped;

            DevicesView = CollectionViewSource.GetDefaultView(_devices);
            DevicesView.Filter = o => !_onlyOutsideSubnet || (o is SniffedDevice d && d.IsForeignOnSegment);

            // Live filtering: en enhed bliver først "fremmed på segmentet" når den senere
            // sender broadcast eller ARP-resolver — uden dette ville filteret ikke køre igen
            // på den ejendomsændring, og fundet ville forblive skjult.
            if (DevicesView is ICollectionViewLiveShaping live && live.CanChangeLiveFiltering)
            {
                live.LiveFilteringProperties.Add(nameof(SniffedDevice.IsForeignOnSegment));
                live.IsLiveFiltering = true;
            }

            StartCommand = new RelayCommand(_ => Start(), _ => !IsCapturing && SelectedAdapter != null);
            StopCommand = new RelayCommand(_ => Stop(), _ => IsCapturing);
            ClearCommand = new RelayCommand(_ => Clear());
            RefreshAdaptersCommand = new AsyncRelayCommand(
                _ => RefreshAdaptersAsync(),
                onError: ex => StatusMessage = $"Kunne ikke hente adaptere: {ex.Message}");

            _flushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
            _flushTimer.Tick += (_, _) => FlushPending();

            RefreshAdaptersCommand.Execute(null);
        }

        private async System.Threading.Tasks.Task RefreshAdaptersAsync()
        {
            try
            {
                var adapters = await _networkService.GetNetworkAdaptersAsync();
                var connected = adapters.Where(a => a.IsConnected && a.IpAddresses.Length > 0).ToList();
                AvailableAdapters = new ObservableCollection<NetworkAdapter>(connected);

                // Find IP skal bruge default route adapter (som Scan-fanen gør)
                // — undgår VPN, Tailscale tunnels osv. og bruger systemets primary connection
                // Filter: adapter med Gateway + ikke-VPN description
                var vpnKeywords = new[] { "Tailscale", "Hamachi", "OpenVPN", "WireGuard", "NordVPN", "ExpressVPN", "ProtonVPN", "Cisco" };
                var physicalAdapters = connected.Where(a =>
                    !string.IsNullOrEmpty(a.Gateway) &&
                    !vpnKeywords.Any(kw => a.Description?.Contains(kw, StringComparison.OrdinalIgnoreCase) ?? false)
                ).ToList();

                var defaultAdapter = physicalAdapters.FirstOrDefault() ?? connected.FirstOrDefault();
                // Only set if not already selected (preserve user's choice across refresh)
                SelectedAdapter ??= defaultAdapter;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Kunne ikke hente adaptere: {ex.Message}";
            }
        }

        private void Start()
        {
            if (SelectedAdapter == null || SelectedAdapter.IpAddresses.Length == 0)
            {
                StatusMessage = "Vælg en adapter med en IP-adresse først.";
                return;
            }

            string localIp = SelectedAdapter.IpAddresses[0];
            ComputeLocalSubnet(SelectedAdapter);

            try
            {
                _findIpService.Start(localIp);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Kunne ikke starte lytning: {ex.Message} (kræver administrator).";
                return;
            }

            IsCapturing = true;
            _flushTimer.Start();
            StatusMessage = $"Lytter på {localIp} … fanger broadcast/multicast fra segmentet.";
        }

        private void Stop()
        {
            _findIpService.Stop();
            _flushTimer.Stop();
            FlushPending();
            IsCapturing = false;
            StatusMessage = $"Stoppet. {Devices.Count} enheder fundet, {TotalPackets} pakker set.";
        }

        private void Clear()
        {
            _pending.Clear();
            lock (_macResolveStarted) _macResolveStarted.Clear();
            Devices.Clear();
            _deviceByIp.Clear();
            TotalPackets = 0;
            StatusMessage = IsCapturing ? "Ryddet — lytter videre." : "Ryddet.";
        }

        // Rejses af servicen hvis captureløkken dør uventet (adapter forsvandt midt i capture).
        private void OnCaptureStopped()
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (!IsCapturing) return;
                _flushTimer.Stop();
                FlushPending();
                IsCapturing = false;
                StatusMessage = "Capture stoppede uventet — adapteren forsvandt måske. Tryk Start igen.";
            });
        }

        // Kaldes fra sniffer-baggrundstråden — hold den let, ingen UI-adgang her.
        private void OnPacketCaptured(CapturedPacket packet)
        {
            string src = packet.SourceIp;

            // Filtrér vores egen udgående trafik, loopback og ugyldige kilder fra.
            if (_localIps.Contains(src)) return;
            if (src.StartsWith("127.") || src == "0.0.0.0" || src == "255.255.255.255") return;

            var agg = _pending.GetOrAdd(src, _ => new PacketAgg { FirstSeen = DateTime.Now });
            lock (agg)
            {
                agg.Count++;
                agg.LastSeen = DateTime.Now;
                agg.LastProtocol = packet.ProtocolName;
                // Broadcast/multicast destination = et "wakeup call". Sådanne pakker routes
                // ikke — kun en enhed på vores eget L2-segment kan have sendt dem hertil.
                if (IsBroadcastOrMulticast(packet.DestIp)) agg.SawBroadcast = true;
                agg.Dirty = true;
            }
        }

        // Kaldes på UI-tråden af _flushTimer — flytter ophobede tal ind i collection.
        private void FlushPending()
        {
            long grandTotal = 0;
            foreach (var kvp in _pending)
            {
                var agg = kvp.Value;
                long count; DateTime firstSeen, lastSeen; string proto; bool dirty; bool sawBroadcast;
                lock (agg)
                {
                    count = agg.Count; firstSeen = agg.FirstSeen; lastSeen = agg.LastSeen;
                    proto = agg.LastProtocol; dirty = agg.Dirty; sawBroadcast = agg.SawBroadcast;
                    agg.Dirty = false;
                }
                grandTotal += count;
                if (!dirty) continue;

                if (!_deviceByIp.TryGetValue(kvp.Key, out var device))
                {
                    bool outside = IsOutsideSubnet(kvp.Key);
                    device = new SniffedDevice
                    {
                        IpAddress = kvp.Key,
                        FirstSeen = firstSeen,
                        IsOutsideSubnet = outside,
                        IsCarrierNat = IsCarrierNat(kvp.Key),
                        // I vores eget subnet = per definition på segmentet.
                        IsOnSegment = !outside
                    };
                    Devices.Add(device);
                    _deviceByIp[kvp.Key] = device;
                    BeginResolveMac(device);
                }
                // Har vi set et broadcast/multicast fra denne kilde, sidder den på vores segment.
                if (sawBroadcast) device.IsOnSegment = true;
                device.PacketCount = count;
                device.LastSeen = lastSeen;
                device.LastProtocol = proto;
            }
            TotalPackets = grandTotal;
        }

        private async void BeginResolveMac(SniffedDevice device)
        {
            lock (_macResolveStarted)
            {
                if (!_macResolveStarted.Add(device.IpAddress)) return;
            }

            try
            {
                // SendARP virker på tværs af subnets på samme L2-segment, så vi kan
                // også få MAC på enheder uden for vores eget subnet.
                string mac = await _networkService.GetMacAddressAsync(device.IpAddress);
                if (string.IsNullOrEmpty(mac)) return;

                string vendor = OuiLookup.Lookup(mac);
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    device.MacAddress = mac;
                    device.Vendor = vendor;
                    // ARP svarede → enheden er L2-reachable, altså på vores fysiske segment.
                    device.IsOnSegment = true;
                });
            }
            catch { /* MAC ukendt — fx enhed der ikke svarer på ARP */ }
        }

        private void ComputeLocalSubnet(NetworkAdapter adapter)
        {
            _localIps.Clear();
            foreach (var ip in adapter.IpAddresses) _localIps.Add(ip);

            _localNetwork = 0;
            _localMask = 0;
            if (adapter.IpAddresses.Length > 0 &&
                IPAddress.TryParse(adapter.IpAddresses[0], out var localIp) &&
                IPAddress.TryParse(adapter.SubnetMask ?? "255.255.255.0", out var mask))
            {
                _localMask = ToUint(mask);
                _localNetwork = ToUint(localIp) & _localMask;
            }
        }

        private bool IsOutsideSubnet(string ip)
        {
            if (_localMask == 0) return false;
            if (!IPAddress.TryParse(ip, out var addr)) return false;
            // Multicast (224.0.0.0/4) og broadcast tæller ikke som "en fremmed enhed".
            var b = addr.GetAddressBytes();
            if (b[0] >= 224) return false;
            return (ToUint(addr) & _localMask) != _localNetwork;
        }

        // CGNAT / Carrier-Grade NAT: 100.64.0.0/10 (RFC 6598) — ISP'ens delte NAT-lag.
        private static bool IsCarrierNat(string ip)
        {
            if (!IPAddress.TryParse(ip, out var addr)) return false;
            var b = addr.GetAddressBytes();
            return b[0] == 100 && b[1] >= 64 && b[1] <= 127;
        }

        // Broadcast (255.255.255.255 eller subnet-broadcast) eller multicast (224-239.x.x.x).
        private bool IsBroadcastOrMulticast(string ip)
        {
            if (!IPAddress.TryParse(ip, out var addr)) return false;
            var b = addr.GetAddressBytes();
            if (b[0] >= 224 && b[0] <= 239) return true;           // multicast
            if (b[0] == 255 && b[1] == 255 && b[2] == 255 && b[3] == 255) return true; // limited broadcast
            if (_localMask != 0)
            {
                uint broadcast = _localNetwork | ~_localMask;       // subnet-directed broadcast
                if (ToUint(addr) == broadcast) return true;
            }
            return false;
        }

        private static uint ToUint(IPAddress ip)
        {
            var b = ip.GetAddressBytes();
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }

        public void Dispose()
        {
            _findIpService.PacketCaptured -= OnPacketCaptured;
            _findIpService.CaptureStopped -= OnCaptureStopped;
            _findIpService.Stop();
            _flushTimer.Stop();
            (_findIpService as IDisposable)?.Dispose();
        }

        private sealed class PacketAgg
        {
            public long Count;
            public DateTime FirstSeen;
            public DateTime LastSeen;
            public string LastProtocol = string.Empty;
            public bool Dirty;
            public bool SawBroadcast;
        }
    }
}
