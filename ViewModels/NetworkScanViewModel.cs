using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using M1Scan.Models;
using M1Scan.Services;
using M1Scan.Utils;

namespace M1Scan.ViewModels
{
    public class NetworkScanViewModel : ObservableObject, IDisposable
    {
        private readonly INetworkService _networkService;
        private readonly IExportService _exportService;
        private readonly DispatcherTimer _autoRefreshTimer;

        private ObservableCollection<HostInfo> _discoveredHosts = new();
        private string _ipAddressInput = string.Empty;
        private string _subnetInput = "192.168.1";
        private int _startIp = 1;
        private int _endIp = 254;
        private bool _isScanning;
        private string _statusMessage = "Ready to scan";
        private int _scanProgress;
        private bool _isAutoRefreshEnabled;
        private int _autoRefreshInterval = 30;

        private ObservableCollection<NetworkAdapter> _availableAdapters = new();
        private NetworkAdapter? _selectedAdapter;

        // Batch UI updates during scan — filled from background threads, flushed on UI thread
        private readonly ConcurrentQueue<HostInfo> _uiQueue = new();
        private CancellationTokenSource? _scanCts;

        // OUI-lookup cache per scan — holds vendor names for MAC prefixes already looked up
        private readonly Dictionary<string, string> _ouiCache = new(StringComparer.OrdinalIgnoreCase);

