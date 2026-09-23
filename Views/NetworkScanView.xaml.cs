using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using M1Scan.ViewModels;

namespace M1Scan.Views
{
    public partial class NetworkScanView : UserControl
    {
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

        public NetworkScanView()
        {
            InitializeComponent();
        }

        // Adapter-picker (samme ContextMenu-mønster som de andre faner).
        private void AdapterDropdownButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not NetworkScanViewModel vm) return;

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
                    Template = _adapterItemTemplate.Value,
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
