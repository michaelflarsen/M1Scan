using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using M1Scan.ViewModels;

namespace M1Scan.Views
{
    public partial class FindIpView : UserControl
    {
        public FindIpView()
        {
            InitializeComponent();
        }

        // Adapter-picker (samme ContextMenu-mønster som Scan/IP-skift-fanerne).
        private void FindIpAdapterDropdownButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not FindIpViewModel vm) return;

            var menu = new ContextMenu
            {
                PlacementTarget = (Button)sender,
                Placement = PlacementMode.Bottom
            };

            foreach (var adapter in vm.AvailableAdapters)
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
                panel.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });

                if (adapter == vm.SelectedAdapter)
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
                    Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D))
                };
                var captured = adapter;
                item.Click += (_, _) => vm.SelectedAdapter = captured;
                menu.Items.Add(item);
            }

            menu.Items.Add(new Separator());

            var refreshItem = new MenuItem { Header = "Refresh adapters" };
            refreshItem.Click += (_, _) => vm.RefreshAdaptersCommand.Execute(null);
            menu.Items.Add(refreshItem);

            menu.IsOpen = true;
        }
    }
}
