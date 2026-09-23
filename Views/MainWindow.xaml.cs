using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using M1Scan.Models;
using M1Scan.ViewModels;

namespace M1Scan.Views
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_TEXT_COLOR = 36;
        private const int DWMWCP_ROUND = 2;

        private MainViewModel _vm = null!;
        private static readonly double[] Scales = { 0.50, 0.60, 0.70, 0.80, 0.90, 1.0, 1.15, 1.30, 1.50, 1.75, 2.00 };
        private int _scaleIndex = 5;

        private string _selectedPage = "Dashboard";
        private int _onlineCount;
        private int _offlineCount;
        private string _lastScanTime = "—";
        private string _searchText = string.Empty;
        private ICollectionView? _filteredHosts;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string SelectedPage
        {
            get => _selectedPage;
            set { _selectedPage = value; Notify(); UpdatePageVisibility(); }
        }

        public int OnlineCount
        {
            get => _onlineCount;
            private set { _onlineCount = value; Notify(); }
        }

        public int OfflineCount
        {
            get => _offlineCount;
            private set { _offlineCount = value; Notify(); }
        }

        public string LastScanTime
        {
            get => _lastScanTime;
            private set { _lastScanTime = value; Notify(); }
        }

        public ICollectionView? FilteredHosts
        {
            get => _filteredHosts;
            private set { _filteredHosts = value; Notify(); }
        }

        public string SearchText
        {
            get => _searchText;
            set { _searchText = value; Notify(); _filteredHosts?.Refresh(); }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;

            int pref = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));

            // #1E1E1E som COLORREF (0x00BBGGRR)
            int captionColor = 0x001E1E1E;
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));

            int textColor = 0x00FFFFFF;
            DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref textColor, sizeof(int));
        }

        public MainWindow()
        {
            InitializeComponent();
            _vm = new MainViewModel();
            DataContext = _vm;

            Closed += (_, _) => _vm.Dispose();

            _vm.NetworkScanVm.DiscoveredHosts.CollectionChanged += OnHostsChanged;
            _vm.NetworkScanVm.PropertyChanged += OnScanVmPropertyChanged;

            var view = CollectionViewSource.GetDefaultView(_vm.NetworkScanVm.DiscoveredHosts);
            view.Filter = obj => obj is HostInfo h && MatchesSearch(h);
            FilteredHosts = view;

            AppVersionText.Text = "v" + (System.Reflection.Assembly.GetExecutingAssembly()
                                              .GetName().Version?.ToString(3) ?? "?");

            UpdatePageVisibility();
            ApplyScale();
        }

        private void OnHostsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            OnlineCount  = _vm.NetworkScanVm.DiscoveredHosts.Count(h => h.IsReachable);
            OfflineCount = _vm.NetworkScanVm.DiscoveredHosts.Count(h => !h.IsReachable);
        }

        private void OnScanVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(_vm.NetworkScanVm.IsScanning) && !_vm.NetworkScanVm.IsScanning)
                LastScanTime = DateTime.Now.ToString("HH:mm");
        }

        private bool MatchesSearch(HostInfo h)
        {
            if (string.IsNullOrWhiteSpace(_searchText)) return true;
            return h.IpAddress.Contains(_searchText, StringComparison.OrdinalIgnoreCase)
                || h.HostName.Contains(_searchText, StringComparison.OrdinalIgnoreCase)
                // NetBIOS-navnet er det der vises når reverse-DNS svigter, så det skal
                // også kunne søges på — ellers kan man ikke finde en række på det navn
                // man faktisk ser i tabellen.
                || h.NetBiosName.Contains(_searchText, StringComparison.OrdinalIgnoreCase)
                || h.Vendor.Contains(_searchText, StringComparison.OrdinalIgnoreCase)
                || h.MacAddress.Contains(_searchText, StringComparison.OrdinalIgnoreCase);
        }

        private void UpdatePageVisibility()
        {
            if (HomePanel      != null) HomePanel.Visibility      = _selectedPage == "Dashboard"    ? Visibility.Visible : Visibility.Collapsed;
            if (WorkspacePanel != null) WorkspacePanel.Visibility = _selectedPage == "DeviceFollow" ? Visibility.Visible : Visibility.Collapsed;
            if (DevicesPanel   != null) DevicesPanel.Visibility   = _selectedPage == "Scan"         ? Visibility.Visible : Visibility.Collapsed;
            if (AdaptersPanel  != null) AdaptersPanel.Visibility  = _selectedPage == "Adapters"     ? Visibility.Visible : Visibility.Collapsed;
            if (IpConfigPanel  != null) IpConfigPanel.Visibility  = _selectedPage == "IpSkift"      ? Visibility.Visible : Visibility.Collapsed;
            if (TraceroutePanel != null) TraceroutePanel.Visibility = _selectedPage == "Traceroute"  ? Visibility.Visible : Visibility.Collapsed;
            if (FindIpPanel    != null) FindIpPanel.Visibility     = _selectedPage == "FindIp"       ? Visibility.Visible : Visibility.Collapsed;
            if (MacAliasPanel  != null) MacAliasPanel.Visibility   = _selectedPage == "MacAlias"     ? Visibility.Visible : Visibility.Collapsed;
            if (HistoryPanel   != null) HistoryPanel.Visibility    = _selectedPage == "History"      ? Visibility.Visible : Visibility.Collapsed;
            if (PingMonitorPanel != null) PingMonitorPanel.Visibility = _selectedPage == "PingMonitor" ? Visibility.Visible : Visibility.Collapsed;
            if (StatsRow       != null) StatsRow.Visibility       = _selectedPage == "Scan"         ? Visibility.Visible : Visibility.Collapsed;

            UpdatePageActivation();
        }

        /// <summary>
        /// Starter/stopper sidernes baggrundsarbejde. Alle side-ViewModels lever hele
        /// appens levetid (navigationen slår kun Visibility til/fra), så uden dette
        /// kørte deres timere fra opstart og for evigt — også for sider brugeren
        /// aldrig åbnede. Se IActivatablePage.
        /// </summary>
        private void UpdatePageActivation()
        {
            if (_vm == null) return;

            SetActive(_vm.HomeVm,        _selectedPage == "Dashboard");
            SetActive(_vm.WorkspaceVm,   _selectedPage == "DeviceFollow");
            SetActive(_vm.HistoryVm,     _selectedPage == "History");
            SetActive(_vm.PingMonitorVm, _selectedPage == "PingMonitor");

            static void SetActive(IActivatablePage page, bool active)
            {
                if (active) page.OnActivated();
                else        page.OnDeactivated();
            }
        }

        private void ApplyScale()
        {
            double s = Scales[_scaleIndex];
            ContentScale.ScaleX = s;
            ContentScale.ScaleY = s;
            ZoomLabel.Text = $"{(int)(s * 100)}%";
            ZoomOutBtn.IsEnabled  = _scaleIndex > 0;
            ZoomInBtn.IsEnabled   = _scaleIndex < Scales.Length - 1;
        }

        private void ZoomIn_Click(object sender, RoutedEventArgs e)
        {
            if (_scaleIndex < Scales.Length - 1) { _scaleIndex++; ApplyScale(); }
        }

        private void ZoomOut_Click(object sender, RoutedEventArgs e)
        {
            if (_scaleIndex > 0) { _scaleIndex--; ApplyScale(); }
        }

        private void ZoomReset_Click(object sender, RoutedEventArgs e)
        {
            _scaleIndex = 5; ApplyScale();
        }

        private void SideNav_Click(object sender, RoutedEventArgs e)
            => SelectedPage = ((FrameworkElement)sender).Tag?.ToString() ?? "Scan";

        private void Notify([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
