using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using M1Scan.ViewModels;

namespace M1Scan.Views
{
    public partial class HomeView : UserControl
    {
        // Row layout:
        //  3 = diagnostik toggle  4 = diagnostik cards  5 = spacer (0)
        //  6 = graph header       7 = graph sparklines (resizable)
        //  8 = GraphSplitter (Auto)  9 = adapter cards
        // Begge sektioner tvinger deres indholds-række eksplicit til 0 ved kollaps
        // (Auto-række + Collapsed-indhold efterlod en rest → uens afstand).

        private GridLength _savedGraphHeight = new GridLength(80);
        private HomeViewModel? _vm;

        public HomeView()
        {
            InitializeComponent();
            IsVisibleChanged   += (_, _) => SyncSampler();
            DataContextChanged += (_, _) => OnDataContextChanged();

            // After drag, reset adapters row to Auto so no surplus space accumulates.
            GraphSplitter.AddHandler(
                Thumb.DragCompletedEvent,
                new DragCompletedEventHandler((_, _) =>
                    RootGrid.RowDefinitions[9].Height = GridLength.Auto));
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
            if (e.PropertyName == nameof(HomeViewModel.GraphsVisible))
                ApplyGraphRows(_vm!.GraphsVisible);
            else if (e.PropertyName == nameof(HomeViewModel.DiagnosticsVisible))
                ApplyDiagRows(_vm!.DiagnosticsVisible);
        }

        // Tving Row 4 til 0 ved kollaps — mirror af ApplyGraphRows for Row 7.
        private void ApplyDiagRows(bool visible) =>
            RootGrid.RowDefinitions[4].Height = visible ? GridLength.Auto : new GridLength(0);

        // Row 8 (GraphSplitter) er Auto og kollapser selv via splitterens Visibility.
        // Diagnostik (Row 4) kollapser via WrapPanel-DataTrigger + Auto-række — ingen code-behind.
        private void ApplyGraphRows(bool visible)
        {
            var rowGraph = RootGrid.RowDefinitions[7];

            if (visible)
            {
                rowGraph.MinHeight = 60;
                rowGraph.Height    = _savedGraphHeight.Value > 0
                    ? _savedGraphHeight : new GridLength(80);
            }
            else
            {
                if (rowGraph.Height.IsAbsolute && rowGraph.Height.Value > 0)
                    _savedGraphHeight = rowGraph.Height;
                rowGraph.MinHeight = 0;
                rowGraph.Height    = new GridLength(0);
            }
        }
    }
}
