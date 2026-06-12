using System.Windows;
using System.Windows.Controls;
using M1Scan.ViewModels;

namespace M1Scan.Views
{
    public partial class HomeView : UserControl
    {
        public HomeView()
        {
            InitializeComponent();
            // Latency-sampleren skal kun køre mens dashboardet er synligt.
            // DataContextChanged er nødvendig: ved opstart fires IsVisibleChanged
            // før DataContext er sat, og så ville sampleren aldrig starte.
            IsVisibleChanged    += (_, _) => SyncSampler();
            DataContextChanged  += (_, _) => SyncSampler();
        }

        private void SyncSampler()
        {
            (DataContext as HomeViewModel)?.SetDashboardVisible(IsVisible);
        }
    }
}
