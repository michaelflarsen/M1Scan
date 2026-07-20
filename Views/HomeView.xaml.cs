using System.Windows.Controls;
using M1Scan.ViewModels;

namespace M1Scan.Views
{
    public partial class HomeView : UserControl
    {
        public HomeView()
        {
            InitializeComponent();
            IsVisibleChanged   += (_, _) => SyncSampler();
            DataContextChanged += (_, _) => SyncSampler();
        }

        private void SyncSampler() =>
            (DataContext as HomeViewModel)?.SetDashboardVisible(IsVisible);
    }
}
