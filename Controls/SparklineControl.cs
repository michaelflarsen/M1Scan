using System;
using System.Collections.Generic;
using System.Globalization;
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

        public static readonly DependencyProperty ShowColorZonesProperty =
            DependencyProperty.Register(nameof(ShowColorZones), typeof(bool), typeof(SparklineControl),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty GridLabelBrushProperty =
            DependencyProperty.Register(nameof(GridLabelBrush), typeof(Brush), typeof(SparklineControl),
                new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x7B, 0x8F, 0xA8)),
                    FrameworkPropertyMetadataOptions.AffectsRender));

        // Zonegrænserne fra forbindelsesbevis-redesign.md — samme tal en læser uden
        // netværksbaggrund kan slå op i metric-kortenes ord-vurdering.
        private const double GreenZoneTopMs = 80;
        private const double YellowZoneTopMs = 200;

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

        /// <summary>
        /// Tegn tre vandrette farvebånd bag kurven (grøn/gul/rød, mærket med ord i
        /// højre kant) i stedet for numeriske ms-labels på y-aksen. Kun brugt i
        /// forbindelsesrapporten — dommen skal stå i grafen, ikke kun i teksten
        /// under den. Se forbindelsesbevis-redesign.md, afsnit "Farvezoner i grafen".
        /// </summary>
        public bool ShowColorZones
        {
            get => (bool)GetValue(ShowColorZonesProperty);
            set => SetValue(ShowColorZonesProperty, value);
        }

        public Brush GridLabelBrush
        {
            get => (Brush)GetValue(GridLabelBrushProperty);
            set => SetValue(GridLabelBrushProperty, value);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;

            // Usynlig hit-test-baggrund så tooltip mv. virker på hele fladen
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

            var values = Values;
            if (values == null || values.Count == 0) return;

            double dataMax = values.Where(v => v.HasValue).Select(v => v!.Value)
                                    .DefaultIfEmpty(0).Max();

            double max;
            if (ShowColorZones)
            {
                // Mindst det grønne bånd plus luft, ellers ser en perfekt måling
                // dramatisk ud ved at fylde hele grafhøjden. Overstiger data det,
                // udvides skalaen i stedet for at klippe kurven (aldrig klip).
                max = dataMax <= GreenZoneTopMs
                    ? GreenZoneTopMs * 1.25
                    : dataMax * 1.15;
            }
            else
            {
                max = Math.Max(dataMax, MinScaleMs);
            }

            // Plads i højre side til zone-labels ("Hurtigt"/"Mærkbart"/"Dårligt").
            double pad = ShowColorZones ? 6 : 2;
            double labelReserve = ShowColorZones ? 60 : 0;
            double chartW = w - labelReserve;
            double usableH = h - 2 * pad;

            // Divisoren må aldrig blive 0: en enkelt sample i stretch-tilstand
            // tegnes i venstre kant frem for at give division by zero.
            int span = StretchToFit
                ? Math.Max(values.Count - 1, 1)
                : Models.LatencySeries.Capacity - 1;
            double stepX = chartW / span;

            double YOf(double ms) => pad + usableH * (1 - Math.Min(ms, max) / max);

            if (ShowColorZones)
                DrawColorZones(dc, chartW, max, YOf);

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

        /// <summary>
        /// Tre vandrette bånd bag kurven (grøn 0-80 ms "Hurtigt", gul 80-200 ms
        /// "Mærkbart", rød over 200 ms "Dårligt"), mærket med ord i højre kant i
        /// stedet for tal — dommen skal kunne aflæses uden at kende ms-skalaen.
        /// Et bånd der ligger helt over den valgte skala tegnes ikke.
        /// </summary>
        private void DrawColorZones(DrawingContext dc, double chartW, double max, Func<double, double> yOf)
        {
            var zones = new (double from, double to, string label, Color color)[]
            {
                (0, GreenZoneTopMs, "Hurtigt", Color.FromArgb(0x33, 0x4C, 0xAF, 0x50)),
                (GreenZoneTopMs, YellowZoneTopMs, "Mærkbart", Color.FromArgb(0x33, 0xFF, 0x98, 0x00)),
                (YellowZoneTopMs, double.MaxValue, "Dårligt", Color.FromArgb(0x33, 0xF4, 0x43, 0x36)),
            };

            var typeface = new Typeface("Segoe UI");
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            foreach (var (from, to, label, color) in zones)
            {
                if (from >= max) continue;

                double bandTop = yOf(Math.Min(to, max));
                double bandBottom = yOf(from);
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                dc.DrawRectangle(brush, null, new Rect(0, bandTop, chartW, bandBottom - bandTop));

                var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    typeface, 10, GridLabelBrush, dpi);
                dc.DrawText(text, new Point(chartW + 6, (bandTop + bandBottom) / 2 - text.Height / 2));
            }
        }
    }
}
