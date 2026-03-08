using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using MMPing.Models;
using MMPing.Services;
using MMPing.Utils;

namespace MMPing.ViewModels
{
    public class NetworkScanViewModel : ObservableObject
    {
        private readonly INetworkService _networkService;
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
            set => SetProperty(ref _startIp, value);
        }

        public int EndIp
        {
            get => _endIp;
            set => SetProperty(ref _endIp, value);
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

        public RelayCommand PingSingleCommand { get; }
        public RelayCommand ScanNetworkCommand { get; }
        public RelayCommand ClearResultsCommand { get; }
        public RelayCommand AutoDetectSubnetCommand { get; }
        public RelayCommand ToggleAutoRefreshCommand { get; }
        public RelayCommand OpenInBrowserCommand { get; }
        public RelayCommand CopyIpCommand { get; }

        public NetworkScanViewModel()
        {
            _networkService = new NetworkService();

            _autoRefreshTimer = new DispatcherTimer();
            _autoRefreshTimer.Tick += async (_, _) => await ScanNetworkAsync();

            PingSingleCommand = new RelayCommand(async _ => await PingSingleAsync(), _ => !IsScanning && !string.IsNullOrEmpty(IpAddressInput));
            ScanNetworkCommand = new RelayCommand(async _ => await ScanNetworkAsync(), _ => !IsScanning);
            ClearResultsCommand = new RelayCommand(_ => DiscoveredHosts.Clear());
            AutoDetectSubnetCommand = new RelayCommand(async _ => await AutoDetectSubnetAsync(), _ => !IsScanning);
            ToggleAutoRefreshCommand = new RelayCommand(_ => IsAutoRefreshEnabled = !IsAutoRefreshEnabled);
            OpenInBrowserCommand = new RelayCommand(param =>
            {
                if (param is string ip && !string.IsNullOrEmpty(ip))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"http://{ip}") { UseShellExecute = true });
            });
            CopyIpCommand = new RelayCommand(param =>
            {
                if (param is string ip && !string.IsNullOrEmpty(ip))
                    System.Windows.Clipboard.SetText(ip);
            });
        }

        private async Task AutoDetectSubnetAsync()
        {
            try
            {
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
                var host = await _networkService.PingHostAsync(IpAddressInput);
                if (host.IsReachable)
                    host.IsPort80Open = await _networkService.CheckPortAsync(host.IpAddress, 80);

                var existing = DiscoveredHosts.FirstOrDefault(h => h.IpAddress == host.IpAddress);
                if (existing != null)
                {
                    existing.ResponseTime = host.ResponseTime;
                    existing.Status = host.Status;
                    existing.LastSeen = host.LastSeen;
                    existing.IsReachable = host.IsReachable;
                    existing.IsPort80Open = host.IsPort80Open;
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
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
            }
            finally
            {
                IsScanning = false;
            }
        }

        private async Task ScanNetworkAsync()
        {
            if (IsScanning) return;
            IsScanning = true;
            ScanProgress = 0;
            int total = EndIp - StartIp + 1;
            int completed = 0;
            int found = 0;
            StatusMessage = $"Scanner {total} IP-adresser...";

            try
            {
                var pingTasks = Enumerable.Range(StartIp, total).Select(async i =>
                {
                    var ip = $"{SubnetInput}.{i}";
                    var result = await _networkService.PingHostAsync(ip);
                    int count = System.Threading.Interlocked.Increment(ref completed);
                    if (result.IsReachable) System.Threading.Interlocked.Increment(ref found);
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        ScanProgress = (int)((count / (double)total) * 100);
                        StatusMessage = $"Scanner {SubnetInput}.x  —  {count}/{total} tjekket  —  {found} fundet";
                    });
                    return result;
                }).ToList();

                var hostInfos = await Task.WhenAll(pingTasks);
                var hosts = hostInfos.Where(h => h.IsReachable).ToList();

                // Port 80-check parallelt på alle online hosts
                await Task.WhenAll(hosts.Select(async host =>
                {
                    host.IsPort80Open = await _networkService.CheckPortAsync(host.IpAddress, 80);
                }));

                // Merge
                foreach (var host in hosts)
                {
                    var existing = DiscoveredHosts.FirstOrDefault(h => h.IpAddress == host.IpAddress);
                    if (existing != null)
                    {
                        existing.HostName = host.HostName;
                        existing.ResponseTime = host.ResponseTime;
                        existing.Status = host.Status;
                        existing.LastSeen = host.LastSeen;
                        existing.IsReachable = true;
                        existing.OsGuess = host.OsGuess;
                        existing.IsPort80Open = host.IsPort80Open;
                        if (!string.IsNullOrEmpty(host.MacAddress)) existing.MacAddress = host.MacAddress;
                        if (!string.IsNullOrEmpty(host.Vendor)) existing.Vendor = host.Vendor;
                        if (!string.IsNullOrEmpty(host.NetBiosName)) existing.NetBiosName = host.NetBiosName;
                    }
                    else
                    {
                        DiscoveredHosts.Add(host);
                    }
                }

                foreach (var h in DiscoveredHosts.Where(h => !hosts.Any(r => r.IpAddress == h.IpAddress)).ToList())
                {
                    h.Status = "Offline";
                    h.IsReachable = false;
                }

                SortHostsByIp();

                var onlineCount = DiscoveredHosts.Count(h => h.IsReachable);
                StatusMessage = $"Færdig — {onlineCount} online, {DiscoveredHosts.Count - onlineCount} offline";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
            }
            finally
            {
                IsScanning = false;
            }
        }
    }
}
