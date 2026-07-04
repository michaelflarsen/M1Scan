using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using M1Scan.Models;
using M1Scan.Services;
using M1Scan.Utils;

namespace M1Scan.ViewModels
{
    public class AdapterDisplay : ObservableObject
    {
        public string Name        { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string IpAddress   { get; init; } = string.Empty;
        public string SubnetMask  { get; init; } = string.Empty;
        public string Gateway     { get; init; } = string.Empty;
        public bool   IsUp        { get; init; }
        public long   SpeedBitsPerSec { get; init; } = -1;

        public string SpeedText => SpeedBitsPerSec <= 0
            ? "—"
            : SpeedBitsPerSec >= 1_000_000_000
                ? $"{SpeedBitsPerSec / 1_000_000_000d:0.#} Gbit/s"
                : $"{SpeedBitsPerSec / 1_000_000} Mbit/s";

        private bool _isDefaultRoute;
        public bool IsDefaultRoute
        {
            get => _isDefaultRoute;
            set => SetProperty(ref _isDefaultRoute, value);
        }
    }

    public class InternetCheckResult : ObservableObject
    {
        private bool _isOnline;
        private int  _latencyMs;
        private bool _isChecking = true;

        public string Host  { get; init; } = string.Empty;
        public string Label { get; init; } = string.Empty;

        public bool IsOnline
        {
            get => _isOnline;
            set { if (SetProperty(ref _isOnline, value)) OnPropertyChanged(nameof(LatencyDisplay)); }
        }

        public int LatencyMs
        {
            get => _latencyMs;
            set { if (SetProperty(ref _latencyMs, value)) OnPropertyChanged(nameof(LatencyDisplay)); }
        }

        public bool IsChecking
        {
            get => _isChecking;
            set { if (SetProperty(ref _isChecking, value)) OnPropertyChanged(nameof(LatencyDisplay)); }
        }

        public string LatencyDisplay =>
            IsChecking ? "..." : IsOnline ? $"{LatencyMs} ms" : "—";
    }

    public class ArpDeviceEntry : ObservableObject
    {
        public string IpAddress  { get; init; } = string.Empty;
        public string MacAddress { get; init; } = string.Empty;
        public string Vendor     { get; init; } = string.Empty;
        public DateTimeOffset? FirstSeen { get; init; }

        private bool _isNew;
        public bool IsNew
        {
            get => _isNew;
            set => SetProperty(ref _isNew, value);
        }

        public string FirstSeenDisplay =>
            FirstSeen.HasValue ? $"Først set: {FirstSeen.Value:dd-MM-yyyy HH:mm}" : string.Empty;
    }

    public class ArpSubnetGroup : ObservableObject
    {
        public string               Subnet      { get; init; } = string.Empty;
        public string               Display     { get; init; } = string.Empty;
        public string               AdapterName { get; init; } = string.Empty;
        public List<ArpDeviceEntry> Devices     { get; init; } = new();

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetProperty(ref _isExpanded, value);
        }

        private int _newCount;
        public int NewCount
        {
            get => _newCount;
            set => SetProperty(ref _newCount, value);
        }

        public RelayCommand ToggleCommand { get; }

        public ArpSubnetGroup()
        {
            ToggleCommand = new RelayCommand(_ => IsExpanded = !IsExpanded);
        }
    }

    public class WanInfo : ObservableObject
    {
        private bool   _isLoading = true;
        private string _ip        = "Henter...";
        private string _isp       = string.Empty;
        private string _asn       = string.Empty;
        private string _country   = string.Empty;

        public bool   IsLoading { get => _isLoading; set => SetProperty(ref _isLoading, value); }
        public string Ip        { get => _ip;        set => SetProperty(ref _ip,        value); }
        public string Isp       { get => _isp;       set => SetProperty(ref _isp,       value); }
        public string Asn       { get => _asn;       set => SetProperty(ref _asn,       value); }
        public string Country   { get => _country;   set => SetProperty(ref _country,   value); }
    }

    public class HomeViewModel : ObservableObject, IDisposable
    {
        private const int NetworkChangeDebounceMs = 1500;
        private const string WanProbeHost = "1.1.1.1";

