using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using MMPing.Models;
using MMPing.Services;
using MMPing.Utils;

namespace MMPing.ViewModels
{
    public class IpConfigViewModel : ObservableObject
    {
        private readonly IIpConfigService _ipConfigService;
        private readonly INetworkService _networkService;

        private ObservableCollection<NetworkAdapter> _networkAdapters = new();
        private NetworkAdapter? _selectedAdapter;
        private string _ipAddress = string.Empty;
        private string _subnetMask = "255.255.255.0";
        private string _gateway = string.Empty;
        private bool _isDhcp = true;
        private bool _isConfiguring;
        private string _statusMessage = "Ready";

        public ObservableCollection<NetworkAdapter> NetworkAdapters
        {
            get => _networkAdapters;
            set => SetProperty(ref _networkAdapters, value);
        }

        public NetworkAdapter? SelectedAdapter
        {
            get => _selectedAdapter;
            set
            {
                if (SetProperty(ref _selectedAdapter, value) && value != null)
                {
                    LoadAdapterConfig();
                }
            }
        }

        public string IpAddress
        {
            get => _ipAddress;
            set => SetProperty(ref _ipAddress, value);
        }

        public string SubnetMask
        {
            get => _subnetMask;
            set => SetProperty(ref _subnetMask, value);
        }

        public string Gateway
        {
            get => _gateway;
            set => SetProperty(ref _gateway, value);
        }

        public bool IsDhcp
        {
            get => _isDhcp;
            set => SetProperty(ref _isDhcp, value);
        }

        public bool IsConfiguring
        {
            get => _isConfiguring;
            set => SetProperty(ref _isConfiguring, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public RelayCommand ApplyStaticIpCommand { get; }
        public RelayCommand ApplyDhcpCommand { get; }
        public RelayCommand FlushDnsCommand { get; }
        public RelayCommand RefreshAdaptersCommand { get; }

        public IpConfigViewModel()
        {
            _ipConfigService = new IpConfigService();
            _networkService = new NetworkService();

            ApplyStaticIpCommand = new RelayCommand(async _ => await ApplyStaticIpAsync(), _ => !IsConfiguring && !IsDhcp && SelectedAdapter != null);
            ApplyDhcpCommand = new RelayCommand(async _ => await ApplyDhcpAsync(), _ => !IsConfiguring && SelectedAdapter != null);
            FlushDnsCommand = new RelayCommand(async _ => await FlushDnsAsync(), _ => !IsConfiguring);
            RefreshAdaptersCommand = new RelayCommand(async _ => await RefreshAdaptersAsync());

            _ = RefreshAdaptersAsync();
        }

        private async Task RefreshAdaptersAsync()
        {
            try
            {
                var adapters = await _networkService.GetNetworkAdaptersAsync();
                NetworkAdapters = new ObservableCollection<NetworkAdapter>(adapters);
                StatusMessage = "Adapters refreshed";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
            }
        }

        private void LoadAdapterConfig()
        {
            if (SelectedAdapter == null) return;

            IsDhcp = SelectedAdapter.IsDhcpEnabled;

            if (SelectedAdapter.IpAddresses.Length > 0)
                IpAddress = SelectedAdapter.IpAddresses[0];

            if (!string.IsNullOrEmpty(SelectedAdapter.SubnetMask))
                SubnetMask = SelectedAdapter.SubnetMask;

            if (!string.IsNullOrEmpty(SelectedAdapter.Gateway))
                Gateway = SelectedAdapter.Gateway;

            StatusMessage = $"Loaded config for {SelectedAdapter.Description}";
        }

        private async Task ApplyStaticIpAsync()
        {
            if (SelectedAdapter == null || string.IsNullOrEmpty(IpAddress)) return;

            IsConfiguring = true;
            StatusMessage = "Applying static IP configuration...";

            try
            {
                bool success = await _ipConfigService.SetStaticIpAsync(
                    SelectedAdapter.Name,
                    IpAddress,
                    SubnetMask,
                    Gateway ?? "0.0.0.0"
                );

                StatusMessage = success ? "Static IP applied successfully" : "Failed to apply static IP";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
            }
            finally
            {
                IsConfiguring = false;
            }
        }

        private async Task ApplyDhcpAsync()
        {
            if (SelectedAdapter == null) return;

            IsConfiguring = true;
            StatusMessage = "Applying DHCP configuration...";

            try
            {
                bool success = await _ipConfigService.SetDhcpAsync(SelectedAdapter.Name);
                StatusMessage = success ? "DHCP applied successfully" : "Failed to apply DHCP";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
            }
            finally
            {
                IsConfiguring = false;
            }
        }

        private async Task FlushDnsAsync()
        {
            IsConfiguring = true;
            StatusMessage = "Flushing DNS cache...";

            try
            {
                string result = await _ipConfigService.FlushDnsAsync();
                StatusMessage = string.IsNullOrEmpty(result) ? "DNS flushed successfully" : result;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
            }
            finally
            {
                IsConfiguring = false;
            }
        }
    }
}
