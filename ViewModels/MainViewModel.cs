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
        public TracerouteViewModel  TracerouteVm  { get; }
        public FindIpViewModel      FindIpVm      { get; }
        public MacAliasViewModel    MacAliasVm    { get; }

        public AsyncRelayCommand RefreshAdaptersCommand { get; }
        public AsyncRelayCommand ResetAdapterCommand { get; }

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
            ITracerouteService tracerouteService = new TracerouteService();
            IGeoIpService geoIpService = new GeoIpService();
            IFindIpService findIpService = new FindIpService();
            IMacAliasService macAliasService = new MacAliasService();
            IDeviceNameService deviceNameService = new DeviceNameService();

            // Brugerens egne enhedsnavne skal være indlæst før det første scan
            // begynder at slå MAC-adresser op, ellers vises de først efter næste scan.
            _ = deviceNameService.LoadAsync();

            HomeVm        = new HomeViewModel(_networkService, diagnosticsService);
            NetworkScanVm = new NetworkScanViewModel(_networkService, exportService, deviceNameService);
            IpConfigVm    = new IpConfigViewModel(_ipConfigService, _networkService);
            WorkspaceVm   = new WorkspaceViewModel(_ipConfigService, exportService);
            UpdateVm      = new UpdateViewModel(updateService);
            TracerouteVm  = new TracerouteViewModel(tracerouteService, geoIpService);
            FindIpVm      = new FindIpViewModel(findIpService, _networkService);
            MacAliasVm    = new MacAliasViewModel(macAliasService);

            // Sæt MacAliasService globalt i OuiLookup så aliaser overrides vendors overalt
            OuiLookup.SetMacAliasService(macAliasService);

            RefreshAdaptersCommand = new AsyncRelayCommand(_ => RefreshAdaptersAsync(), onError: OnCommandError);
            ResetAdapterCommand = new AsyncRelayCommand(_ => ResetAdapterAsync(), _ => SelectedAdapter != null, OnCommandError);

            // Load adapters on startup
            RefreshAdaptersCommand.Execute(null);

            // Opstartstjek: UpdateService svælger selv alle fejl, men en fejl i
            // ViewModel-laget må heller ikke slippe ud af en fire-and-forget-task.
            _ = SafeCheckForUpdateAsync();
        }

        private void OnCommandError(Exception ex) => StatusMessage = $"Fejl: {ex.Message}";

        private async Task SafeCheckForUpdateAsync()
        {
            try { await UpdateVm.CheckForUpdateSilentlyAsync(); }
            catch (Exception ex) { CrashLog.Write("CheckForUpdateSilently", ex); }
        }

        public void Dispose()
        {
            HomeVm.Dispose();
            NetworkScanVm.Dispose();
            WorkspaceVm.Dispose();
            FindIpVm.Dispose();
            MacAliasVm.Dispose();
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
                var result = await _ipConfigService.ResetNetworkAdapterAsync(SelectedAdapter.Name);
                StatusMessage = result.Message;
                
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
