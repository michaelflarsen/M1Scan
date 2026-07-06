using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
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

        private static readonly Lazy<ControlTemplate> _adapterItemTemplate = new(() =>
            (ControlTemplate)XamlReader.Parse("""
                <ControlTemplate
                    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    TargetType="MenuItem">
                    <Border x:Name="Bd" Padding="8,5,8,5"
                            Background="{TemplateBinding Background}">
                        <ContentPresenter ContentSource="Header" VerticalAlignment="Center"/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsHighlighted" Value="True">
                            <Setter TargetName="Bd" Property="Background" Value="#3A5A8A"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
                """));

        private static ControlTemplate GetAdapterItemTemplate() => _adapterItemTemplate.Value;

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
            if (StatsRow       != null) StatsRow.Visibility       = _selectedPage == "Scan"         ? Visibility.Visible : Visibility.Collapsed;
        }

        private void AdapterDropdownButton_Click(object sender, RoutedEventArgs e)
        {
            var vm = _vm.NetworkScanVm;
            ShowAdapterPickerMenu((Button)sender, vm.AvailableAdapters, vm.SelectedAdapter,
                a => vm.SelectedAdapter = a, vm.RefreshAdaptersCommand);
        }

        private void IpConfigAdapterDropdownButton_Click(object sender, RoutedEventArgs e)
        {
            var vm = _vm.IpConfigVm;
            ShowAdapterPickerMenu((Button)sender, vm.NetworkAdapters, vm.SelectedAdapter,
                a => vm.SelectedAdapter = a, vm.RefreshAdaptersCommand);
        }

        /// <summary>
        /// ContextMenu-baseret adapter-picker — bruges i stedet for en almindelig ComboBox,
        /// da en tidligere custom ComboBox-styling (DarkComboBoxStyle) havde en Popup/StaysOpen-fejl
        /// der forhindrede item-selection (dropdown lukkede uden at vælge noget).
        /// </summary>
        private void ShowAdapterPickerMenu(
            Button anchor,
            IEnumerable<NetworkAdapter> adapters,
            NetworkAdapter? selectedAdapter,
            Action<NetworkAdapter> onSelect,
            ICommand? refreshCommand)
        {
            var menu = new ContextMenu();
            menu.PlacementTarget = anchor;
            menu.Placement = PlacementMode.Bottom;

            foreach (var adapter in adapters)
            {
                var ip = adapter.IpAddresses.Length > 0 ? adapter.IpAddresses[0] : "";
                var label = string.IsNullOrEmpty(ip) ? adapter.Description : $"{adapter.Description} — {ip}";

                var panel = new StackPanel { Orientation = Orientation.Horizontal };

                var dot = new Ellipse
                {
                    Width = 10,
                    Height = 10,
                    Fill = new SolidColorBrush(adapter.IsConnected
                        ? Color.FromRgb(0x4C, 0xAF, 0x50)
                        : Color.FromRgb(0x66, 0x66, 0x66)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 8, 0)
                };
                if (adapter.IsConnected)
                {
                    dot.Effect = new DropShadowEffect
                    {
                        Color = Color.FromRgb(0x4C, 0xAF, 0x50),
                        BlurRadius = 6,
                        ShadowDepth = 0,
                        Opacity = 0.8
                    };
                }
                panel.Children.Add(dot);

                panel.Children.Add(new TextBlock
                {
                    Text = label,
                    VerticalAlignment = VerticalAlignment.Center
                });

                if (adapter == selectedAdapter)
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = "✓",
                        Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(8, 0, 0, 0)
                    });
                }

                var item = new MenuItem
                {
                    Header = panel,
                    Template = GetAdapterItemTemplate(),
                    Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D))
                };
                var captured = adapter;
                item.Click += (_, _) => onSelect(captured);
                menu.Items.Add(item);
            }

            menu.Items.Add(new Separator());

            var refreshItem = new MenuItem { Header = "Refresh adapters" };
            refreshItem.Click += (_, _) => refreshCommand?.Execute(null);
            menu.Items.Add(refreshItem);

            menu.IsOpen = true;
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
