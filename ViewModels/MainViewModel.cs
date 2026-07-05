using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using M1Scan.Models;
using M1Scan.Services;
using M1Scan.Utils;

namespace M1Scan.ViewModels
{
    public class MainViewModel : ObservableObject, IDisposable
    {
        private readonly INetworkService _networkService;
        private readonly IIpConfigService _ipConfigService;

        private ObservableCollection<NetworkAdapter> _networkAdapters = new();
        private ObservableCollection<NetworkAdapter> _activeAdapters = new();
        private ObservableCollection<NetworkAdapter> _inactiveAdapters = new();
        private NetworkAdapter? _selectedAdapter;
        private bool _isLoading;
        private string _statusMessage = "Ready";

        public ObservableCollection<NetworkAdapter> NetworkAdapters
        {
            get => _networkAdapters;
            set => SetProperty(ref _networkAdapters, value);
        }

        public ObservableCollection<NetworkAdapter> ActiveAdapters
        {
            get => _activeAdapters;
            set => SetProperty(ref _activeAdapters, value);
        }

        public ObservableCollection<NetworkAdapter> InactiveAdapters
        {
            get => _inactiveAdapters;
            set => SetProperty(ref _inactiveAdapters, value);
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

        public HomeViewModel         HomeVm        { get; }
        public NetworkScanViewModel NetworkScanVm { get; }
        public IpConfigViewModel    IpConfigVm    { get; }
        public WorkspaceViewModel   WorkspaceVm   { get; }
        public UpdateViewModel      UpdateVm      { get; }

        public RelayCommand RefreshAdaptersCommand { get; }
        public RelayCommand ResetAdapterCommand { get; }

        // Composition root: appens eneste service-instanser oprettes her og
        // sendes ned til hver ViewModel via constructor-injection (se CLAUDE.md
        // "Dependency injection"). Ingen DI-container — bevidst valg for et
        // værktøj af denne størrelse; alle services er stateless og sikre at dele.
        public MainViewModel()
        {
            _networkService = new NetworkService();
            _ipConfigService = new IpConfigService();
            IDiagnosticsService diagnosticsService = new DiagnosticsService();
            IExportService exportService = new ExportService();
            IUpdateService updateService = new UpdateService();

            HomeVm        = new HomeViewModel(_networkService, diagnosticsService);
            NetworkScanVm = new NetworkScanViewModel(_networkService, exportService);
            IpConfigVm    = new IpConfigViewModel(_ipConfigService, _networkService);
            WorkspaceVm   = new WorkspaceViewModel(_ipConfigService, exportService);
            UpdateVm      = new UpdateViewModel(updateService);

            RefreshAdaptersCommand = new RelayCommand(async _ => await RefreshAdaptersAsync());
            ResetAdapterCommand = new RelayCommand(async _ => await ResetAdapterAsync(), _ => SelectedAdapter != null);

            // Load adapters on startup
            _ = RefreshAdaptersAsync();
            _ = UpdateVm.CheckForUpdateSilentlyAsync();
        }

        public void Dispose()
        {
            HomeVm.Dispose();
            NetworkScanVm.Dispose();
            WorkspaceVm.Dispose();
        }



        private async Task RefreshAdaptersAsync()
        {
            IsLoading = true;
            StatusMessage = "Loading network adapters...";

            try
            {
                var adapters = await _networkService.GetNetworkAdaptersAsync();
                NetworkAdapters   = new ObservableCollection<NetworkAdapter>(adapters);
                ActiveAdapters    = new ObservableCollection<NetworkAdapter>(adapters.Where(a => a.IsConnected));
                InactiveAdapters  = new ObservableCollection<NetworkAdapter>(adapters.Where(a => !a.IsConnected));
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
