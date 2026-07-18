using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using M1Scan.ViewModels;

namespace M1Scan.Views
{
    public partial class TracerouteView : UserControl
    {
        private const double CanvasPadding = 40;
        private const double BarSpacing = 5;
        private TracerouteViewModel? _oldVm;
        private NotifyCollectionChangedEventHandler? _collectionChangedHandler;
        private System.ComponentModel.PropertyChangedEventHandler? _propertyChangedHandler;

        // Cache canvas elements to avoid recreating on every redraw
        private readonly Dictionary<int, Rectangle> _barRectangles = new(); // hopNumber -> Rectangle
        private readonly Dictionary<int, TextBlock> _lossLabels = new();    // hopNumber -> loss% label
        private readonly Dictionary<int, TextBlock> _hopLabels = new();     // hopNumber -> hop# label

        public TracerouteView()
        {
            InitializeComponent();
            DataContextChanged += (s, e) =>
            {
                // Unsubscribe from old ViewModel
                if (_oldVm != null)
                {
                    _oldVm.Hops.CollectionChanged -= _collectionChangedHandler;
                    _oldVm.PropertyChanged -= _propertyChangedHandler;
                }

                if (DataContext is TracerouteViewModel vm)
                {
                    // Create and store handlers to enable unsubscription
                    _collectionChangedHandler = (_, __) => RedrawGraph();
                    _propertyChangedHandler = (_, args) =>
                    {
                        if (args.PropertyName == nameof(vm.MaxLatency))
                            RedrawGraph();
                    };

                    vm.Hops.CollectionChanged += _collectionChangedHandler;
                    vm.PropertyChanged += _propertyChangedHandler;
                    _oldVm = vm;
                }
                else
                {
                    _oldVm = null;
                }
            };
        }

        private void HopGraphCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            RedrawGraph();
        }

        private void RedrawGraph()
        {
            if (DataContext is not TracerouteViewModel vm || vm.Hops.Count == 0)
            {
                HopGraphCanvas.Children.Clear();
                _barRectangles.Clear();
                _lossLabels.Clear();
                _hopLabels.Clear();
                return;
            }

            // Don't clear — reuse elements instead (avoids recreation overhead on every redraw)

            double w = HopGraphCanvas.ActualWidth;
            double h = HopGraphCanvas.ActualHeight;

            if (w <= CanvasPadding * 2 || h <= CanvasPadding * 2) return;

            // Y-scale: auto fra max latency
            double maxLatency = vm.MaxLatency > 0 ? vm.MaxLatency : 100;
            double graphHeight = h - 2 * CanvasPadding;
            double yScale = graphHeight / maxLatency;

            // X: fordel hop'erne
            int hopCount = vm.Hops.Count;
            double barWidth = Math.Max(5, (w - 2 * CanvasPadding - (hopCount - 1) * BarSpacing) / hopCount);

            // Tegn gridlines + Y-akse
            DrawYAxis(h, graphHeight, maxLatency, yScale);

            // Tegn søjler (reuse elements to avoid recreation overhead)
            for (int i = 0; i < hopCount; i++)
            {
                var hop = vm.Hops[i];
                double x = CanvasPadding + i * (barWidth + BarSpacing);
                double hopLatency = hop.LatencySeries.Avg;
                double hopLoss = hop.LatencySeries.LossPercent;

                // Søjlehøjde: latency scaled, eller fuld høj hvis 100% tab
                double barHeight = hopLoss >= 100 ? graphHeight : Math.Max(1, hopLatency * yScale);
                double y = CanvasPadding + graphHeight - barHeight;

                // Farve efter tab%
                Color barColor = hopLoss switch
                {
                    >= 100 => Color.FromRgb(0xe2, 0x41, 0x3f), // rød #e2413f
                    > 0 => Color.FromRgb(0xf5, 0xa6, 0x23),    // amber #f5a623
                    _ => Color.FromRgb(0x4f, 0xc3, 0xf7)       // cyan #4fc3f7
                };

                // Reuse or create bar rectangle
                Rectangle rect;
                if (_barRectangles.ContainsKey(hop.HopNumber))
                {
                    rect = _barRectangles[hop.HopNumber];
                }
                else
                {
                    rect = new Rectangle { Opacity = 0.85 };
                    _barRectangles[hop.HopNumber] = rect;
                    HopGraphCanvas.Children.Add(rect);
                }
                rect.Width = barWidth;
                rect.Height = barHeight;
                rect.Fill = new SolidColorBrush(barColor);
                Canvas.SetLeft(rect, x);
                Canvas.SetTop(rect, y);

                // Reuse or create loss% label
                if (hopLoss > 0)
                {
                    TextBlock label;
                    if (_lossLabels.ContainsKey(hop.HopNumber))
                    {
                        label = _lossLabels[hop.HopNumber];
                    }
                    else
                    {
                        label = new TextBlock
                        {
                            Foreground = new SolidColorBrush(Colors.White),
                            FontSize = 9,
                            FontFamily = new FontFamily("JetBrains Mono, Consolas, Courier New"),
                            TextAlignment = TextAlignment.Center
                        };
                        _lossLabels[hop.HopNumber] = label;
                        HopGraphCanvas.Children.Add(label);
                    }
                    label.Text = $"{hopLoss:F0}%";
                    label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    Canvas.SetLeft(label, x + barWidth / 2 - label.DesiredSize.Width / 2);
                    Canvas.SetTop(label, y - label.DesiredSize.Height - 4);
                }
                else if (_lossLabels.ContainsKey(hop.HopNumber))
                {
                    HopGraphCanvas.Children.Remove(_lossLabels[hop.HopNumber]);
                    _lossLabels.Remove(hop.HopNumber);
                }

                // Reuse or create hop-number label
                TextBlock hopLabel;
                if (_hopLabels.ContainsKey(hop.HopNumber))
                {
                    hopLabel = _hopLabels[hop.HopNumber];
                }
                else
                {
                    hopLabel = new TextBlock
                    {
                        Foreground = new SolidColorBrush(Color.FromRgb(0x90, 0xca, 0xf9)),
                        FontSize = 10,
                        FontFamily = new FontFamily("JetBrains Mono, Consolas, Courier New"),
                        TextAlignment = TextAlignment.Center
                    };
                    _hopLabels[hop.HopNumber] = hopLabel;
                    HopGraphCanvas.Children.Add(hopLabel);
                }
                hopLabel.Text = hop.HopNumber.ToString();
                hopLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(hopLabel, x + barWidth / 2 - hopLabel.DesiredSize.Width / 2);
                Canvas.SetTop(hopLabel, CanvasPadding + graphHeight + 4);
            }
        }

