using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using M1Scan.ViewModels;

namespace M1Scan.Views
{
    public partial class HomeView : UserControl
    {
        // Row layout:
        //  2 = diagnostik toggle  3 = diagnostik cards  4 = spacer (0)
        //  5 = graph header       6 = graph sparklines (resizable)
        //  7 = højde-splitter     8 = WAN chain + score  9 = adapter cards

        private GridLength _savedGraphHeight = new GridLength(80);
        private HomeViewModel? _vm;
        private bool _connectorUpdateQueued;

        public HomeView()
        {
            InitializeComponent();
            IsVisibleChanged   += (_, _) => SyncSampler();
            DataContextChanged += (_, _) => OnDataContextChanged();
            RootGrid.LayoutUpdated += OnLayoutUpdated;

            GraphSplitter.AddHandler(
                Thumb.DragCompletedEvent,
                new DragCompletedEventHandler((_, _) =>
                    RootGrid.RowDefinitions[6].Height = GridLength.Auto));
        }

        private void SyncSampler() =>
            (DataContext as HomeViewModel)?.SetDashboardVisible(IsVisible);

        private void OnDataContextChanged()
        {
            if (_vm != null)
                _vm.PropertyChanged -= OnVmPropertyChanged;

            _vm = DataContext as HomeViewModel;

            if (_vm != null)
            {
                _vm.PropertyChanged += OnVmPropertyChanged;
                ApplyGraphRows(_vm.GraphsVisible);
                ApplyDiagRows(_vm.DiagnosticsVisible);
            }

            SyncSampler();
        }

        private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            var vm = _vm;
            if (vm == null) return;
            if (e.PropertyName == nameof(HomeViewModel.GraphsVisible))
                ApplyGraphRows(vm.GraphsVisible);
            else if (e.PropertyName == nameof(HomeViewModel.DiagnosticsVisible))
                ApplyDiagRows(vm.DiagnosticsVisible);
        }

        private void ApplyDiagRows(bool visible)
        {
            RootGrid.RowDefinitions[3].Height = visible ? GridLength.Auto : new GridLength(0);
            RootGrid.RowDefinitions[4].Height = visible ? new GridLength(8) : new GridLength(0);
        }

        private void ApplyGraphRows(bool visible)
        {
            var row = RootGrid.RowDefinitions[6];
            if (visible)
            {
                row.MinHeight = 60;
                row.Height    = _savedGraphHeight.Value > 0 ? _savedGraphHeight : new GridLength(80);
            }
            else
            {
                if (row.Height.IsAbsolute && row.Height.Value > 0)
                    _savedGraphHeight = row.Height;
                row.MinHeight = 0;
                row.Height    = new GridLength(0);
            }
        }

        private void OnLayoutUpdated(object? sender, EventArgs e)
        {
            if (_connectorUpdateQueued) return;
            _connectorUpdateQueued = true;
            Dispatcher.InvokeAsync(() =>
            {
                _connectorUpdateQueued = false;
                UpdateConnector();
            }, DispatcherPriority.Background);
        }

        private void UpdateConnector()
        {
            ConnectorCanvas.Children.Clear();

            if (WanChainNode1.ActualWidth == 0 || WanChainNode1.ActualHeight == 0) return;

            var defaultAdapter = _vm?.ActiveAdapters.FirstOrDefault(a => a.IsDefaultRoute);
            if (defaultAdapter == null) return;

            var container = AdaptersItemsControl.ItemContainerGenerator
                .ContainerFromItem(defaultAdapter) as FrameworkElement;
            if (container == null || container.ActualWidth == 0) return;

            var start = WanChainNode1.TranslatePoint(
                new Point(WanChainNode1.ActualWidth / 2, WanChainNode1.ActualHeight),
                ConnectorCanvas);
            var end = container.TranslatePoint(
                new Point(container.ActualWidth / 2, 0),
                ConnectorCanvas);

            if (double.IsNaN(start.X) || double.IsNaN(end.X)) return;
            if (end.Y <= start.Y + 2) return;  // adapter card not below node card

            var accent = new SolidColorBrush(Color.FromRgb(0x4f, 0xc3, 0xf7));

            // Dashed line (stop a few px before arrow tip)
            var line = new Line
            {
                X1 = start.X, Y1 = start.Y,
                X2 = end.X,   Y2 = end.Y - 8,
                Stroke = accent, StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 4, 3 }
            };
            ConnectorCanvas.Children.Add(line);

            // Circle at top end
            AddCircle(start.X, start.Y, accent);

            // Arrow pointing down at bottom end
            var arrow = new Polygon
            {
                Fill = accent,
                Points = new PointCollection
                {
                    new Point(end.X,     end.Y),
                    new Point(end.X - 5, end.Y - 8),
                    new Point(end.X + 5, end.Y - 8)
                }
            };
            ConnectorCanvas.Children.Add(arrow);

            // Circle at bottom end
            AddCircle(end.X, end.Y, accent);


            void AddCircle(double cx, double cy, Brush fill)
            {
                var c = new Ellipse { Width = 6, Height = 6, Fill = fill };
                Canvas.SetLeft(c, cx - 3);
                Canvas.SetTop(c,  cy - 3);
                ConnectorCanvas.Children.Add(c);
            }
        }
    }
}
