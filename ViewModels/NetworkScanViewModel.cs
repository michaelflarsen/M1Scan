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

        // VM'ens levetid. Alle scan-CTS'er linkes til denne, så Dispose() garanteret
        // stopper alt baggrundsarbejde — uanset hvilken fase scanningen er i.
        private readonly CancellationTokenSource _lifetime = new();

        // OUI-lookup cache per scan — holds (vendor, originalOui) tuples to avoid redundant lookups
        private readonly ConcurrentDictionary<string, (string vendor, string originalOui)> _ouiCache = new(StringComparer.OrdinalIgnoreCase);

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
            set
            {
                if (!SetProperty(ref _scanProgress, value)) return;

                // EstimatedTimeRemaining er en beregnet property uden egen setter —
                // uden denne notifikation blev den bundet én gang ved start og
                // opdaterede sig aldrig, så feltet stod tomt hele scanningen.
                OnPropertyChanged(nameof(EstimatedTimeRemaining));

                // Hold også "forløbet tid" levende under scanningen; den blev
                // tidligere først sat helt til sidst.
                if (IsScanning)
                    ScanElapsedTime = $"{(DateTime.UtcNow - _scanStartTime).TotalSeconds:F1}s";
            }
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

        public string EstimatedTimeRemaining
        {
            get
            {
                if (!IsScanning || DateTime.UtcNow <= _scanStartTime)
                    return string.Empty;

                var elapsed = DateTime.UtcNow - _scanStartTime;
                if (elapsed.TotalSeconds < 1 || ScanProgress <= 0)
                    return "...";

                var progressPercent = Math.Max(1, Math.Min(100, ScanProgress));
                var estimatedTotal = elapsed.TotalSeconds * (100.0 / progressPercent);
                var remaining = Math.Max(0, estimatedTotal - elapsed.TotalSeconds);

                var ts = TimeSpan.FromSeconds(remaining);
                return ts.Hours > 0
                    ? $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                    : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
            }
        }

        public AsyncRelayCommand PingSingleCommand { get; }
        public AsyncRelayCommand ScanNetworkCommand { get; }
        public AsyncRelayCommand AddScanCommand { get; }
        public RelayCommand CancelScanCommand { get; }
        public RelayCommand ClearResultsCommand { get; }
        public AsyncRelayCommand RefreshAdaptersCommand { get; }
        public AsyncRelayCommand AutoDetectSubnetCommand { get; }
        public RelayCommand ToggleAutoRefreshCommand { get; }
        public RelayCommand OpenInBrowserCommand { get; }
        public RelayCommand CopyIpCommand { get; }
        public RelayCommand CopyMacCommand { get; }
        public AsyncRelayCommand PingHostCommand { get; }
        public AsyncRelayCommand ExportCommand { get; }

        public NetworkScanViewModel(INetworkService networkService, IExportService exportService)
        {
            _networkService = networkService;
            _exportService = exportService;

            _autoRefreshTimer = new DispatcherTimer();

            PingSingleCommand = new AsyncRelayCommand(_ => PingSingleAsync(), _ => !IsScanning && !string.IsNullOrEmpty(IpAddressInput), OnCommandError);
            ScanNetworkCommand = new AsyncRelayCommand(_ => ScanNetworkAsync(), _ => !IsScanning, OnCommandError);
            AddScanCommand = new AsyncRelayCommand(_ => ScanNetworkAsync(merge: true), _ => !IsScanning, OnCommandError);
            // Abonneres FØRST når AddScanCommand findes. DispatcherTimer.Tick er en
            // async void-flade: en fejl her ville ellers ryge direkte til Dispatcher'en,
            // så tick'et går gennem kommandoen, som fanger og rapporterer den.
            _autoRefreshTimer.Tick += (_, _) => AddScanCommand.Execute(null);

            CancelScanCommand = new RelayCommand(_ => CancelScan(), _ => IsScanning);
            ClearResultsCommand = new RelayCommand(_ => ClearResults());
            RefreshAdaptersCommand = new AsyncRelayCommand(_ => RefreshAdaptersAsync(), onError: OnCommandError);
            AutoDetectSubnetCommand = new AsyncRelayCommand(_ => AutoDetectSubnetAsync(), _ => !IsScanning, OnCommandError);
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
            CopyMacCommand = new RelayCommand(param =>
            {
                if (param is string mac && !string.IsNullOrEmpty(mac))
                    System.Windows.Clipboard.SetText(mac);
            });
            PingHostCommand = new AsyncRelayCommand(
                param =>
                {
                    if (param is not string ip || string.IsNullOrEmpty(ip))
                        return Task.CompletedTask;
                    IpAddressInput = ip;
                    return PingSingleAsync();
                },
                _ => !IsScanning,
                OnCommandError);
            ExportCommand = new AsyncRelayCommand(
                _ => ExportAsync(),
                _ => DiscoveredHosts.Count > 0,
                OnCommandError);

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

        // Fejl fra en kommando ender her i stedet for at dræbe processen. Brugeren
        // ser årsagen i statuslinjen; detaljerne ligger i crash.log.
        private void OnCommandError(Exception ex) => StatusMessage = $"Fejl: {ex.Message}";

        // _scanCts må kun cancel'es hvis den stadig er levende — den disposes i
        // ScanNetworkAsync's finally, og et klik lige derefter ville ellers kaste.
        private void CancelScan()
        {
            try { _scanCts?.Cancel(); }
            catch (ObjectDisposedException) { /* scanningen sluttede lige */ }
        }

        private async void OnNetworkAddressChanged(object? sender, EventArgs e)
        {
            // async void event-handler: alt skal fanges her, ellers ryger fejlen
            // til AppDomain-niveau fra en pool-tråd.
            try
            {
                await Task.Delay(1500, _lifetime.Token); // let OS stabilise before re-enumerating
                if (_lifetime.IsCancellationRequested) return;

                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher != null)
                    await dispatcher.InvokeAsync(() => RefreshAdaptersCommand.Execute(null));
            }
            catch (OperationCanceledException) { /* VM'en er lukket ned */ }
            catch (Exception ex) { CrashLog.Write("OnNetworkAddressChanged", ex); }
        }

        public void Dispose()
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
            _autoRefreshTimer.Stop();

            // Afbryd en igangværende scanning FØR vi forsvinder — ellers kører ~150
            // tasks videre og skriver mod en Dispatcher der er ved at lukke.
            try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
            CancelScan();
            _lifetime.Dispose();
        }

        // Indeks over DiscoveredHosts (IP -> række). Uden det slog både FlushUiQueue og
        // UpdateHostInUI op med FirstOrDefault pr. element, altså O(n) inde i en løkke
        // over n elementer — O(n²) ved hvert flush, hver 100. ms under et scan.
        // Må kun berøres fra UI-tråden, ligesom DiscoveredHosts selv.
        private readonly Dictionary<string, HostInfo> _hostIndex = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Tilføjer eller fletter en host i den bundne liste. Kun UI-tråden.</summary>
        /// <param name="authoritative">Se <see cref="HostInfo.MergeFrom"/> — true ved et
        /// eksplicit gen-ping af én host, hvor "nu offline" skal slå igennem.</param>
        private void UpsertHost(HostInfo host, bool authoritative = false)
        {
            if (_hostIndex.TryGetValue(host.IpAddress, out var existing))
            {
                existing.MergeFrom(host, authoritative);
            }
            else
            {
                _hostIndex[host.IpAddress] = host;
                DiscoveredHosts.Add(host);
            }
        }

        // Flushes _uiQueue to DiscoveredHosts — must be called on UI thread.
        private void FlushUiQueue()
        {
            while (_uiQueue.TryDequeue(out var host))
                UpsertHost(host);
        }

        /// <summary>Rydder både listen og indekset — de skal aldrig komme ud af sync.</summary>
        private void ClearResults()
        {
            DiscoveredHosts.Clear();
            _hostIndex.Clear();
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

            // Selection-sort på plads med Move, men uden IndexOf pr. trin: IndexOf er
            // O(n) og gjorde sorteringen O(n²). Et positionsindeks holder styr på hvor
            // hver række aktuelt ligger, så hvert trin er O(1) opslag.
            var positions = new Dictionary<HostInfo, int>(DiscoveredHosts.Count);
            for (int i = 0; i < DiscoveredHosts.Count; i++)
                positions[DiscoveredHosts[i]] = i;

            for (int target = 0; target < sorted.Count; target++)
            {
                var host = sorted[target];
                int current = positions[host];
                if (current == target) continue;

                DiscoveredHosts.Move(current, target);

                // Move skubber alt mellem target og current én plads ned.
                for (int i = target; i <= current; i++)
                    positions[DiscoveredHosts[i]] = i;
            }
        }

        private async Task PingSingleAsync()
        {
            IsScanning = true;
            StatusMessage = $"Pinging {IpAddressInput}...";

            try
            {
                _scanCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
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
                        if (!_ouiCache.TryGetValue(mac, out var cached))
                        {
                            var (vendor, originalOui) = OuiLookup.LookupWithOriginal(mac);
                            cached = (vendor, originalOui);
                            _ouiCache[mac] = cached;
                        }
                        host.Vendor = cached.vendor;
                        host.OriginalVendor = cached.originalOui;
                    }
                }

                // Eksplicit gen-ping af netop denne host: resultatet er autoritativt,
                // så en host der er gået offline skal også vises som offline.
                UpsertHost(host, authoritative: true);

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
                ClearResults();
                _ouiCache.Clear();
            }
            StatusMessage = merge
                ? $"Auto-opdatering af {SubnetInput}.x..."
                : SelectedAdapter != null
                    ? $"Starter ping-scanning af {SubnetInput}.x på {SelectedAdapter.Description}..."
                    : $"Starter ping-scanning af {SubnetInput}.x...";

            _scanCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            var ct = _scanCts.Token;
            _scanStartTime = DateTime.UtcNow;

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

                // ===== FASE 0+1: ARP-flood + to-faset ping-sweep =====
                var floodTask = _networkService.FloodArpAsync(SubnetInput, StartIp, EndIp, ct);

                int totalTasks = EndIp - StartIp + 1;
                var onlineCount = 0;
                var allIps = Enumerable.Range(StartIp, totalTasks)
                                       .Select(i => $"{SubnetInput}.{i}").ToList();

                // Første sweep-fejl (hvis nogen). Uden denne blev et blokeret raw-socket
                // rapporteret som "Færdig — 0 online enheder fundet", altså en succes.
                string? sweepError = null;

                // Shared enrichment (MAC via ARP, derefter port-tjek + NetBIOS) — bruges for
                // både fase 1- og fase 2-hosts, så logikken ikke duplikeres.
                async Task EnrichHostsAsync(List<HostInfo> hosts, Dictionary<string, string> arpTable, string enrichSrcIp)
                {
                    if (hosts.Count == 0) return;

                    using var arpSem = new SemaphoreSlim(100);
                    var macTasks = hosts.Select(async host =>
                    {
                        await arpSem.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            var mac = await _networkService.SendArpRequestAsync(host.IpAddress, enrichSrcIp, ct);
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
                            if (!_ouiCache.TryGetValue(mac, out var cached))
                            {
                                var (vendor, originalOui) = OuiLookup.LookupWithOriginal(mac);
                                cached = (vendor, originalOui);
                                _ouiCache[mac] = cached;
                            }
                            host.Vendor = cached.vendor;
                            host.OriginalVendor = cached.originalOui;
                            _uiQueue.Enqueue(host);
                        }
                    }

                    FlushUiQueue();

                    using var portSem = new SemaphoreSlim(150);
                    var enrichmentTasks = hosts.Select(async host =>
                    {
                        var portResults = await Task.WhenAll(
                            CheckPortBounded(portSem, host.IpAddress, 80, enrichSrcIp, ct),
                            CheckPortBounded(portSem, host.IpAddress, 443, enrichSrcIp, ct),
                            CheckPortBounded(portSem, host.IpAddress, 8080, enrichSrcIp, ct),
                            CheckPortBounded(portSem, host.IpAddress, 502, enrichSrcIp, ct));

                        var netbios = await _networkService.GetNetBiosNameAsync(host.IpAddress, enrichSrcIp, ct);
                        await UpdateHostInUI(host, portResults, netbios);
                    });
                    await Task.WhenAll(enrichmentTasks);
                }

                // Resolve hostnames (reverse-DNS/mDNS) — sweep-baserede HostInfo har ikke
                // fået dette fra en managed Ping, så det gøres eksplicit.
                // Spørger enheden selv først (mDNS), derefter netværket (reverse-DNS).
                //
                // Rækkefølgen er bevidst: en PTR-record i routeren sættes af en
                // administrator og bliver forældet — på dette netværk pegede .4's
                // PTR på "seer.dk", som er en TJENESTE der kører på maskinen (port
                // 5055), mens maskinen selv hedder TechnoBunker (port 9000).
                // mDNS-navnet kommer fra maskinen i det øjeblik vi spørger.
                //
                // Enheder der ikke svarer på mDNS (fx de fleste Android-telefoner)
                // falder tilbage til reverse-DNS, hvor routerens DHCP-registrerede
                // navne stadig giver gode resultater.
                async Task<string> ResolveBestNameAsync(string hostIp, string nameSrcIp, int dnsTimeoutMs)
                {
                    // Begge opslag startes SAMTIDIGT og vi vælger bagefter. Kørte de i
                    // rækkefølge, ville hver enhed der ikke svarer på mDNS (fx de fleste
                    // Android-telefoner) betale hele mDNS-timeouten før reverse-DNS
                    // overhovedet gik i gang. De bruger hver sin protokol — multicast UDP
                    // og unicast DNS — så de konkurrerer ikke nævneværdigt.
                    var mdnsTask = _networkService.ResolveMdnsNameAsync(hostIp, nameSrcIp, 700, ct);
                    var dnsTask  = _networkService.ResolveHostNameAsync(hostIp, dnsTimeoutMs, ct);

                    await Task.WhenAll(mdnsTask, dnsTask);

                    var mdns = mdnsTask.Result;
                    if (!string.IsNullOrEmpty(mdns) && mdns != hostIp)
                        return mdns;

                    return dnsTask.Result;
                }

                async Task ResolveHostNamesAsync(IEnumerable<HostInfo> hosts)
                {
                    // 16 samtidige opslag, ikke 150. Reverse-DNS på et LAN besvares af
                    // routerens lille resolver (og af mDNS-responders på selve
                    // enhederne); 150 parallelle forespørgsler mætter den, så næsten
                    // alt løber ind i timeout. Målt på et /24 med 68 aktive hosts gav
                    // 150-vejs parallelitet stort set kun timeouts, mens de samme navne
                    // kunne slås op fint enkeltvis. Færre samtidige + længere timeout
                    // giver flere navne på samme samlede tid.
                    const int DnsConcurrency = 16;
                    const int DnsTimeoutMs = 1500;

                    using var dnsSem = new SemaphoreSlim(DnsConcurrency);
                    var dnsTasks = hosts.Select(host => Task.Run(async () =>
                    {
                        await dnsSem.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            var name = await ResolveBestNameAsync(host.IpAddress, srcIp, DnsTimeoutMs);
                            if (!string.IsNullOrEmpty(name) && name != host.IpAddress)
                            {
                                // HostInfo is bound to the grid — mutate it on the UI thread so
                                // the change notification actually refreshes the visible row.
                                await Application.Current.Dispatcher.InvokeAsync(() => host.HostName = name);
                            }
                        }
                        finally { dnsSem.Release(); }
                    }, ct)).ToList();
                    await Task.WhenAll(dnsTasks);
                }

                HostInfo BuildSweepHost(string ip, long rttMs, int ttl) => new()
                {
                    IpAddress = ip,
                    HostName = ip,
                    IsReachable = true,
                    Status = "Online",
                    ResponseTime = (int)rttMs,
                    AdapterName = SelectedAdapter?.Name ?? string.Empty,
                    OsGuess = ttl switch
                    {
                        > 0 and <= 64   => "Linux / Mac",
                        > 64 and <= 128 => "Windows",
                        > 128           => "Netværksenhed",
                        _               => string.Empty
                    },
                    LastSeen = DateTime.Now
                };

                // Fase 2: baggrunds-sweep (500ms) for IP'er der ikke svarede i fase 1 —
                // fanger langsomme ESP/IoT-enheder uden at forsinke fase 1's berigelse.
                async Task<List<HostInfo>> RunPhase2Async(List<string> targets, string phase2SrcIp, Dictionary<string, string> arpTable)
                {
                    var phase2Hosts = new List<HostInfo>();
                    if (targets.Count == 0) return phase2Hosts;

                    const int Phase2TimeoutMs = 3000;
                    var sweep2 = await _networkService.PingSweepBoundAsync(targets, phase2SrcIp, Phase2TimeoutMs, ct);
                    if (sweep2.Failed) sweepError ??= sweep2.Error;

                    foreach (var (ip, r) in sweep2.Hosts)
                    {
                        var host = BuildSweepHost(ip, r.rttMs, r.ttl);
                        phase2Hosts.Add(host);
                        _uiQueue.Enqueue(host);
                    }
                    Interlocked.Add(ref onlineCount, sweep2.Hosts.Count);

                    // TCP-fallback for hosts der stadig ikke svarer på ICMP efter begge faser.
                    var noReply2 = targets.Where(ip => !sweep2.Hosts.ContainsKey(ip)).ToList();
                    var tcpPorts = new[] { 80, 443, 445 };
                    var tcpFound = new ConcurrentBag<HostInfo>();
                    using var tcpSem = new SemaphoreSlim(150);
                    var tcpTasks = noReply2.Select(ip => Task.Run(async () =>
                    {
                        await tcpSem.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            foreach (var p in tcpPorts)
                            {
                                if (await _networkService.CheckPortAsync(ip, p, phase2SrcIp, 200, ct))
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
                                    tcpFound.Add(host);
                                    _uiQueue.Enqueue(host);
                                    Interlocked.Increment(ref onlineCount);
                                    break;
                                }
                            }
                        }
                        finally { tcpSem.Release(); }
                    }, ct)).ToList();
                    await Task.WhenAll(tcpTasks);
                    phase2Hosts.AddRange(tcpFound);

                    await ResolveHostNamesAsync(phase2Hosts);
                    await EnrichHostsAsync(phase2Hosts, arpTable, phase2SrcIp);

                    return phase2Hosts;
                }

                List<HostInfo> onlineList;

                if (!string.IsNullOrEmpty(srcIp))
                {
                    // ---- Fase 1: hurtig sweep (100ms) — finder normale enheder næsten instant ----
                    const int Phase1TimeoutMs = 100;
                    StatusMessage = $"Hurtig ping-scanning af {totalTasks} IP'er...";
                    var sweep1 = await _networkService.PingSweepBoundAsync(allIps, srcIp, Phase1TimeoutMs, ct);
                    if (sweep1.Failed) sweepError ??= sweep1.Error;

                    onlineCount = sweep1.Hosts.Count;
                    var phase1Hosts = new List<HostInfo>();
                    foreach (var (ip, r) in sweep1.Hosts)
                    {
                        var host = BuildSweepHost(ip, r.rttMs, r.ttl);
                        phase1Hosts.Add(host);
                        _uiQueue.Enqueue(host);
                    }
                    ScanProgress = 25;

                    // Non-respondenter fra fase 1 bliver fase 2's mål. Kun ægte echo-replies
                    // (ICMP type 0) havner i sweep1 (se IcmpSweepBound) — "destination
                    // unreachable" og andre ICMP-fejl er allerede filtreret fra, så de korrekt
                    // falder igennem til fase 2 sammen med reelle timeouts.
                    var noReply1 = allIps.Where(ip => !sweep1.Hosts.ContainsKey(ip)).ToList();

                    // ARP-cachen læses én gang (fallback-kilde kun — primær MAC-opløsning er
                    // det blokerende SendArpRequestAsync pr. host) og deles af begge faser.
                    var arpTable = _networkService.GetArpTableNative();

                    // Start fase 2 nu (uden at afvente den), så den kører samtidig med fase 1's
                    // DNS-opslag og berigelse nedenfor.
                    var phase2Task = RunPhase2Async(noReply1, srcIp, arpTable);
                    var phase2Hosts = new List<HostInfo>();

                    try
                    {
                        StatusMessage = "Slår værtsnavne op...";
                        await ResolveHostNamesAsync(phase1Hosts);
                        ScanProgress = 35;

                        await floodTask;
                        FlushUiQueue();

                        StatusMessage = $"Ping-fase færdig — {onlineCount} online indtil videre. Henter MAC-adresser... (baggrundsscan for langsomme enheder kører)";
                        await EnrichHostsAsync(phase1Hosts, arpTable, srcIp);
                        ScanProgress = 70;
                    }
                    finally
                    {
                        // Uanset om ovenstående blev annulleret eller fejlede, skal fase 2
                        // altid joines her — ellers kører den videre løsrevet fra scannets
                        // livscyklus og kan skrive forældede resultater ind i en senere scanning.
                        try { phase2Hosts = await phase2Task; }
                        catch (OperationCanceledException) { /* forventet ved annullering */ }
                    }

                    onlineList = phase1Hosts.Concat(phase2Hosts).ToList();
                    onlineCount = onlineList.Count;
                    ScanProgress = 90;
                }
                else
                {
                    // Fallback path (no adapter/src IP): managed Ping per host, bounded.
                    // Efterlades enkelt-faset — den looper allerede pr. host med egen timeout,
                    // og to-fasning ville blot fordoble ping-trafikken uden reel gevinst.
                    var reachableHosts = new ConcurrentBag<HostInfo>();
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
                    onlineList = reachableHosts.ToList();

                    await floodTask;
                    FlushUiQueue();

                    StatusMessage = $"Ping-fase færdig — {onlineCount} online. Henter MAC-adresser...";
                    ScanProgress = 55;
                    var arpTable = _networkService.GetArpTableNative();
                    await EnrichHostsAsync(onlineList, arpTable, srcIp);
                    ScanProgress = 70;
                }

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
                        if (!_ouiCache.TryGetValue(mac, out var cached))
                        {
                            var (vendor, originalOui) = OuiLookup.LookupWithOriginal(mac);
                            cached = (vendor, originalOui);
                            _ouiCache[mac] = cached;
                        }
                        host.Vendor = cached.vendor;
                        host.OriginalVendor = cached.originalOui;
                        await UpdateHostInUI(host);
                    }
                }

                SortHostsByIp();
                ScanProgress = 100;
                var total = DiscoveredHosts.Count(h => h.IsReachable);
                var elapsed = DateTime.UtcNow - _scanStartTime;
                ScanElapsedTime = $"{elapsed.TotalSeconds:F1}s";

                // Fejlede ping-sweep'et, må resultatet IKKE præsenteres som en færdig
                // scanning — "0 online" ville da betyde "vi kunne ikke spørge", ikke
                // "der er ingen enheder". TCP-fallbacket kan stadig have fundet noget,
                // så vis begge dele.
                StatusMessage = sweepError is null
                    ? $"Færdig — {total} online enheder fundet på {ScanElapsedTime}"
                    : $"Ufuldstændig scanning: {sweepError} — kun {total} enheder fundet via TCP-fallback.";
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

        private async Task UpdateHostInUI(HostInfo host, bool[]? portResults = null, string? netbios = null)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null) return; // appen lukker ned

            await dispatcher.InvokeAsync(() =>
            {
                // Portresultater og NetBIOS-navn hører til DENNE observation, så de
                // skrives på host'en før fletningen — så gælder de samme merge-regler
                // for dem som for alt andet (se HostInfo.MergeFrom).
                if (!string.IsNullOrEmpty(netbios)) host.NetBiosName = netbios;
                if (portResults != null)
                {
                    host.IsPort80Open   = portResults[0];
                    host.IsPort443Open  = portResults[1];
                    host.IsPort8080Open = portResults[2];
                    host.IsPort502Open  = portResults[3];
                }

                UpsertHost(host);
            });
        }
    }
}