        private void DrawYAxis(double h, double graphHeight, double maxLatency, double yScale)
        {
            // Grid interval: auto baseret på max latency
            double interval = maxLatency <= 50 ? 10
                            : maxLatency <= 200 ? 20
                            : maxLatency <= 500 ? 50
                            : 100;

            for (double ms = interval; ms <= maxLatency; ms += interval)
            {
                double y = CanvasPadding + graphHeight - (ms * yScale);

                // Gridline
                var line = new Line
                {
                    X1 = CanvasPadding,
                    Y1 = y,
                    X2 = HopGraphCanvas.ActualWidth - CanvasPadding,
                    Y2 = y,
                    Stroke = new SolidColorBrush(Color.FromRgb(0x2a, 0x3f, 0x5f)),
                    StrokeThickness = 0.5,
                    Opacity = 0.4
                };
                HopGraphCanvas.Children.Add(line);

                // Y-label
                var label = new TextBlock
                {
                    Text = $"{ms:F0}",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x90, 0xca, 0xf9)),
                    FontSize = 9,
                    FontFamily = new FontFamily("JetBrains Mono, Consolas, Courier New")
                };
                Canvas.SetLeft(label, 5);
                Canvas.SetTop(label, y - 7);
                HopGraphCanvas.Children.Add(label);
            }

            // Y-akse linje
            var yAxis = new Line
            {
                X1 = CanvasPadding,
                Y1 = 0,
                X2 = CanvasPadding,
                Y2 = h,
                Stroke = new SolidColorBrush(Color.FromRgb(0xe0, 0x33, 0x7a)),
                StrokeThickness = 1.5
            };
            HopGraphCanvas.Children.Add(yAxis);
        }

        private void DataGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // Pass mouse wheel events to parent ScrollViewer so scrolling works when over DataGrid
            var scrollViewer = FindParent<ScrollViewer>(sender as UIElement);
            if (scrollViewer != null)
            {
                scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - e.Delta / 3.0);
                e.Handled = true;
            }
        }

        private static T? FindParent<T>(UIElement? element) where T : UIElement
        {
            UIElement? parent = VisualTreeHelper.GetParent(element) as UIElement;
            if (parent == null) return null;
            return parent is T t ? t : FindParent<T>(parent);
        }
    }
}
