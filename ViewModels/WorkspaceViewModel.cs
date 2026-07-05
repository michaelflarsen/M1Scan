using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using M1Scan.Models;
using M1Scan.Services;
using M1Scan.Utils;
using M1Scan.Views;

namespace M1Scan.ViewModels
{
    public class WorkspaceViewModel : ObservableObject, IDisposable
    {
        private readonly IIpConfigService _ipConfigService;
        private readonly IExportService _exportService;
        private readonly DispatcherTimer _pingTimer;
        private readonly DispatcherTimer _adapterTimer;

        private static readonly string PersistPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "M1Scan", "workspace.json");

        // Adapter descriptions that indicate virtual/non-physical interfaces to skip
        private static readonly string[] ExcludedKeywords =
            { "Virtual", "Hyper-V", "TAP", "Tailscale", "Bluetooth" };

        // MY IP
        private string _myIpAddress     = "—";
        private string _mySubnetDisplay = "";
        private string _myAdapterName   = "";
        private string _mySubnetMask    = "255.255.255.0";
        private string _myGateway       = "";
        private string _myAdapterSystemName = "";
        private List<AdapterEntry> _availableAdapters = new();

        // Add row
        private string _newIpInput     = string.Empty;
        private string _newDescription = string.Empty;

        // Bulk add
        private string _bulkBase  = "192.168.1";
        private int    _bulkStart = 1;
        private int    _bulkEnd   = 10;

        // Interval + status
        private int    _pingIntervalSeconds = 3;
        private string _statusMessage       = "Ready";

        // Clear-all confirm state
        private bool  _clearConfirmPending;
        private bool? _gatewayOnline;
        private bool  _isDhcpBusy;
        private string _clearAllLabel = "Ryd alle";
        private DispatcherTimer? _clearConfirmTimer;

        // ── Properties ──────────────────────────────────────────────────────

        public string MyIpAddress
        {
            get => _myIpAddress;
            set => SetProperty(ref _myIpAddress, value);
        }

        public string MySubnetDisplay
        {
            get => _mySubnetDisplay;
            set => SetProperty(ref _mySubnetDisplay, value);
        }

        public string MyAdapterName
        {
            get => _myAdapterName;
            set => SetProperty(ref _myAdapterName, value);
        }

        public string ActiveAdapterSystemName => _myAdapterSystemName;
        public IReadOnlyList<AdapterEntry> AvailableAdapters => _availableAdapters;

        public string GatewayIp => _myGateway;

        public bool? GatewayOnline
        {
            get => _gatewayOnline;
            set => SetProperty(ref _gatewayOnline, value);
        }

