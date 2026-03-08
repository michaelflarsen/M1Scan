using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using MMPing.Models;
using MMPing.Services;
using MMPing.Utils;

namespace MMPing.ViewModels
{
    public class MainViewModel : ObservableObject
    {
        private readonly INetworkService _networkService;
        private readonly IIpConfigService _ipConfigService;

        private ObservableCollection<NetworkAdapter> _networkAdapters = new();
        private NetworkAdapter? _selectedAdapter;
        private bool _isLoading;
        private string _statusMessage = "Ready";

        public ObservableCollection<NetworkAdapter> NetworkAdapters
        {
            get => _networkAdapters;
            set => SetProperty(ref _networkAdapters, value);
        }

        public NetworkAdapter? SelectedAdapter
        {
            get => _selectedAdapter;
            set => SetProperty(ref _selectedAdapter, value);
        }

        public bool IsLoading
        {
            get => _isLoading;
            set => SetProperty(ref _isLoading, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public NetworkScanViewModel NetworkScanVm { get; }
        public IpConfigViewModel IpConfigVm { get; }

        public RelayCommand RefreshAdaptersCommand { get; }
        public RelayCommand ResetAdapterCommand { get; }

        public MainViewModel()
        {
            _networkService = new NetworkService();
            _ipConfigService = new IpConfigService();

            NetworkScanVm = new NetworkScanViewModel();
            IpConfigVm = new IpConfigViewModel();

            RefreshAdaptersCommand = new RelayCommand(async _ => await RefreshAdaptersAsync());
            ResetAdapterCommand = new RelayCommand(async _ => await ResetAdapterAsync(), _ => SelectedAdapter != null);

            // Load adapters on startup
            _ = RefreshAdaptersAsync();
        }

        private async Task RefreshAdaptersAsync()
        {
            IsLoading = true;
            StatusMessage = "Loading network adapters...";

            try
            {
                var adapters = await _networkService.GetNetworkAdaptersAsync();
                NetworkAdapters = new ObservableCollection<NetworkAdapter>(adapters);
                StatusMessage = $"Loaded {adapters.Count} network adapters";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task ResetAdapterAsync()
        {
            if (SelectedAdapter == null) return;

            IsLoading = true;
            StatusMessage = $"Resetting {SelectedAdapter.Name}...";

            try
            {
                bool success = await _ipConfigService.ResetNetworkAdapterAsync(SelectedAdapter.Name);
                StatusMessage = success ? "Adapter reset successfully" : "Failed to reset adapter";
                
                await Task.Delay(2000);
                await RefreshAdaptersAsync();
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }
    }
}
