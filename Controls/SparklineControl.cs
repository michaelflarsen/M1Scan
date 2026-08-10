using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace M1Scan.Controls
{
    /// <summary>
    /// Letvægts-sparkline til latency-serier. null-samples (tabt pakke)
    /// tegnes som røde tick-marks på bundlinjen. Y-aksen har 20 ms-gulv,
    /// så en flad lav-latency-linje ikke fylder hele grafen.
    /// </summary>
    public class SparklineControl : FrameworkElement
    {
        private const double MinScaleMs = 20;

        public static readonly DependencyProperty ValuesProperty =
            DependencyProperty.Register(nameof(Values), typeof(IReadOnlyList<double?>), typeof(SparklineControl),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty StrokeProperty =
            DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(SparklineControl),
                new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x64, 0xB5, 0xF6)),
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty LossBrushProperty =
            DependencyProperty.Register(nameof(LossBrush), typeof(Brush), typeof(SparklineControl),
                new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36)),
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty StretchToFitProperty =
            DependencyProperty.Register(nameof(StretchToFit), typeof(bool), typeof(SparklineControl),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        public IReadOnlyList<double?>? Values
        {
            get => (IReadOnlyList<double?>?)GetValue(ValuesProperty);
            set => SetValue(ValuesProperty, value);
        }

        public Brush Stroke
        {
            get => (Brush)GetValue(StrokeProperty);
            set => SetValue(StrokeProperty, value);
        }

        public Brush LossBrush
        {
            get => (Brush)GetValue(LossBrushProperty);
            set => SetValue(LossBrushProperty, value);
        }

        /// <summary>
        /// Fordel de faktiske samples over hele bredden i stedet for over
        /// <see cref="Models.LatencySeries.Capacity"/>.
        ///
        /// Standard er false, fordi de løbende monitorer skal vokse fra venstre mod
        /// højre efterhånden som data samler sig — der ville en strækning få grafen
        /// til at "skride" ved hver ny måling. Sæt den til true for en AFSLUTTET
        /// serie (fx forbindelsesrapporten), hvor et fast vindue bare ville efterlade
        /// resten af bredden tom.
        /// </summary>
        public bool StretchToFit
        {
            get => (bool)GetValue(StretchToFitProperty);
            set => SetValue(StretchToFitProperty, value);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;

            // Usynlig hit-test-baggrund så tooltip mv. virker på hele fladen
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

            var values = Values;
            if (values == null || values.Count == 0) return;

            double max = Math.Max(values.Where(v => v.HasValue).Select(v => v!.Value)
                                        .DefaultIfEmpty(MinScaleMs).Max(), MinScaleMs);

            const double pad = 2;
            double usableH = h - 2 * pad;

            // Divisoren må aldrig blive 0: en enkelt sample i stretch-tilstand
            // tegnes i venstre kant frem for at give division by zero.
            int span = StretchToFit
                ? Math.Max(values.Count - 1, 1)
                : Models.LatencySeries.Capacity - 1;
            double stepX = w / span;

            double YOf(double ms) => pad + usableH * (1 - Math.Min(ms, max) / max);

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                bool penDown = false;
                for (int i = 0; i < values.Count; i++)
                {
                    if (!values[i].HasValue) { penDown = false; continue; }
                    var pt = new Point(i * stepX, YOf(values[i]!.Value));
                    if (!penDown) { ctx.BeginFigure(pt, false, false); penDown = true; }
                    else ctx.LineTo(pt, true, true);
                }
            }
            geometry.Freeze();

            var pen = new Pen(Stroke, 1.2);
            pen.Freeze();
            dc.DrawGeometry(null, pen, geometry);

            // Tabte pakker: røde tick-marks på bundlinjen
            var lossPen = new Pen(LossBrush, 1.5);
            lossPen.Freeze();
            for (int i = 0; i < values.Count; i++)
            {
                if (values[i].HasValue) continue;
                double x = i * stepX;
                dc.DrawLine(lossPen, new Point(x, h - pad - 5), new Point(x, h - pad));
            }
        }
    }
}