        public bool IsDhcpBusy
        {
            get => _isDhcpBusy;
            set
            {
                if (SetProperty(ref _isDhcpBusy, value))
                    System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }

        public ObservableCollection<PingEntry> WatchList { get; } = new();

        public string NewIpInput
        {
            get => _newIpInput;
            set => SetProperty(ref _newIpInput, value);
        }

        public string NewDescription
        {
            get => _newDescription;
            set => SetProperty(ref _newDescription, value);
        }

        public string BulkBase
        {
            get => _bulkBase;
            set { if (SetProperty(ref _bulkBase, value)) OnPropertyChanged(nameof(BulkPreview)); }
        }

        public int BulkStart
        {
            get => _bulkStart;
            set { if (SetProperty(ref _bulkStart, Math.Clamp(value, 0, 254))) OnPropertyChanged(nameof(BulkPreview)); }
        }

        public int BulkEnd
        {
            get => _bulkEnd;
            set { if (SetProperty(ref _bulkEnd, Math.Clamp(value, 0, 254))) OnPropertyChanged(nameof(BulkPreview)); }
        }

        public string BulkPreview => $"→  {BulkBase}.{BulkStart}  –  {BulkBase}.{BulkEnd}";

        public int PingIntervalSeconds
        {
            get => _pingIntervalSeconds;
            set
            {
                int clamped = Math.Clamp(value, 1, 30);
                if (SetProperty(ref _pingIntervalSeconds, clamped))
                    _pingTimer.Interval = TimeSpan.FromSeconds(clamped);
            }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public string ClearAllLabel
        {
            get => _clearAllLabel;
            private set => SetProperty(ref _clearAllLabel, value);
        }

        // ── Commands ─────────────────────────────────────────────────────────

        public RelayCommand AddEntryCommand      { get; }
        public RelayCommand RemoveEntryCommand   { get; }
        public RelayCommand BulkAddCommand       { get; }
        public RelayCommand ToggleFollowCommand  { get; }
        public RelayCommand RefreshAdapterCommand { get; }
        public RelayCommand SelectAdapterCommand { get; }
        public RelayCommand CheckPortsCommand    { get; }
        public RelayCommand OpenPort80Command    { get; }
        public RelayCommand OpenPort443Command   { get; }
        public RelayCommand OpenPort8080Command  { get; }
        public RelayCommand RemoveTimeoutCommand { get; }
        public RelayCommand RemoveOnlineCommand  { get; }
        public RelayCommand ClearAllCommand      { get; }
        public RelayCommand SetDhcpCommand       { get; }
        public RelayCommand ExportCommand        { get; }

        // ── Constructor ──────────────────────────────────────────────────────

        public WorkspaceViewModel(IIpConfigService ipConfigService, IExportService exportService)
        {
            _ipConfigService = ipConfigService;
            _exportService = exportService;

            _pingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_pingIntervalSeconds) };
            _pingTimer.Tick += async (_, _) => await PingAllAsync();
            _pingTimer.Start();

            // Safety poll every 30s in case network-change event is missed
            _adapterTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _adapterTimer.Tick += (_, _) => RefreshMyIp();
            _adapterTimer.Start();

            AddEntryCommand = new RelayCommand(
                _ => AddEntry(),
                _ => !string.IsNullOrWhiteSpace(NewIpInput));

            RemoveEntryCommand = new RelayCommand(param =>
            {
                if (param is PingEntry e) { WatchList.Remove(e); SaveWatchList(); }
            });

            BulkAddCommand = new RelayCommand(
                _ => BulkAdd(),
                param => !string.IsNullOrWhiteSpace(BulkBase) && BulkStart <= BulkEnd
                         && IPAddress.TryParse($"{BulkBase.Trim()}.0", out _));

            ToggleFollowCommand = new RelayCommand(param =>
            {
                if (param is not PingEntry entry) return;
                var watchedIps = WatchList.Select(e => e.IpAddress).ToList();
                var dialog = new FollowDialog(
                    _ipConfigService,
                    _myAdapterSystemName,
                    _myAdapterName,
                    _myIpAddress,
                    SuggestFollowIp(entry.IpAddress, watchedIps),
                    _mySubnetMask,
                    DeriveGateway(entry.IpAddress))
                {
                    Owner = Application.Current.MainWindow
                };
                dialog.ShowDialog();
                RefreshMyIp();
            });

            RefreshAdapterCommand = new RelayCommand(_ => RefreshMyIp());

            SetDhcpCommand = new RelayCommand(
                _ => _ = ExecuteSetDhcpAsync(),
                _ => !string.IsNullOrEmpty(_myAdapterSystemName) && !_isDhcpBusy);

            SelectAdapterCommand = new RelayCommand(param =>
            {
                if (param is AdapterEntry entry) SelectAdapter(entry);
            });

            CheckPortsCommand = new RelayCommand(param =>
            {
                if (param is PingEntry entry)
                    _ = CheckPortsAsync(entry);
            });

            OpenPort80Command   = new RelayCommand(param => OpenUrl($"http://{param}"),      param => !string.IsNullOrEmpty(param as string));
            OpenPort443Command  = new RelayCommand(param => OpenUrl($"https://{param}"),     param => !string.IsNullOrEmpty(param as string));
            OpenPort8080Command = new RelayCommand(param => OpenUrl($"http://{param}:8080"), param => !string.IsNullOrEmpty(param as string));

            RemoveTimeoutCommand = new RelayCommand(
                _ =>
                {
                    foreach (var e in WatchList.Where(e => e.Status is PingStatus.Timeout or PingStatus.Offline).ToList())
                        WatchList.Remove(e);
                    SaveWatchList();
                },
                _ => WatchList.Any(e => e.Status is PingStatus.Timeout or PingStatus.Offline));

            RemoveOnlineCommand = new RelayCommand(
                _ =>
                {
                    foreach (var e in WatchList.Where(e => e.Status == PingStatus.Online).ToList())
                        WatchList.Remove(e);
                    SaveWatchList();
                },
                _ => WatchList.Any(e => e.Status == PingStatus.Online));

            ClearAllCommand = new RelayCommand(
                _ =>
                {
                    if (!_clearConfirmPending)
                    {
                        _clearConfirmTimer?.Stop();
                        _clearConfirmTimer = null;
                        _clearConfirmPending = true;
                        ClearAllLabel = "Bekræft?";
                        _clearConfirmTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                        _clearConfirmTimer.Tick += (_, _) =>
                        {
                            _clearConfirmTimer?.Stop();
                            _clearConfirmTimer = null;
                            _clearConfirmPending = false;
                            ClearAllLabel = "Ryd alle";
                        };
                        _clearConfirmTimer.Start();
                    }
                    else
                    {
                        _clearConfirmTimer?.Stop();
                        _clearConfirmTimer = null;
                        _clearConfirmPending = false;
                        ClearAllLabel = "Ryd alle";
                        if (WatchList.Count > 0)
                        {
                            WatchList.Clear();
                            SaveWatchList();
                        }
                    }
                },
                _ => WatchList.Count > 0);

            ExportCommand = new RelayCommand(
                async _ => await ExportAsync(),
                _ => WatchList.Count > 0);

            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;

            LoadWatchList();
            RefreshMyIp();
        }