        private DateTime _scanStartTime;
        private string _scanElapsedTime = string.Empty;

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
                {
                    OnPropertyChanged(nameof(SelectedAdapterLabel));
                    if (value != null && value.IpAddresses.Length > 0)
                    {
                        UpdateSubnetFromAdapter(value);
                        StatusMessage = $"Valgt adapter: {value.Description}";
                    }
                }
            }
        }

        public string SelectedAdapterLabel
        {
            get
            {
                if (_selectedAdapter == null) return "Select adapter";
                var ip = _selectedAdapter.IpAddresses.Length > 0 ? _selectedAdapter.IpAddresses[0] : "";
                return string.IsNullOrEmpty(ip)
                    ? _selectedAdapter.Description
                    : $"{_selectedAdapter.Description} — {ip}";
            }
        }

        // Source IP of the selected adapter — binds all scan traffic (ping, port, NetBIOS,
        // ARP) to that interface so a VPN/tunnel can't hijack the route to a LAN target.
        private string ScanSrcIp => _selectedAdapter?.IpAddresses.FirstOrDefault() ?? string.Empty;

        public ObservableCollection<HostInfo> DiscoveredHosts
        {
            get => _discoveredHosts;
            set => SetProperty(ref _discoveredHosts, value);
        }

        public string IpAddressInput
        {
            get => _ipAddressInput;
            set => SetProperty(ref _ipAddressInput, value);
        }

        public string SubnetInput
        {
            get => _subnetInput;
            set => SetProperty(ref _subnetInput, value);
        }

        public int StartIp
        {
            get => _startIp;
            set => SetProperty(ref _startIp, Math.Clamp(value, 1, 254));
        }

        public int EndIp
        {
            get => _endIp;
            set => SetProperty(ref _endIp, Math.Clamp(value, 1, 254));
        }

        public bool IsScanning
        {
            get => _isScanning;
            set => SetProperty(ref _isScanning, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public int ScanProgress
        {
            get => _scanProgress;
            set => SetProperty(ref _scanProgress, value);
        }

        public bool IsAutoRefreshEnabled
        {
            get => _isAutoRefreshEnabled;
            set
            {
                if (SetProperty(ref _isAutoRefreshEnabled, value))
                {
                    if (value)
                    {
                        _autoRefreshTimer.Interval = TimeSpan.FromSeconds(AutoRefreshInterval);
                        _autoRefreshTimer.Start();
                    }
                    else
                    {
                        _autoRefreshTimer.Stop();
                    }
                    OnPropertyChanged(nameof(AutoRefreshButtonLabel));
                }
            }
        }

        public int AutoRefreshInterval
        {
            get => _autoRefreshInterval;
            set
            {
                if (SetProperty(ref _autoRefreshInterval, value) && _autoRefreshTimer.IsEnabled)
                    _autoRefreshTimer.Interval = TimeSpan.FromSeconds(value);
            }
        }

        public string AutoRefreshButtonLabel => IsAutoRefreshEnabled ? "Stop auto" : "Start auto";

        public string ScanElapsedTime
        {
            get => _scanElapsedTime;
            set => SetProperty(ref _scanElapsedTime, value);
        }

        public RelayCommand PingSingleCommand { get; }
        public RelayCommand ScanNetworkCommand { get; }
        public RelayCommand CancelScanCommand { get; }
        public RelayCommand ClearResultsCommand { get; }
        public RelayCommand RefreshAdaptersCommand { get; }
        public RelayCommand AutoDetectSubnetCommand { get; }
        public RelayCommand ToggleAutoRefreshCommand { get; }
        public RelayCommand OpenInBrowserCommand { get; }
        public RelayCommand CopyIpCommand { get; }
        public RelayCommand PingHostCommand { get; }
        public RelayCommand ExportCommand { get; }

        public NetworkScanViewModel(INetworkService networkService, IExportService exportService)
        {
            _networkService = networkService;
            _exportService = exportService;

            _autoRefreshTimer = new DispatcherTimer();
            _autoRefreshTimer.Tick += async (_, _) => await ScanNetworkAsync(merge: true);

            PingSingleCommand = new RelayCommand(async _ => await PingSingleAsync(), _ => !IsScanning && !string.IsNullOrEmpty(IpAddressInput));
            ScanNetworkCommand = new RelayCommand(async _ => await ScanNetworkAsync(), _ => !IsScanning);
            CancelScanCommand = new RelayCommand(_ => _scanCts?.Cancel(), _ => IsScanning);
            ClearResultsCommand = new RelayCommand(_ => DiscoveredHosts.Clear());
            RefreshAdaptersCommand = new RelayCommand(async _ => await RefreshAdaptersAsync());
            AutoDetectSubnetCommand = new RelayCommand(async _ => await AutoDetectSubnetAsync(), _ => !IsScanning);
            ToggleAutoRefreshCommand = new RelayCommand(_ => IsAutoRefreshEnabled = !IsAutoRefreshEnabled);
            OpenInBrowserCommand = new RelayCommand(param =>
            {
                if (param is string url &&
                    Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
                    IPAddress.TryParse(uri.Host, out _))
                {
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                }
            });
            CopyIpCommand = new RelayCommand(param =>
            {
                if (param is string ip && !string.IsNullOrEmpty(ip))
                    System.Windows.Clipboard.SetText(ip);
            });
            PingHostCommand = new RelayCommand(
                async param =>
                {
                    if (param is string ip && !string.IsNullOrEmpty(ip))
                    {
                        IpAddressInput = ip;
                        await PingSingleAsync();
                    }
                },
                _ => !IsScanning);
            ExportCommand = new RelayCommand(
                async _ => await ExportAsync(),
                _ => DiscoveredHosts.Count > 0);

            _ = RefreshAdaptersAsync();

            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        }

        private async Task ExportAsync()
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "CSV-fil (*.csv)|*.csv|JSON-fil (*.json)|*.json",
                FileName = $"m1scan-scan-{DateTime.Now:yyyy-MM-dd_HHmm}.csv"
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                var hosts = DiscoveredHosts.ToList();
                await _exportService.ExportHostsAsync(hosts, dialog.FileName);
                StatusMessage = $"Eksporterede {hosts.Count} enheder til {System.IO.Path.GetFileName(dialog.FileName)}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Eksport fejlede: {ex.Message}";
            }
        }

        private async void OnNetworkAddressChanged(object? sender, EventArgs e)
        {
            await Task.Delay(1500); // let OS stabilise before re-enumerating
            if (Application.Current != null)
                await Application.Current.Dispatcher.InvokeAsync(RefreshAdaptersAsync);
        }

        public void Dispose()
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
            _autoRefreshTimer.Stop();
        }

        // Flushes _uiQueue to DiscoveredHosts — must be called on UI thread.
        private void FlushUiQueue()
        {
            while (_uiQueue.TryDequeue(out var host))
            {
                var existing = DiscoveredHosts.FirstOrDefault(h => h.IpAddress == host.IpAddress);
                if (existing is null)
                {
                    DiscoveredHosts.Add(host);
                }
                else
                {
                    if (!string.IsNullOrEmpty(host.HostName) && host.HostName != host.IpAddress)
                        existing.HostName = host.HostName;
                    if (host.ResponseTime > 0) existing.ResponseTime = host.ResponseTime;
                    if (!string.IsNullOrEmpty(host.Status)) existing.Status = host.Status;
                    existing.LastSeen = host.LastSeen;
                    if (host.IsReachable) existing.IsReachable = true;
                    if (!string.IsNullOrEmpty(host.OsGuess)) existing.OsGuess = host.OsGuess;
                    if (!string.IsNullOrEmpty(host.MacAddress)) existing.MacAddress = host.MacAddress;
                    if (!string.IsNullOrEmpty(host.Vendor)) existing.Vendor = host.Vendor;
                    if (!string.IsNullOrEmpty(host.NetBiosName)) existing.NetBiosName = host.NetBiosName;
                    if (host.IsPort80Open) existing.IsPort80Open = true;
                    if (host.IsPort443Open) existing.IsPort443Open = true;
                    if (host.IsPort8080Open) existing.IsPort8080Open = true;
                    if (host.IsPort502Open) existing.IsPort502Open = true;
                }
            }
        }

        private async Task RefreshAdaptersAsync()
        {
            try
            {
                var previousName = _selectedAdapter?.Name;
                var adapters = await _networkService.GetNetworkAdaptersAsync();
                var filtered = adapters
                    .Where(a => !a.Description.Contains("Tunneling", StringComparison.OrdinalIgnoreCase)
                             && !a.Description.Contains("WAN Miniport", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(a => a.IsConnected)
                    .ThenBy(a => a.Description)
                    .ToList();

                AvailableAdapters = new ObservableCollection<NetworkAdapter>(filtered);
                SelectedAdapter =
                    (previousName != null ? AvailableAdapters.FirstOrDefault(a => a.Name == previousName) : null)
                    ?? AvailableAdapters.FirstOrDefault(a => a.IsConnected && a.IpAddresses.Length > 0 && a.IpAddresses[0].StartsWith("192."))
                    ?? AvailableAdapters.FirstOrDefault(a => a.IsConnected)
                    ?? AvailableAdapters.FirstOrDefault();
                StatusMessage = $"Loaded {AvailableAdapters.Count} adapters";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Fejl ved læsning af adaptere: {ex.Message}";
            }
        }

        private void UpdateSubnetFromAdapter(NetworkAdapter adapter)
        {
            if (adapter.IpAddresses.Length == 0) return;

            var ip = adapter.IpAddresses[0];
            var parts = ip.Split('.');
            if (parts.Length != 4) return;

            SubnetInput = $"{parts[0]}.{parts[1]}.{parts[2]}";
            StartIp = 1;
            EndIp = 254;

            if (string.IsNullOrEmpty(adapter.SubnetMask) ||
                !IPAddress.TryParse(adapter.SubnetMask, out var maskAddr) ||
                !IPAddress.TryParse(ip, out var ipAddr))
                return;

            var maskBytes = maskAddr.GetAddressBytes();
            var ipBytes   = ipAddr.GetAddressBytes();

            int prefixLen = 0;
            foreach (var b in maskBytes)
            {
                var n = (int)b;
                while (n != 0) { prefixLen += n & 1; n >>= 1; }
            }

            int hostBits = 32 - prefixLen;

            if (prefixLen >= 24 && hostBits >= 2)
            {
                var netBase = (byte)(ipBytes[3] & maskBytes[3]);
                int hostCount = (1 << hostBits) - 2;
                StartIp = netBase + 1;
                EndIp   = netBase + hostCount;
            }
        }

        private async Task AutoDetectSubnetAsync()
        {
            try
            {
                if (SelectedAdapter != null && SelectedAdapter.IpAddresses.Length > 0)
                {
                    UpdateSubnetFromAdapter(SelectedAdapter);
                    StatusMessage = $"Subnet sat fra valgt adapter: {SelectedAdapter.Description} - {SubnetInput}.1-254";
                    return;
                }

                var adapters = await _networkService.GetNetworkAdaptersAsync();
                var active = adapters.FirstOrDefault(a =>
                    a.IsConnected &&
                    a.IpAddresses.Length > 0 &&
                    !a.IpAddresses[0].StartsWith("169.254") &&
                    !string.IsNullOrEmpty(a.Gateway) &&
                    a.Gateway != "0.0.0.0");

                if (active != null)
                {
                    var parts = active.IpAddresses[0].Split('.');
                    if (parts.Length == 4)
                    {
                        SubnetInput = $"{parts[0]}.{parts[1]}.{parts[2]}";
                        StartIp = 1;
                        EndIp = 254;
                        StatusMessage = $"Subnet detekteret fra {active.Description}: {SubnetInput}.1-254";
                    }
                }
                else
                {
                    StatusMessage = "Ingen aktiv adapter med gateway fundet";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Fejl: {ex.Message}";
            }
        }

        private static uint IpToUint(string ip)
        {
            if (System.Net.IPAddress.TryParse(ip, out var addr))
            {
                var b = addr.GetAddressBytes();
                return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
            }
            return 0;
        }

        private void SortHostsByIp()
        {
            var sorted = DiscoveredHosts.OrderBy(h => IpToUint(h.IpAddress)).ToList();
            for (int i = 0; i < sorted.Count; i++)
            {
                int current = DiscoveredHosts.IndexOf(sorted[i]);
                if (current != i)
                    DiscoveredHosts.Move(current, i);
            }
        }

        private async Task PingSingleAsync()
        {
            IsScanning = true;
            StatusMessage = $"Pinging {IpAddressInput}...";

            try
            {
                _scanCts = new CancellationTokenSource();
                var ct = _scanCts.Token;

                var srcIp = ScanSrcIp;
                var host = await _networkService.PingHostAsync(IpAddressInput, SelectedAdapter?.Name ?? string.Empty, srcIp, ct);
                if (host.IsReachable)
                {
                    var portResults = await Task.WhenAll(
                        _networkService.CheckPortAsync(host.IpAddress, 80, srcIp, 1000, ct),
                        _networkService.CheckPortAsync(host.IpAddress, 443, srcIp, 1000, ct),
                        _networkService.CheckPortAsync(host.IpAddress, 8080, srcIp, 1000, ct),
                        _networkService.CheckPortAsync(host.IpAddress, 502, srcIp, 1000, ct));
                    host.IsPort80Open   = portResults[0];
                    host.IsPort443Open  = portResults[1];
                    host.IsPort8080Open = portResults[2];
                    host.IsPort502Open  = portResults[3];

                    var mac = await _networkService.SendArpRequestAsync(host.IpAddress, srcIp, ct);
                    if (string.IsNullOrEmpty(mac))
                        mac = await _networkService.GetMacAddressAsync(host.IpAddress, ct);
                    if (!string.IsNullOrEmpty(mac))
                    {
                        host.MacAddress = mac;
                        if (!_ouiCache.TryGetValue(mac, out var vendor))
                        {
                            vendor = OuiLookup.Lookup(mac);
                            _ouiCache[mac] = vendor;
                        }
                        host.Vendor = vendor;
                    }
                }

                var existing = DiscoveredHosts.FirstOrDefault(h => h.IpAddress == host.IpAddress);
                if (existing != null)
                {
                    existing.HostName = host.HostName;
                    existing.ResponseTime = host.ResponseTime;
                    existing.Status = host.Status;
                    existing.LastSeen = host.LastSeen;
                    existing.IsReachable = host.IsReachable;
                    existing.IsPort80Open = host.IsPort80Open;
                    existing.IsPort443Open = host.IsPort443Open;
                    existing.IsPort8080Open = host.IsPort8080Open;
                    existing.IsPort502Open = host.IsPort502Open;
                    if (!string.IsNullOrEmpty(host.MacAddress)) existing.MacAddress = host.MacAddress;
                    if (!string.IsNullOrEmpty(host.Vendor)) existing.Vendor = host.Vendor;
                }
                else
                {
                    DiscoveredHosts.Add(host);
                    SortHostsByIp();
                }

                StatusMessage = host.IsReachable
                    ? $"{host.HostName} svarede på {host.ResponseTime}ms"
                    : $"{IpAddressInput} er offline";
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Ping annulleret.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
            }
            finally
            {
                _scanCts?.Dispose();
                _scanCts = null;
                IsScanning = false;
            }
        }

        private async Task ScanNetworkAsync(bool merge = false)
        {
            if (IsScanning) return;
            IsScanning = true;
            ScanProgress = 0;
            if (!merge)
            {
                DiscoveredHosts.Clear();
                _ouiCache.Clear();
            }
            StatusMessage = merge
                ? $"Auto-opdatering af {SubnetInput}.x..."
                : SelectedAdapter != null
                    ? $"Starter ping-scanning af {SubnetInput}.x på {SelectedAdapter.Description}..."
                    : $"Starter ping-scanning af {SubnetInput}.x...";

            _scanCts = new CancellationTokenSource();
            var ct = _scanCts.Token;
            _scanStartTime = DateTime.Now;

            // DispatcherTimer flushes _uiQueue to DiscoveredHosts every 100ms on UI thread
            bool flushing = false;
            var uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            uiTimer.Tick += (_, _) =>
            {
                if (flushing) return;
                flushing = true;
                try { FlushUiQueue(); }
                finally { flushing = false; }
            };
            uiTimer.Start();

            try
            {
                // Bind all scan traffic to the selected adapter's source IP.
                var srcIp = ScanSrcIp;

                // ===== FASE 0+1: ARP-flood + ping-sweep (samtidige) =====
                var floodTask = _networkService.FloodArpAsync(SubnetInput, StartIp, EndIp, ct);

                int totalTasks = EndIp - StartIp + 1;
                var onlineCount = 0;
                var reachableHosts = new ConcurrentBag<HostInfo>();
                var allIps = Enumerable.Range(StartIp, totalTasks)
                                       .Select(i => $"{SubnetInput}.{i}").ToList();

                if (!string.IsNullOrEmpty(srcIp))
                {
                    // Fast path: sweep the whole range on one bound raw socket.
                    StatusMessage = $"Ping-scanning af {totalTasks} IP'er...";
                    var sweep = await _networkService.PingSweepBoundAsync(allIps, srcIp, 800, ct);
                    onlineCount = sweep.Count;
                    foreach (var (ip, r) in sweep)
                    {
                        var host = new HostInfo
                        {
                            IpAddress = ip,
                            HostName = ip,
                            IsReachable = true,
                            Status = "Online",
                            ResponseTime = (int)r.rttMs,
                            AdapterName = SelectedAdapter?.Name ?? string.Empty,
                            OsGuess = r.ttl switch
                            {
                                > 0 and <= 64   => "Linux / Mac",
                                > 64 and <= 128 => "Windows",
                                > 128           => "Netværksenhed",
                                _               => string.Empty
                            },
                            LastSeen = DateTime.Now
                        };
                        reachableHosts.Add(host);
                        _uiQueue.Enqueue(host);
                    }
                    ScanProgress = 40;

                    // TCP-fallback for hosts der blokerer ICMP (routere, firewalls, IoT):
                    // prøv et par almindelige porte på de IP'er der ikke svarede på ping.
                    var noReply = allIps.Where(ip => !sweep.ContainsKey(ip)).ToList();
                    var tcpPorts = new[] { 80, 443, 445 };
                    using var tcpSem = new SemaphoreSlim(150);
                    var tcpTasks = noReply.Select(ip => Task.Run(async () =>
                    {
                        await tcpSem.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            foreach (var p in tcpPorts)
                            {
                                if (await _networkService.CheckPortAsync(ip, p, srcIp, 300, ct))
                                {
                                    var host = new HostInfo
                                    {
                                        IpAddress = ip,
                                        HostName = ip,
                                        IsReachable = true,
                                        Status = $"Online (TCP:{p})",
                                        AdapterName = SelectedAdapter?.Name ?? string.Empty,
                                        LastSeen = DateTime.Now
                                    };
                                    reachableHosts.Add(host);
                                    _uiQueue.Enqueue(host);
                                    Interlocked.Increment(ref onlineCount);
                                    break;
                                }
                            }
                        }
                        finally { tcpSem.Release(); }
                    }, ct)).ToList();
                    await Task.WhenAll(tcpTasks);

                    // Resolve hostnames (reverse-DNS/mDNS) for all online hosts — the sweep
                    // path builds HostInfo directly, so this step must be done explicitly.
                    StatusMessage = "Slår værtsnavne op...";
                    using var dnsSem = new SemaphoreSlim(100);
                    var dnsTasks = reachableHosts.Select(host => Task.Run(async () =>
                    {
                        await dnsSem.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            var name = await _networkService.ResolveHostNameAsync(host.IpAddress, 2000, ct);
                            if (!string.IsNullOrEmpty(name) && name != host.IpAddress)
                                host.HostName = name;
                        }
                        finally { dnsSem.Release(); }
                    }, ct)).ToList();
                    await Task.WhenAll(dnsTasks);

                    ScanProgress = 50;
                }
                else
                {
                    // Fallback path (no adapter/src IP): managed Ping per host, bounded.
                    var completedCount = 0;
                    using var semaphore = new SemaphoreSlim(150);
                    var pingTasks = allIps.Select(ip => Task.Run(async () =>
                    {
                        if (ct.IsCancellationRequested) return;
                        await semaphore.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            var host = await _networkService.PingHostAsync(ip, SelectedAdapter?.Name ?? string.Empty, ct);
                            var cnt = Interlocked.Increment(ref completedCount);
                            if (host.IsReachable)
                            {
                                host.Status = "Online";
                                Interlocked.Increment(ref onlineCount);
                                reachableHosts.Add(host);
                                _uiQueue.Enqueue(host);
                            }
                            var progress = 10 + (int)(40.0 * cnt / totalTasks);
                            _ = Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                ScanProgress = progress;
                                StatusMessage = $"Ping-scanning: {cnt}/{totalTasks} IP'er, {onlineCount} online";
                            });
                        }
                        finally { semaphore.Release(); }
                    }, ct)).ToList();

                    await Task.WhenAll(pingTasks);
                }

                await floodTask;

                FlushUiQueue(); // Flush resterende ping-resultater

                // ===== FASE 2: MAC via native ARP-tabel (instant) =====
                StatusMessage = $"Ping-fase færdig — {onlineCount} online. Henter MAC-adresser...";
                ScanProgress = 55;

                var onlineList = reachableHosts.ToList();

                // Read the ARP cache first (populated by the flood + ping sweep) — instant.
                var arpTable = _networkService.GetArpTableNative();

                // Resolve MAC per host: prefer a direct blocking SendARP (returns the MAC
                // even for slow devices like Shelly), fall back to the cache snapshot.
                using var arpSem = new SemaphoreSlim(100);
                var macTasks = onlineList.Select(async host =>
                {
                    await arpSem.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        var mac = await _networkService.SendArpRequestAsync(host.IpAddress, srcIp, ct);
                        if (string.IsNullOrEmpty(mac) &&
                            arpTable.TryGetValue(host.IpAddress, out var cachedMac))
                        {
                            mac = cachedMac;
                        }
                        return (host, mac);
                    }
                    finally { arpSem.Release(); }
                });

                foreach (var (host, mac) in await Task.WhenAll(macTasks))
                {
                    if (!string.IsNullOrEmpty(mac))
                    {
                        host.MacAddress = mac;
                        if (!_ouiCache.TryGetValue(mac, out var vendor))
                        {
                            vendor = OuiLookup.Lookup(mac);
                            _ouiCache[mac] = vendor;
                        }
                        host.Vendor = vendor;
                        _uiQueue.Enqueue(host);
                    }
                }

                FlushUiQueue();

                // ===== FASE 3+4: Port-tjek + NetBIOS (paralleliseret) =====
                StatusMessage = "MAC-adresser hentet. Tjekker porte og NetBIOS...";
                ScanProgress = 70;

                using var portSem = new SemaphoreSlim(150);
                var enrichmentTasks = onlineList.Select(async host =>
                {
                    var portResults = await Task.WhenAll(
                        CheckPortBounded(portSem, host.IpAddress, 80, srcIp, ct),
                        CheckPortBounded(portSem, host.IpAddress, 443, srcIp, ct),
                        CheckPortBounded(portSem, host.IpAddress, 8080, srcIp, ct),
                        CheckPortBounded(portSem, host.IpAddress, 502, srcIp, ct));
                    host.IsPort80Open   = portResults[0];
                    host.IsPort443Open  = portResults[1];
                    host.IsPort8080Open = portResults[2];
                    host.IsPort502Open  = portResults[3];

                    host.NetBiosName = await _networkService.GetNetBiosNameAsync(host.IpAddress, srcIp, ct);
                    await UpdateHostInUI(host);
                });
                await Task.WhenAll(enrichmentTasks);

                // ===== FASE 5: Endelig MAC-reconciliation =====
                // Efter alt netværkstrafik er Windows' ARP-cache fuldt populeret. Fyld MAC
                // ind for enhver host der stadig mangler den — inkl. services på en server
                // der deler hostens fysiske MAC (de har en MAC i cachen selvom SendARP fejlede).
                var finalArp = _networkService.GetArpTableNative();
                foreach (var host in onlineList)
                {
                    if (!string.IsNullOrEmpty(host.MacAddress)) continue;
                    if (finalArp.TryGetValue(host.IpAddress, out var mac) && !string.IsNullOrEmpty(mac))
                    {
                        host.MacAddress = mac;
                        if (!_ouiCache.TryGetValue(mac, out var vendor))
                        {
                            vendor = OuiLookup.Lookup(mac);
                            _ouiCache[mac] = vendor;
                        }
                        host.Vendor = vendor;
                        await UpdateHostInUI(host);
                    }
                }

                SortHostsByIp();
                ScanProgress = 100;
                var total = DiscoveredHosts.Count(h => h.IsReachable);
                var elapsed = DateTime.Now - _scanStartTime;
                ScanElapsedTime = $"{elapsed.TotalSeconds:F1}s";
                StatusMessage = $"Færdig — {total} online enheder fundet på {ScanElapsedTime}";
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Scanning annulleret.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
            }
            finally
            {
                uiTimer.Stop();
                FlushUiQueue();
                _scanCts?.Dispose();
                _scanCts = null;
                IsScanning = false;
            }
        }

        private async Task<bool> CheckPortBounded(SemaphoreSlim sem, string ip, int port, string srcIp, CancellationToken ct)
        {
            await sem.WaitAsync(ct).ConfigureAwait(false);
            try { return await _networkService.CheckPortAsync(ip, port, srcIp, 1000, ct); }
            finally { sem.Release(); }
        }

        private async Task UpdateHostInUI(HostInfo host)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var existing = DiscoveredHosts.FirstOrDefault(h => h.IpAddress == host.IpAddress);
                if (existing != null)
                {
                    existing.HostName = host.HostName;
                    existing.ResponseTime = host.ResponseTime;
                    existing.Status = host.Status;
                    existing.LastSeen = host.LastSeen;
                    existing.IsReachable = host.IsReachable;
                    existing.OsGuess = host.OsGuess;
                    existing.NetBiosName = host.NetBiosName;
                    existing.IsPort80Open = host.IsPort80Open;
                    existing.IsPort443Open = host.IsPort443Open;
                    existing.IsPort8080Open = host.IsPort8080Open;
                    existing.IsPort502Open = host.IsPort502Open;
                    if (!string.IsNullOrEmpty(host.MacAddress)) existing.MacAddress = host.MacAddress;
                    if (!string.IsNullOrEmpty(host.Vendor)) existing.Vendor = host.Vendor;
                }
                else
                {
                    DiscoveredHosts.Add(host);
                }
            });
        }
    }
}
