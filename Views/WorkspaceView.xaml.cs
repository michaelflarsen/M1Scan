using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using M1Scan.ViewModels;

namespace M1Scan.Views
{
    public partial class WorkspaceView : UserControl
    {
        private static readonly System.Lazy<ControlTemplate> _adapterItemTemplate = new(() =>
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

        public WorkspaceView()
        {
            InitializeComponent();
        }

        private void MyIpCard_MouseEnter(object sender, MouseEventArgs e)
        {
            ((System.Windows.Controls.Control)sender).Background =
                new SolidColorBrush(Color.FromRgb(0x23, 0x30, 0x40));
        }

        private void MyIpCard_MouseLeave(object sender, MouseEventArgs e)
        {
            ((System.Windows.Controls.Control)sender).Background =
                new SolidColorBrush(Color.FromRgb(0x1C, 0x25, 0x35));
        }

        private void MyIpCard_Click(object sender, MouseButtonEventArgs e)
        {
            ShowAdapterMenu((UIElement)sender);
        }

        private void MyIpButton_Click(object sender, RoutedEventArgs e)
        {
            ShowAdapterMenu((UIElement)sender);
            e.Handled = true;
        }

        private void ShowAdapterMenu(UIElement target)
        {
            if (DataContext is not WorkspaceViewModel vm) return;

            var menu = new ContextMenu
            {
                PlacementTarget = target,
                Placement = PlacementMode.Bottom
            };

            foreach (var adapter in vm.AvailableAdapters)
            {
                var label = string.IsNullOrEmpty(adapter.IpAddress)
                    ? adapter.Description
                    : $"{adapter.Description} — {adapter.IpAddress}";

                var panel = new StackPanel { Orientation = Orientation.Horizontal };

                var dot = new Ellipse
                {
                    Width = 10,
                    Height = 10,
                    Fill = new SolidColorBrush(adapter.IsUp
                        ? Color.FromRgb(0x4C, 0xAF, 0x50)
                        : Color.FromRgb(0x66, 0x66, 0x66)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 8, 0)
                };
                if (adapter.IsUp)
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

                if (adapter.SystemName == vm.ActiveAdapterSystemName)
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
                item.Click += (_, _) => vm.SelectAdapterCommand.Execute(captured);
                menu.Items.Add(item);
            }

            menu.Items.Add(new Separator());

            var refreshItem = new MenuItem { Header = "Refresh" };
            refreshItem.Click += (_, _) => vm.RefreshAdapterCommand.Execute(null);
            menu.Items.Add(refreshItem);

            menu.IsOpen = true;
        }
    }
}