        private async Task ExportAsync()
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "CSV-fil (*.csv)|*.csv|JSON-fil (*.json)|*.json",
                FileName = $"m1scan-watchlist-{DateTime.Now:yyyy-MM-dd_HHmm}.csv"
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                var entries = WatchList.ToList();
                await _exportService.ExportWatchListAsync(entries, dialog.FileName);
                StatusMessage = $"Eksporterede {entries.Count} enheder til {Path.GetFileName(dialog.FileName)}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Eksport fejlede: {ex.Message}";
            }
        }

        // ── Private methods ──────────────────────────────────────────────────

        private void AddEntry()
        {
            var ip = NewIpInput.Trim();
            if (string.IsNullOrWhiteSpace(ip)) return;
            if (WatchList.Any(e => e.IpAddress == ip)) return;
            WatchList.Add(new PingEntry { IpAddress = ip, Description = NewDescription.Trim() });
            NewIpInput     = string.Empty;
            NewDescription = string.Empty;
            SaveWatchList();
        }

        private void BulkAdd()
        {
            int added = 0;
            for (int i = BulkStart; i <= BulkEnd; i++)
            {
                var ip = $"{BulkBase.Trim()}.{i}";
                if (WatchList.Any(e => e.IpAddress == ip)) continue;
                WatchList.Add(new PingEntry { IpAddress = ip });
                added++;
            }
            if (added > 0) SaveWatchList();
            StatusMessage = $"Added {added} entries";
        }

        private async Task PingAllAsync()
        {
            var entries = WatchList.ToList();
            if (entries.Count == 0) return;

            var tasks = entries.Select(async entry =>
            {
                var previousStatus = entry.Status;
                PingStatus newStatus;
                long ms = 0;
                try
                {
                    using var ping = new Ping();
                    var reply = await ping.SendPingAsync(entry.IpAddress, 1000);
                    if (reply.Status == IPStatus.Success)
                    {
                        newStatus = PingStatus.Online;
                        ms        = reply.RoundtripTime;
                    }
                    else
                    {
                        newStatus = reply.Status == IPStatus.TimedOut
                            ? PingStatus.Timeout : PingStatus.Offline;
                    }
                }
                catch
                {
                    newStatus = PingStatus.Offline;
                }

                bool triggerPortCheck = previousStatus != PingStatus.Online
                                     && newStatus == PingStatus.Online
                                     && entry.Port80Open == null;

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    entry.Status      = newStatus;
                    entry.IsOnline    = newStatus == PingStatus.Online;
                    entry.RoundtripMs = ms;
                    entry.LastChecked = DateTime.Now;
                });

                if (triggerPortCheck && Application.Current != null)
                    _ = CheckPortsAsync(entry);
            });

            await Task.WhenAll(tasks);

            if (Application.Current != null)
                await Application.Current.Dispatcher.InvokeAsync(
                    System.Windows.Input.CommandManager.InvalidateRequerySuggested);
        }

        private static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { }
        }

        private async Task CheckPortsAsync(PingEntry entry)
        {
            await Application.Current.Dispatcher.InvokeAsync(() => entry.IsCheckingPorts = true);

            bool[] results = await Task.WhenAll(
                CheckPortAsync(entry.IpAddress, 80),
                CheckPortAsync(entry.IpAddress, 443),
                CheckPortAsync(entry.IpAddress, 8080));

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                entry.Port80Open      = results[0];
                entry.Port443Open     = results[1];
                entry.Port8080Open    = results[2];
                entry.IsCheckingPorts = false;
            });
        }

        private static async Task<bool> CheckPortAsync(string ip, int port)
        {
            try
            {
                using var client = new TcpClient();
                using var cts = new CancellationTokenSource(1000);
                await client.ConnectAsync(ip, port, cts.Token);
                return true;
            }
            catch { return false; }
        }

        // Fix 1: prioritise physical adapters with lowest route metric; exclude virtual adapters.
        // Fix 2: gateway is filtered to IPv4-only here, so FollowDialog never receives an IPv6 address.
        private void RefreshMyIp()
        {
            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces();

                _availableAdapters = interfaces
                    .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                             && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel
                             && !IsExcluded(n))
                    .Select(n =>
                    {
                        var p = n.GetIPProperties();
                        var u = p.UnicastAddresses.FirstOrDefault(a =>
                            a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                         && !a.Address.ToString().StartsWith("169.254"));
                        return new AdapterEntry(
                            n.Name, n.Description,
                            u?.Address.ToString() ?? "",
                            n.OperationalStatus == OperationalStatus.Up);
                    })
                    .OrderByDescending(a => a.IsUp)
                    .ThenBy(a => a.Description)
                    .ToList();

                var best = interfaces
                    .Where(n => n.OperationalStatus == OperationalStatus.Up
                             && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                             && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel
                             && !IsExcluded(n))
                    .Select(n =>
                    {
                        var props = n.GetIPProperties();
                        var uni   = props.UnicastAddresses
                            .FirstOrDefault(a =>
                                a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                             && !a.Address.ToString().StartsWith("169.254"));
                        // Fix 2: pick only the first IPv4 gateway, ignore fe80:: link-local
                        var gw = props.GatewayAddresses
                            .Select(g => g.Address)
                            .FirstOrDefault(a =>
                                a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                             && a.ToString() != "0.0.0.0");
                        int metric = GetInterfaceMetric(n.Id);
                        int index  = GetInterfaceIndex(n);
                        return (Iface: n, Uni: uni, Gw: gw, Metric: metric, Index: index);
                    })
                    .Where(x => x.Uni != null)
                    .OrderByDescending(x => IsPhysical(x.Iface))  // physical Ethernet/WiFi first
                    .ThenBy(x => x.Metric)                        // lowest route metric wins
                    .ThenBy(x => x.Index)                         // fallback: lowest interface index
                    .FirstOrDefault();

                if (best.Uni == null)
                {
                    MyIpAddress          = "No connection";
                    MySubnetDisplay      = "";
                    MyAdapterName        = "";
                    _mySubnetMask        = "255.255.255.0";
                    _myGateway           = "";
                    OnPropertyChanged(nameof(GatewayIp));
                    GatewayOnline        = false;
                    _myAdapterSystemName = "";
                    return;
                }

                var ip      = best.Uni.Address.ToString();
                var mask    = best.Uni.IPv4Mask;
                var maskStr = mask?.ToString() ?? "255.255.255.0";
                int prefix  = mask == null ? 24 : CountPrefixBits(mask.GetAddressBytes());

                MyIpAddress          = ip;
                MySubnetDisplay      = $"{maskStr}  /{prefix}";
                MyAdapterName        = best.Iface.Description;
                _mySubnetMask        = maskStr;
                _myGateway           = best.Gw?.ToString() ?? "";
                _myAdapterSystemName = best.Iface.Name;
                OnPropertyChanged(nameof(GatewayIp));
                _ = PingGatewayAsync(_myGateway);
            }
            catch (Exception ex)
            {
                MyIpAddress   = "Error";
                MyAdapterName = ex.Message;
            }
        }

        // Fix 1: wait 1500ms so the OS has time to assign an IP before we read it
        private async void OnNetworkAddressChanged(object? sender, EventArgs e)
        {
            try
            {
                await Task.Delay(1500);
                if (Application.Current != null)
                    await Application.Current.Dispatcher.InvokeAsync(RefreshMyIp);
            }
            catch { }
        }

        public void Dispose()
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
            _pingTimer.Stop();
            _adapterTimer.Stop();
            _clearConfirmTimer?.Stop();
            _clearConfirmTimer = null;
        }

        private void SelectAdapter(AdapterEntry entry)
        {
            try
            {
                var iface = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => n.Name == entry.SystemName);
                if (iface == null) return;

                var props = iface.GetIPProperties();
                var uni = props.UnicastAddresses.FirstOrDefault(a =>
                    a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                 && !a.Address.ToString().StartsWith("169.254"));
                if (uni == null) return;

                var gw = props.GatewayAddresses
                    .Select(g => g.Address)
                    .FirstOrDefault(a =>
                        a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                     && a.ToString() != "0.0.0.0");

                var ip      = uni.Address.ToString();
                var mask    = uni.IPv4Mask;
                var maskStr = mask?.ToString() ?? "255.255.255.0";
                int prefix  = mask == null ? 24 : CountPrefixBits(mask.GetAddressBytes());

                MyIpAddress          = ip;
                MySubnetDisplay      = $"{maskStr}  /{prefix}";
                MyAdapterName        = iface.Description;
                _mySubnetMask        = maskStr;
                _myGateway           = gw?.ToString() ?? "";
                _myAdapterSystemName = iface.Name;
                OnPropertyChanged(nameof(GatewayIp));
                _ = PingGatewayAsync(_myGateway);
            }
            catch { }
        }

        // ── Gateway + DHCP helpers ───────────────────────────────────────────

        private async Task ExecuteSetDhcpAsync()
        {
            if (string.IsNullOrEmpty(_myAdapterSystemName)) return;
            IsDhcpBusy = true;
            await _ipConfigService.SetDhcpAsync(_myAdapterSystemName);
            await Task.Delay(1500);
            RefreshMyIp();
            IsDhcpBusy = false;
        }

        private async Task PingGatewayAsync(string gateway)
        {
            if (string.IsNullOrEmpty(gateway)) { GatewayOnline = false; return; }
            GatewayOnline = null;
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(gateway, 1000);
                GatewayOnline = reply.Status == IPStatus.Success;
            }
            catch { GatewayOnline = false; }
        }

        // ── Adapter selection helpers ────────────────────────────────────────

        private static bool IsExcluded(NetworkInterface n) =>
            ExcludedKeywords.Any(k =>
                n.Description.Contains(k, StringComparison.OrdinalIgnoreCase));

        private static bool IsPhysical(NetworkInterface n) =>
            n.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
            n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;

        // Read the route metric from the registry for precise ordering.
        // HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{GUID}\InterfaceMetric
        private static int GetInterfaceMetric(string guid)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{guid}");
                if (key?.GetValue("InterfaceMetric") is int metric) return metric;
            }
            catch { }
            return int.MaxValue;
        }

        private static int GetInterfaceIndex(NetworkInterface n)
        {
            try { return n.GetIPProperties().GetIPv4Properties().Index; }
            catch { return int.MaxValue; }
        }

        // ── IP utility helpers ───────────────────────────────────────────────

        private static int CountPrefixBits(byte[] maskBytes)
        {
            int count = 0;
            foreach (var b in maskBytes)
            {
                var n = (int)b;
                while (n != 0) { count += n & 1; n >>= 1; }
            }
            return count;
        }

        private static string SuggestFollowIp(string baseIp, List<string> watchedIps)
        {
            var parts = baseIp.Split('.');
            if (parts.Length != 4) return string.Empty;
            if (!int.TryParse(parts[3], out var lastOctet)) return string.Empty;
            var prefix = $"{parts[0]}.{parts[1]}.{parts[2]}.";
            for (int candidate = 55; candidate <= 254; candidate++)
            {
                if (candidate == lastOctet) continue;
                var ip = $"{prefix}{candidate}";
                if (!watchedIps.Contains(ip)) return ip;
            }
            return $"{prefix}55";
        }

        private static string DeriveGateway(string entryIp)
        {
            var parts = entryIp.Split('.');
            return parts.Length == 4 ? $"{parts[0]}.{parts[1]}.{parts[2]}.1" : string.Empty;
        }

        // ── Persistence ──────────────────────────────────────────────────────

        private void LoadWatchList()
        {
            try
            {
                if (!File.Exists(PersistPath)) return;
                var json = File.ReadAllText(PersistPath);
                var dtos = JsonSerializer.Deserialize<List<WatchListDto>>(json);
                if (dtos == null) return;
                foreach (var dto in dtos)
                    WatchList.Add(new PingEntry
                    {
                        IpAddress   = dto.IpAddress   ?? string.Empty,
                        Description = dto.Description ?? string.Empty
                    });
            }
            catch { /* ignore corrupt file */ }
        }

        private void SaveWatchList()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PersistPath)!);
                var dtos = WatchList
                    .Select(e => new WatchListDto { IpAddress = e.IpAddress, Description = e.Description })
                    .ToList();
                var json = JsonSerializer.Serialize(dtos, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(PersistPath, json);
            }
            catch { /* ignore write errors */ }
        }

        private sealed record WatchListDto
        {
            public string? IpAddress   { get; init; }
            public string? Description { get; init; }
        }

        public record AdapterEntry(string SystemName, string Description, string IpAddress, bool IsUp);
    }
}
