using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using M1Scan.Models;
using M1Scan.ViewModels;

namespace M1Scan.Views
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private MainViewModel _vm = null!;
        private string _selectedPage = "Devices";
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

        public MainWindow()
        {
            InitializeComponent();
            _vm = new MainViewModel();
            DataContext = _vm;

            _vm.NetworkScanVm.DiscoveredHosts.CollectionChanged += OnHostsChanged;
            _vm.NetworkScanVm.PropertyChanged += OnScanVmPropertyChanged;

            var view = CollectionViewSource.GetDefaultView(_vm.NetworkScanVm.DiscoveredHosts);
            view.Filter = obj => obj is HostInfo h && MatchesSearch(h);
            FilteredHosts = view;

            UpdatePageVisibility();
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
                || h.Vendor.Contains(_searchText, StringComparison.OrdinalIgnoreCase)
                || h.MacAddress.Contains(_searchText, StringComparison.OrdinalIgnoreCase);
        }

        private void UpdatePageVisibility()
        {
            if (DevicesPanel  != null) DevicesPanel.Visibility  = _selectedPage == "Devices"  ? Visibility.Visible : Visibility.Collapsed;
            if (AdaptersPanel != null) AdaptersPanel.Visibility = _selectedPage == "Adapters" ? Visibility.Visible : Visibility.Collapsed;
            if (IpConfigPanel != null) IpConfigPanel.Visibility = _selectedPage == "IpConfig" ? Visibility.Visible : Visibility.Collapsed;
            if (StatsRow      != null) StatsRow.Visibility      = _selectedPage == "Devices"  ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SideNav_Click(object sender, RoutedEventArgs e)
            => SelectedPage = ((FrameworkElement)sender).Tag?.ToString() ?? "Devices";

        private void Notify([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