        private static readonly (string Host, string Label)[] InternetHosts =
        {
            ("8.8.8.8",   "Google DNS"),
            ("1.1.1.1",   "Cloudflare DNS"),
            ("8.8.4.4",   "Google DNS (alt)")
        };

        private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(5) };

        private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = true };

        private static readonly string DashboardDataPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "M1Scan", "dashboard.json");

        private static readonly string UiSettingsPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "M1Scan", "ui_settings.json");

        private readonly INetworkService     _networkService;
        private readonly IDiagnosticsService _diagnosticsService;
        private readonly KnownDevicesStore   _knownDevices;
        private readonly SemaphoreSlim       _loadLock = new SemaphoreSlim(1, 1);
        private readonly DispatcherTimer     _sampleTimer;

        private int _sampleInFlight;
        private int _diagInFlight;
        private volatile string?   _samplerGatewayIp;
        private string[]           _diagDnsServers  = Array.Empty<string>();
        private string             _diagAdapterName = string.Empty;
        private double?            _fastestDnsMs;
        private CancellationTokenSource? _speedCts;

        private ObservableCollection<AdapterDisplay>      _activeAdapters = new();
        private ObservableCollection<InternetCheckResult> _internetChecks = new();
        private ObservableCollection<ArpSubnetGroup>      _nearbyGroups   = new();
        private bool   _isRefreshing;
        private bool   _isOnline;
        private string _lastRefreshed = "—";
        private int    _totalNearby;

        public ObservableCollection<AdapterDisplay> ActiveAdapters
        {
            get => _activeAdapters;
            set => SetProperty(ref _activeAdapters, value);
        }

        public ObservableCollection<InternetCheckResult> InternetChecks
        {
            get => _internetChecks;
            set => SetProperty(ref _internetChecks, value);
        }

        public ObservableCollection<ArpSubnetGroup> NearbyGroups
        {
            get => _nearbyGroups;
            set => SetProperty(ref _nearbyGroups, value);
        }

        public bool IsRefreshing
        {
            get => _isRefreshing;
            set => SetProperty(ref _isRefreshing, value);
        }

        public bool IsOnline
        {
            get => _isOnline;
            set => SetProperty(ref _isOnline, value);
        }

        public string LastRefreshed
        {
            get => _lastRefreshed;
            set => SetProperty(ref _lastRefreshed, value);
        }

        public int TotalNearby
        {
            get => _totalNearby;
            set => SetProperty(ref _totalNearby, value);
        }

        private string _internetVia = "—";
        public string InternetVia
        {
            get => _internetVia;
            set => SetProperty(ref _internetVia, value);
        }

        private InternetCheckResult? _gatewayCheck;
        public InternetCheckResult? GatewayCheck
        {
            get => _gatewayCheck;
            set
            {
                if (SetProperty(ref _gatewayCheck, value))
                    OnPropertyChanged(nameof(HasGateway));
            }
        }

        public bool HasGateway => GatewayCheck != null;

        public WanInfo Wan { get; } = new();

        private AdapterDisplay? _defaultAdapter;
        public AdapterDisplay? DefaultAdapter
        {
            get => _defaultAdapter;
            set => SetProperty(ref _defaultAdapter, value);
        }

        // ── Forbindelseskvalitet (live sampler) ──────────────────────────────

        public LatencySeries GatewaySeries { get; } = new() { Label = "Gateway" };
        public LatencySeries WanSeries     { get; } = new() { Label = "Internet" };

        private string _gatewaySeriesTitle = "GATEWAY";
        public string GatewaySeriesTitle
        {
            get => _gatewaySeriesTitle;
            set => SetProperty(ref _gatewaySeriesTitle, value);
        }

        public string WanSeriesTitle => $"INTERNET · {WanProbeHost}";

        // ── Sundhedsscore ────────────────────────────────────────────────────

        private HealthScore _health = HealthScore.Measuring;
        public HealthScore Health
        {
            get => _health;
            set
            {
                if (SetProperty(ref _health, value))
                {
                    OnPropertyChanged(nameof(HealthSubline));
                    OnPropertyChanged(nameof(HeaderScoreText));
                }
            }
        }

        public string HealthSubline
        {
            get
            {
                if (WanSeries.SampleCount == 0) return "Venter på målinger...";
                var dns = _fastestDnsMs.HasValue ? $"{_fastestDnsMs.Value:F0} ms" : "—";
                return $"Tab {WanSeries.LossPercent:F0}% · Jitter {WanSeries.JitterMs:F1} ms · DNS {dns}";
            }
        }

        public string HeaderScoreText => Health.IsValid ? $"{Health.Score} · {Health.Grade}" : "—";

        // ── Nye enheder ──────────────────────────────────────────────────────

        private int _newDeviceCount;
        public int NewDeviceCount
        {
            get => _newDeviceCount;
            set { if (SetProperty(ref _newDeviceCount, value)) OnPropertyChanged(nameof(HasNewDevices)); }
        }

        public bool HasNewDevices => NewDeviceCount > 0;

        // ── Diagnostik ───────────────────────────────────────────────────────

        private ObservableCollection<DnsTimingResult> _dnsResults = new();
        public ObservableCollection<DnsTimingResult> DnsResults
        {
            get => _dnsResults;
            set => SetProperty(ref _dnsResults, value);
        }

        private bool _dhcpIsDhcp;
        public bool DhcpIsDhcp
        {
            get => _dhcpIsDhcp;
            set => SetProperty(ref _dhcpIsDhcp, value);
        }

        private string _dhcpServerText = "—";
        public string DhcpServerText
        {
            get => _dhcpServerText;
            set => SetProperty(ref _dhcpServerText, value);
        }

        private string _dhcpObtainedText = "—";
        public string DhcpObtainedText
        {
            get => _dhcpObtainedText;
            set => SetProperty(ref _dhcpObtainedText, value);
        }

        private string _dhcpExpiresText = "—";
        public string DhcpExpiresText
        {
            get => _dhcpExpiresText;
            set => SetProperty(ref _dhcpExpiresText, value);
        }

        private string _ipv6Text = "Måler...";
        public string Ipv6Text
        {
            get => _ipv6Text;
            set => SetProperty(ref _ipv6Text, value);
        }

        private bool _ipv6Ok;
        public bool Ipv6Ok
        {
            get => _ipv6Ok;
            set => SetProperty(ref _ipv6Ok, value);
        }

        private string _portalText = "Måler...";
        public string PortalText
        {
            get => _portalText;
            set => SetProperty(ref _portalText, value);
        }

        private bool _portalWarning;
        public bool PortalWarning
        {
            get => _portalWarning;
            set => SetProperty(ref _portalWarning, value);
        }

        // ── Hastighedstest ───────────────────────────────────────────────────

        private bool _isSpeedTesting;
        public bool IsSpeedTesting
        {
            get => _isSpeedTesting;
            set { if (SetProperty(ref _isSpeedTesting, value)) OnPropertyChanged(nameof(SpeedTestButtonText)); }
        }

        public string SpeedTestButtonText => IsSpeedTesting ? "Annuller" : "Test hastighed";

        private double _speedTestProgressPercent;
        public double SpeedTestProgressPercent
        {
            get => _speedTestProgressPercent;
            set => SetProperty(ref _speedTestProgressPercent, value);
        }

        private string _speedTestPhaseText = string.Empty;
        public string SpeedTestPhaseText
        {
            get => _speedTestPhaseText;
            set => SetProperty(ref _speedTestPhaseText, value);
        }

        private SpeedTestResult? _lastSpeedTest;
        public SpeedTestResult? LastSpeedTest
        {
            get => _lastSpeedTest;
            set
            {
                if (SetProperty(ref _lastSpeedTest, value))
                {
                    OnPropertyChanged(nameof(HasSpeedTestResult));
                    OnPropertyChanged(nameof(SpeedTestDownText));
                    OnPropertyChanged(nameof(SpeedTestUpText));
                    OnPropertyChanged(nameof(SpeedTestTimeText));
                }
            }
        }

        public bool   HasSpeedTestResult => LastSpeedTest != null;
        public string SpeedTestDownText  => LastSpeedTest != null ? $"↓ {LastSpeedTest.downloadMbps:F0} Mbit/s" : "";
        public string SpeedTestUpText    => LastSpeedTest != null ? $"↑ {LastSpeedTest.uploadMbps:F0} Mbit/s (est.)" : "";
        public string SpeedTestTimeText  => LastSpeedTest != null ? $"Senest testet: {LastSpeedTest.timestamp:dd-MM-yyyy HH:mm}" : "";

        // ── Graph-toggle ─────────────────────────────────────────────────────

        private bool _graphsVisible = false;
        public bool GraphsVisible
        {
            get => _graphsVisible;
            set { if (SetProperty(ref _graphsVisible, value)) SaveUiSettings(); }
        }

        private bool _diagnosticsVisible = false;
        public bool DiagnosticsVisible
        {
            get => _diagnosticsVisible;
            set { if (SetProperty(ref _diagnosticsVisible, value)) SaveUiSettings(); }
        }

        // ── Kommandoer ───────────────────────────────────────────────────────

        public RelayCommand RefreshCommand           { get; }
        public RelayCommand TestSpeedCommand         { get; }
        public RelayCommand AcknowledgeDeviceCommand { get; }
        public RelayCommand AcknowledgeAllCommand    { get; }
        public RelayCommand ToggleGraphsCommand      { get; }
        public RelayCommand ToggleDiagnosticsCommand { get; }
        public RelayCommand ResetScoreCommand        { get; }

        public HomeViewModel(INetworkService networkService, IDiagnosticsService diagnosticsService)
        {
            _networkService     = networkService;
            _diagnosticsService = diagnosticsService;
            _knownDevices       = new KnownDevicesStore();

            foreach (var (host, label) in InternetHosts)
                InternetChecks.Add(new InternetCheckResult { Host = host, Label = label });

            NetworkChange.NetworkAddressChanged += OnNetworkChanged;

            ToggleGraphsCommand      = new RelayCommand(_ => GraphsVisible      = !GraphsVisible);
            ToggleDiagnosticsCommand = new RelayCommand(_ => DiagnosticsVisible = !DiagnosticsVisible);
            ResetScoreCommand = new RelayCommand(_ =>
            {
                GatewaySeries.Reset();
                WanSeries.Reset();
                _fastestDnsMs = null;
                Health = HealthScore.Measuring;
                OnPropertyChanged(nameof(HealthSubline));
                OnPropertyChanged(nameof(HeaderScoreText));
                _ = RunDiagnosticsAsync(Application.Current.Dispatcher);
            });
            RefreshCommand = new RelayCommand(_ => _ = LoadAsync());

            TestSpeedCommand = new RelayCommand(_ =>
            {
                if (IsSpeedTesting) _speedCts?.Cancel();
                else _ = RunSpeedTestAsync();
            });

            AcknowledgeDeviceCommand = new RelayCommand(p =>
            {
                if (p is not ArpDeviceEntry e || !e.IsNew) return;
                _knownDevices.Acknowledge(e.MacAddress);
                _knownDevices.Save();
                e.IsNew = false;
                RecountNewDevices();
            });

            AcknowledgeAllCommand = new RelayCommand(_ =>
            {
                _knownDevices.AcknowledgeAll();
                _knownDevices.Save();
                foreach (var g in NearbyGroups)
                    foreach (var d in g.Devices)
                        d.IsNew = false;
                RecountNewDevices();
            }, _ => HasNewDevices);

            _sampleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _sampleTimer.Tick += OnSampleTick;

            LoadUiSettings();
            LoadDashboardData();
            _ = LoadAsync();
        }

        public void Dispose()
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            _sampleTimer.Stop();
            _sampleTimer.Tick -= OnSampleTick;
            _speedCts?.Cancel();
            _loadLock.Dispose();
        }

        /// <summary>Kaldes fra HomeView ved IsVisibleChanged — styrer sampler-timeren.</summary>
        public void SetDashboardVisible(bool visible)
        {
            if (visible)
            {
                _sampleTimer.Start();
                _ = SampleOnceAsync();
            }
            else
            {
                _sampleTimer.Stop();
            }
        }

        private void OnSampleTick(object? sender, EventArgs e) => _ = SampleOnceAsync();

        private async void OnNetworkChanged(object? sender, EventArgs e)
        {
            await Task.Delay(NetworkChangeDebounceMs);
            try
            {
                if (Application.Current != null)
                    await LoadAsync();
            }
            catch { }
        }

        // ── Live latency-sampler ─────────────────────────────────────────────

        private async Task SampleOnceAsync()
        {
            // Ingen overlap — et langsomt tick må aldrig stable sig op
            if (Interlocked.CompareExchange(ref _sampleInFlight, 1, 0) != 0) return;
            try
            {
                var gwIp = _samplerGatewayIp;
                var wanTask = PingOnceAsync(WanProbeHost);
                var gwTask  = gwIp != null ? PingOnceAsync(gwIp) : Task.FromResult<double?>(null);

                double? wan = await wanTask;
                double? gw  = await gwTask;

                // Tick kører på UI-tråden og awaits genoptager dér — serierne er UI-bundne
                WanSeries.Add(wan);
                if (gwIp != null) GatewaySeries.Add(gw);

                RecomputeHealth();
            }
            catch { /* enkeltstående sample-fejl ignoreres */ }
            finally
            {
                Interlocked.Exchange(ref _sampleInFlight, 0);
            }
        }

        private static async Task<double?> PingOnceAsync(string host)
        {
            try
            {
                using var ping  = new Ping();
                var       reply = await ping.SendPingAsync(host, 1500);
                return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
            }
            catch
            {
                return null;
            }
        }

        private void RecomputeHealth()
        {
            Health = HealthScore.Compute(
                WanSeries,
                _samplerGatewayIp != null ? GatewaySeries : null,
                _fastestDnsMs);
            OnPropertyChanged(nameof(HealthSubline));
        }

        // ── Hovedindlæsning ──────────────────────────────────────────────────

        public async Task LoadAsync()
        {
            // FIX #1: SemaphoreSlim(1,1) sikrer atomisk adgang — ingen race condition
            if (!await _loadLock.WaitAsync(0)) return;

            // FIX #3: dispatcher fanges én gang — null-safe gennem hele metoden
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) { _loadLock.Release(); return; }

            try
            {
                // FIX #4: alle UI-mutationer via captured dispatcher
                await dispatcher.InvokeAsync(() =>
                {
                    IsRefreshing = true;
                    foreach (var c in InternetChecks) c.IsChecking = true;
                    if (GatewayCheck != null) GatewayCheck.IsChecking = true;
                    Wan.IsLoading = true;
                    Wan.Ip        = "Henter...";
                });

                var adaptersTask = _networkService.GetNetworkAdaptersAsync();
                var arpTask      = _networkService.GetArpTableAsync();
                _ = FetchWanInfoAsync(dispatcher);

                var pingTasks = InternetHosts.Select(async h =>
                {
                    var result = InternetChecks.First(c => c.Host == h.Host);
                    try
                    {
                        using var ping  = new Ping();
                        var       reply = await ping.SendPingAsync(h.Host, 3000);
                        bool ok = reply.Status == IPStatus.Success;
                        var d = Application.Current?.Dispatcher;
                        if (d != null) await d.InvokeAsync(() =>
                        {
                            result.IsOnline   = ok;
                            result.LatencyMs  = ok ? (int)reply.RoundtripTime : 0;
                            result.IsChecking = false;
                        });
                    }
                    catch
                    {
                        var d = Application.Current?.Dispatcher;
                        if (d != null) await d.InvokeAsync(() =>
                        {
                            result.IsOnline   = false;
                            result.LatencyMs  = 0;
                            result.IsChecking = false;
                        });
                    }
                }).ToList();

                var adapters = await adaptersTask;
                var displays = adapters
                    .Where(a => a.IsConnected && a.IpAddresses.Length > 0)
                    .Select(a => new AdapterDisplay
                    {
                        Name        = a.Name,
                        Description = a.Description,
                        IpAddress   = a.IpAddresses[0],
                        SubnetMask  = a.SubnetMask  ?? "—",
                        Gateway     = a.Gateway     ?? "—",
                        IsUp        = a.IsConnected,
                        SpeedBitsPerSec = a.SpeedBitsPerSec
                    }).ToList();

                var best = displays.FirstOrDefault(a => a.Gateway != "—" && !string.IsNullOrEmpty(a.Gateway));
                if (best != null)
                {
                    best.IsDefaultRoute = true;
                    displays.Remove(best);
                    displays.Insert(0, best);
                }

                // IPv6 link-local gateways (fe80::...) kan ikke pinges pålideligt — brug kun IPv4
                string? gatewayIp = (best?.Gateway != null && !best.Gateway.Contains(':'))
                    ? best.Gateway : null;

                // Giv sampleren og diagnostikken deres mål
                _samplerGatewayIp = gatewayIp;
                _diagAdapterName  = best?.Name ?? string.Empty;
                _diagDnsServers   = best != null
                    ? adapters.FirstOrDefault(a => a.Name == best.Name)?.DnsServers ?? Array.Empty<string>()
                    : Array.Empty<string>();

                Task gatewayPingTask = Task.CompletedTask;
                await dispatcher.InvokeAsync(() =>
                {
                    ActiveAdapters = new ObservableCollection<AdapterDisplay>(displays);
                    InternetVia    = best != null ? $"{best.Name}  ({best.IpAddress})" : "—";
                    GatewayCheck   = !string.IsNullOrEmpty(gatewayIp)
                        ? new InternetCheckResult { Host = gatewayIp!, Label = "Gateway" }
                        : null;
                    DefaultAdapter = best;
                    GatewaySeriesTitle = gatewayIp != null ? $"GATEWAY · {gatewayIp}" : "GATEWAY";
                });

                var gwResult = GatewayCheck;
                if (gwResult != null)
                {
                    var capturedGw = gwResult;
                    var capturedIp = gatewayIp!;
                    gatewayPingTask = Task.Run(async () =>
                    {
                        try
                        {
                            using var ping  = new Ping();
                            var       reply = await ping.SendPingAsync(capturedIp, 3000);
                            bool ok = reply.Status == IPStatus.Success;
                            var d = Application.Current?.Dispatcher;
                            if (d != null) await d.InvokeAsync(() =>
                            {
                                capturedGw.IsOnline   = ok;
                                capturedGw.LatencyMs  = ok ? (int)reply.RoundtripTime : 0;
                                capturedGw.IsChecking = false;
                            });
                        }
                        catch
                        {
                            var d = Application.Current?.Dispatcher;
                            if (d != null) await d.InvokeAsync(() =>
                            {
                                capturedGw.IsOnline   = false;
                                capturedGw.LatencyMs  = 0;
                                capturedGw.IsChecking = false;
                            });
                        }
                    });
                }

                var arp = await arpTask;

                // Ny enhed-detektion: første kørsel seeder hele baseline som kendt,
                // så eksisterende enheder ikke alle markeres NYE
                bool seedAsKnown = !_knownDevices.FileExisted;

                var arpGroups = arp
                    .GroupBy(kv =>
                    {
                        var p = kv.Key.Split('.');
                        return p.Length >= 3 ? $"{p[0]}.{p[1]}.{p[2]}" : kv.Key;
                    })
                    .OrderBy(g => IpSortKey(g.Key + ".0"))
                    .Select(g =>
                    {
                        var pfx          = g.Key;
                        var matchedAdapter = displays.FirstOrDefault(a =>
                        {
                            var p = a.IpAddress.Split('.');
                            return p.Length >= 3 && $"{p[0]}.{p[1]}.{p[2]}" == pfx;
                        });
                        var group = new ArpSubnetGroup
                        {
                            Subnet      = g.Key,
                            Display     = g.Key + ".0 /24",
                            AdapterName = matchedAdapter?.Description ?? string.Empty,
                            Devices     = g.OrderBy(kv => IpSortKey(kv.Key))
                                           .Select(kv =>
                                           {
                                               var vendor = OuiLookup.Lookup(kv.Value) ?? string.Empty;
                                               bool isNew = false;
                                               DateTimeOffset? firstSeen = null;
                                               if (KnownDevicesStore.IsRealDeviceMac(kv.Value))
                                               {
                                                   var dev = _knownDevices.Observe(kv.Value, kv.Key, vendor, seedAsKnown);
                                                   isNew     = !dev.acknowledged;
                                                   firstSeen = dev.firstSeen;
                                               }
                                               return new ArpDeviceEntry
                                               {
                                                   IpAddress  = kv.Key,
                                                   MacAddress = kv.Value,
                                                   Vendor     = vendor,
                                                   IsNew      = isNew,
                                                   FirstSeen  = firstSeen
                                               };
                                           }).ToList()
                        };
                        group.NewCount = group.Devices.Count(d => d.IsNew);
                        return group;
                    }).ToList();

                _knownDevices.Save();

                await dispatcher.InvokeAsync(() =>
                {
                    NearbyGroups   = new ObservableCollection<ArpSubnetGroup>(arpGroups);
                    TotalNearby    = arpGroups.Sum(g => g.Devices.Count);
                    NewDeviceCount = arpGroups.Sum(g => g.NewCount);
                });

                await Task.WhenAll(pingTasks.Append(gatewayPingTask));

                await dispatcher.InvokeAsync(() =>
                {
                    IsOnline      = InternetChecks.Any(c => c.IsOnline);
                    LastRefreshed = DateTime.Now.ToString("HH:mm:ss");
                });

                // Diagnostik kører fire-and-forget med egen guard — må aldrig blokere refresh
                _ = RunDiagnosticsAsync(dispatcher);
            }
            finally
            {
                // FIX #4: IsRefreshing = false altid på UI-thread; lås frigives bagefter
                await dispatcher.InvokeAsync(() => IsRefreshing = false);
                _loadLock.Release();
            }
        }

        // ── Diagnostik (DNS, DHCP, IPv6, portal) ─────────────────────────────

        private async Task RunDiagnosticsAsync(Dispatcher dispatcher)
        {
            if (Interlocked.CompareExchange(ref _diagInFlight, 1, 0) != 0) return;
            try
            {
                var gateway = _samplerGatewayIp;
                var servers = new List<(string Server, string Label)>();
                foreach (var dns in _diagDnsServers.Where(s => s.Contains('.')))
                    servers.Add((dns, dns == gateway ? "Gateway" : dns));

                var adapterName = _diagAdapterName;
                var dnsTask    = _diagnosticsService.MeasureDnsServersAsync(servers);
                var ipv6Task   = _diagnosticsService.CheckIpv6Async();
                var portalTask = _diagnosticsService.CheckCaptivePortalAsync();
                var leaseTask  = Task.Run(() => string.IsNullOrEmpty(adapterName)
                    ? null
                    : _diagnosticsService.GetDhcpLease(adapterName));

                var dnsResults = await dnsTask;
                var ipv6       = await ipv6Task;
                var portal     = await portalTask;
                var lease      = await leaseTask;

                _fastestDnsMs = dnsResults
                    .Where(r => r.ResponseMs.HasValue)
                    .Select(r => r.ResponseMs)
                    .Min();

                await dispatcher.InvokeAsync(() =>
                {
                    DnsResults = new ObservableCollection<DnsTimingResult>(dnsResults);

                    DhcpIsDhcp = lease?.IsDhcp == true;
                    if (lease is { IsDhcp: true })
                    {
                        DhcpServerText   = string.IsNullOrEmpty(lease.Server) ? "—" : lease.Server;
                        DhcpObtainedText = lease.Obtained?.ToString("dd-MM HH:mm") ?? "—";
                        if (lease.Expires.HasValue)
                        {
                            var rem = lease.Expires.Value - DateTimeOffset.Now;
                            var dateStr = lease.Expires.Value.ToString("dd-MM HH:mm");
                            DhcpExpiresText = rem.TotalSeconds > 0
                                ? $"{dateStr}  ({(int)rem.TotalHours}t {rem.Minutes:D2}m tilbage)"
                                : $"{dateStr}  (udløbet)";
                        }
                        else { DhcpExpiresText = "—"; }
                    }

                    Ipv6Ok   = ipv6 == Ipv6Status.Connected;
                    Ipv6Text = ipv6 == Ipv6Status.Connected ? "Forbundet" : "Ikke tilgængelig";

                    PortalWarning = portal != CaptivePortalStatus.None;
                    PortalText = portal switch
                    {
                        CaptivePortalStatus.None           => "Ingen portal registreret",
                        CaptivePortalStatus.PortalDetected => "Portal registreret — login kan være påkrævet",
                        _                                  => "Intet svar"
                    };

                    RecomputeHealth();
                });
            }
            catch { /* diagnostik må aldrig vælte dashboardet */ }
            finally
            {
                Interlocked.Exchange(ref _diagInFlight, 0);
            }
        }

        // ── Hastighedstest ───────────────────────────────────────────────────

        private async Task RunSpeedTestAsync()
        {
            IsSpeedTesting = true;
            SpeedTestProgressPercent = 0;
            SpeedTestPhaseText = "Starter...";
            _speedCts = new CancellationTokenSource();

            var progress = new Progress<SpeedTestProgress>(p =>
            {
                SpeedTestProgressPercent = p.Percent;
                SpeedTestPhaseText = p.Phase == SpeedTestPhase.Download
                    ? $"Henter... {p.CurrentMbps:F0} Mbit/s"
                    : "Sender...";
            });

            try
            {
                var result = await _diagnosticsService.RunSpeedTestAsync(progress, _speedCts.Token);
                LastSpeedTest = result;
                SaveDashboardData();
                SpeedTestPhaseText = string.Empty;
            }
            catch (OperationCanceledException)
            {
                SpeedTestPhaseText = "Annulleret";
            }
            catch
            {
                SpeedTestPhaseText = "Test fejlede";
            }
            finally
            {
                IsSpeedTesting = false;
                SpeedTestProgressPercent = 0;
                _speedCts.Dispose();
                _speedCts = null;
            }
        }

        // ── Persistens (ui_settings.json) ────────────────────────────────────

        private void LoadUiSettings()
        {
            try
            {
                if (!File.Exists(UiSettingsPath)) return;
                var json = File.ReadAllText(UiSettingsPath);
                var s = JsonSerializer.Deserialize<UiSettings>(json);
                if (s == null) return;
                _graphsVisible      = s.graphsVisible;
                _diagnosticsVisible = s.diagnosticsVisible;
            }
            catch { /* ignore corrupt file */ }
        }

        private void SaveUiSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(UiSettingsPath)!);
                var s = new UiSettings { graphsVisible = _graphsVisible, diagnosticsVisible = _diagnosticsVisible };
                var json = JsonSerializer.Serialize(s, _jsonOpts);
                File.WriteAllText(UiSettingsPath, json);
            }
            catch { /* ignore write errors */ }
        }

        // ── Persistens (dashboard.json) ──────────────────────────────────────

        private void LoadDashboardData()
        {
            try
            {
                if (!File.Exists(DashboardDataPath)) return;
                var json = File.ReadAllText(DashboardDataPath);
                var data = JsonSerializer.Deserialize<DashboardData>(json);
                if (data?.lastSpeedTest != null)
                    LastSpeedTest = data.lastSpeedTest;
            }
            catch { /* ignore corrupt file */ }
        }

        private void SaveDashboardData()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DashboardDataPath)!);
                var data = new DashboardData { lastSpeedTest = LastSpeedTest };
                var json = JsonSerializer.Serialize(data, _jsonOpts);
                File.WriteAllText(DashboardDataPath, json);
            }
            catch { /* ignore write errors */ }
        }

        private void RecountNewDevices()
        {
            foreach (var g in NearbyGroups)
                g.NewCount = g.Devices.Count(d => d.IsNew);
            NewDeviceCount = NearbyGroups.Sum(g => g.NewCount);
            CommandManager.InvalidateRequerySuggested();
        }

        private async Task FetchWanInfoAsync(Dispatcher dispatcher)
        {
            try
            {
                var json = await _httpClient.GetStringAsync("http://ip-api.com/json");
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                string ip      = root.TryGetProperty("query",   out var q) ? q.GetString() ?? "?" : "?";
                string isp     = root.TryGetProperty("isp",     out var i) ? i.GetString() ?? "" : "";
                string asn     = root.TryGetProperty("as",      out var a) ? a.GetString() ?? "" : "";
                string country = root.TryGetProperty("country", out var c) ? c.GetString() ?? "" : "";
                await dispatcher.InvokeAsync(() =>
                {
                    Wan.Ip      = ip;
                    Wan.Isp     = isp;
                    Wan.Asn     = asn;
                    Wan.Country = country;
                    Wan.IsLoading = false;
                });
            }
            catch
            {
                if (Application.Current != null)
                    await dispatcher.InvokeAsync(() => { Wan.Ip = "Ikke tilgængelig"; Wan.IsLoading = false; });
            }
        }

        private static long IpSortKey(string ip)
        {
            var p = ip.Split('.');
            if (p.Length != 4) return 0;
            long key = 0;
            foreach (var s in p)
                if (uint.TryParse(s, out uint v)) key = (key << 8) | v;
            return key;
        }
    }
}
