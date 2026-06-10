using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using M1Scan.Services;
using M1Scan.Utils;

namespace M1Scan.ViewModels
{
    public class AdapterDisplay
    {
        public string Name          { get; init; } = string.Empty;
        public string Description   { get; init; } = string.Empty;
        public string IpAddress     { get; init; } = string.Empty;
        public string SubnetMask    { get; init; } = string.Empty;
        public string Gateway       { get; init; } = string.Empty;
        public bool   IsUp          { get; init; }
        public bool   IsDefaultRoute { get; set; }
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

    public class ArpDeviceEntry
    {
        public string IpAddress  { get; init; } = string.Empty;
        public string MacAddress { get; init; } = string.Empty;
        public string Vendor     { get; init; } = string.Empty;
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

        public RelayCommand ToggleCommand { get; }

        public ArpSubnetGroup()
        {
            ToggleCommand = new RelayCommand(_ => IsExpanded = !IsExpanded);
        }
    }

    public class HomeViewModel : ObservableObject, IDisposable
    {
        private const int NetworkChangeDebounceMs = 1500;

        private static readonly (string Host, string Label)[] InternetHosts =
        {
            ("8.8.8.8",   "Google DNS"),
            ("1.1.1.1",   "Cloudflare DNS"),
            ("8.8.4.4",   "Google DNS (alt)")
        };

        private readonly INetworkService  _networkService;
        private readonly SemaphoreSlim    _loadLock = new SemaphoreSlim(1, 1);

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

        public RelayCommand RefreshCommand { get; }

        public HomeViewModel()
        {
            _networkService = new NetworkService();

            foreach (var (host, label) in InternetHosts)
                InternetChecks.Add(new InternetCheckResult { Host = host, Label = label });

            NetworkChange.NetworkAddressChanged += OnNetworkChanged;

            RefreshCommand = new RelayCommand(_ => _ = LoadAsync());
            _ = LoadAsync();
        }

        public void Dispose()
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            _loadLock.Dispose();
        }

        private async void OnNetworkChanged(object? sender, EventArgs e)
        {
            await Task.Delay(NetworkChangeDebounceMs);
            try
            {
                if (Application.Current != null)
                    await LoadAsync();
            }
            catch (ObjectDisposedException) { }
        }

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
                });

                var adaptersTask = _networkService.GetNetworkAdaptersAsync();
                var arpTask      = _networkService.GetArpTableAsync();

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
                        IsUp        = a.IsConnected
                    }).ToList();

                var best = displays.FirstOrDefault(a => a.Gateway != "—" && !string.IsNullOrEmpty(a.Gateway));
                if (best != null) best.IsDefaultRoute = true;

                // IPv6 link-local gateways (fe80::...) kan ikke pinges pålideligt — brug kun IPv4
                string? gatewayIp = (best?.Gateway != null && !best.Gateway.Contains(':'))
                    ? best.Gateway : null;
                Task gatewayPingTask = Task.CompletedTask;
                await dispatcher.InvokeAsync(() =>
                {
                    ActiveAdapters = new ObservableCollection<AdapterDisplay>(displays);
                    InternetVia    = best != null ? $"{best.Name}  ({best.IpAddress})" : "—";
                    GatewayCheck   = !string.IsNullOrEmpty(gatewayIp)
                        ? new InternetCheckResult { Host = gatewayIp!, Label = "Gateway" }
                        : null;
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
                        return new ArpSubnetGroup
                        {
                            Subnet      = g.Key,
                            Display     = g.Key + ".0 /24",
                            AdapterName = matchedAdapter?.Description ?? string.Empty,
                            Devices     = g.OrderBy(kv => IpSortKey(kv.Key))
                                           .Select(kv => new ArpDeviceEntry
                                           {
                                               IpAddress  = kv.Key,
                                               MacAddress = kv.Value,
                                               Vendor     = OuiLookup.Lookup(kv.Value) ?? string.Empty
                                           }).ToList()
                        };
                    }).ToList();

                await dispatcher.InvokeAsync(() =>
                {
                    NearbyGroups = new ObservableCollection<ArpSubnetGroup>(arpGroups);
                    TotalNearby  = arpGroups.Sum(g => g.Devices.Count);
                });

                await Task.WhenAll(pingTasks.Append(gatewayPingTask));

                await dispatcher.InvokeAsync(() =>
                {
                    IsOnline      = InternetChecks.Any(c => c.IsOnline);
                    LastRefreshed = DateTime.Now.ToString("HH:mm:ss");
                });
            }
            finally
            {
                // FIX #4: IsRefreshing = false altid på UI-thread; lås frigives bagefter
                await dispatcher.InvokeAsync(() => IsRefreshing = false);
                _loadLock.Release();
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
