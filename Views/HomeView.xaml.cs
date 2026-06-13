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
        //  3 = diagnostik toggle  4 = diagnostik cards  5 = spacer (8px)
        //  6 = graph header       7 = graph sparklines
        //  8 = GraphSplitter      9 = adapter cards

        private GridLength _savedGraphHeight = new GridLength(80);
        private GridLength _savedDiagSpacer  = new GridLength(8);
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
                ApplyDiagRows(_vm.DiagnosticsVisible);
                ApplyGraphRows(_vm.GraphsVisible);
            }

            SyncSampler();
        }

        private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(HomeViewModel.GraphsVisible):
                    ApplyGraphRows(_vm!.GraphsVisible);
                    break;
                case nameof(HomeViewModel.DiagnosticsVisible):
                    ApplyDiagRows(_vm!.DiagnosticsVisible);
                    break;
            }
        }

        private void ApplyGraphRows(bool visible)
        {
            var rowGraph    = RootGrid.RowDefinitions[7];
            var rowSplitter = RootGrid.RowDefinitions[8];

            if (visible)
            {
                rowGraph.MinHeight = 60;
                rowGraph.Height    = _savedGraphHeight.Value > 0
                    ? _savedGraphHeight : new GridLength(80);
                rowSplitter.Height = new GridLength(8);
            }
            else
            {
                if (rowGraph.Height.IsAbsolute && rowGraph.Height.Value > 0)
                    _savedGraphHeight = rowGraph.Height;
                rowGraph.MinHeight = 0;
                rowGraph.Height    = new GridLength(0);
                rowSplitter.Height = new GridLength(0);
            }
        }

        private void ApplyDiagRows(bool visible)
        {
            var rowCards  = RootGrid.RowDefinitions[4];
            var rowSpacer = RootGrid.RowDefinitions[5];

            if (visible)
            {
                rowCards.Height  = GridLength.Auto;
                rowSpacer.Height = _savedDiagSpacer;
            }
            else
            {
                if (rowSpacer.Height.Value > 0)
                    _savedDiagSpacer = rowSpacer.Height;
                rowCards.Height  = new GridLength(0);
                rowSpacer.Height = new GridLength(0);
            }
        }
    }
}
