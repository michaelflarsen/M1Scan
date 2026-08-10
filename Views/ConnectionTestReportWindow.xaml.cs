using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using M1Scan.Models;
using M1Scan.Utils;

namespace M1Scan.Views
{
    /// <summary>
    /// Viser resultatet af en samtidig ping-test mod et brugervalgt mål og en
    /// internet-reference.
    ///
    /// Rapporten er bygget som DOKUMENTATION om målet: enhedens status og tal fylder
    /// mest, mens referencen kun står som en kontrolmåling der viser at testen er
    /// troværdig. Den bruges til at vise en tredjepart at deres enhed er online og
    /// stabil, ofte fjernt fra selve enheden — derfor kan indholdet kopieres både som
    /// tekst og som billede.
    /// </summary>
    public partial class ConnectionTestReportWindow : Window
    {
        private readonly ConnectionTestResult _result;

        public ConnectionTestReportWindow(ConnectionTestResult result)
        {
            InitializeComponent();
            _result = result;

            var target = TargetLabel();

            TitleText.Text = $"Forbindelsesbevis — {target}";
            SubtitleText.Text =
                $"Målt {result.StartedAt:dd-MM-yyyy 'kl.' HH:mm:ss} · varighed {result.DurationSeconds} sekunder";

            // Status-banner
            var statusBrush = ParseBrush(result.TargetStatusColorHex, Brushes.Gray);
            StatusLabelText.Text = result.TargetStatusLabel;
            StatusLabelText.Foreground = statusBrush;
            StatusBanner.BorderBrush = statusBrush;
            StatusDetailText.Text = BuildStatusDetail();

            // Målets tal kommer fra Stats (hele testen), ikke fra serien (rullende
            // vindue) — ellers ville en lang test rapportere færre ping end den tog.
            var t = result.TargetStats;
            TargetSeriesLabel.Text = target;
            TargetSparkline.Values = result.TargetSeries.Values;
            TargetAxisText.Text = t.Replies > 0
                ? $"svartid over tid · skala 0–{Math.Max(result.TargetSeries.Max, 20):F0} ms"
                : string.Empty;
            TargetReplyText.Text  = t.ReplyCountDisplay;
            TargetAvgText.Text    = t.AvgDisplay;
            TargetMaxText.Text    = t.MaxDisplay;
            TargetJitterText.Text = t.JitterDisplay;

            // Kontrolmåling
            var r = result.ReferenceStats;
            ReferenceStatsText.Text =
                $"Svar {r.ReplyCountDisplay} · svartid {r.AvgDisplay} · udsving {r.JitterDisplay} · tab {r.LossDisplay}";
            ReferenceMeaningText.Text = result.ReferenceUnreliable
                ? "Egen internetforbindelse var ustabil under målingen — tallene ovenfor kan derfor være påvirket af måleforholdene."
                : "Egen internetforbindelse var stabil under målingen, så tallene ovenfor afspejler enheden selv.";

            FooterText.Text =
                $"Målt med M1Scan · {t.Sent} ping med {result.IntervalSeconds} sekunds mellemrum · " +
                $"rapport dannet {DateTime.Now:dd-MM-yyyy HH:mm}";

            VerdictText.Text = result.Verdict;
            VerdictText.Foreground = ParseBrush(result.VerdictColorHex, Brushes.LightGray);
        }

        private string TargetLabel() =>
            string.IsNullOrWhiteSpace(_result.TargetDescription)
                ? _result.TargetHostOrIp
                : $"{_result.TargetDescription} ({_result.TargetHostOrIp})";

        /// <summary>
        /// Én sætning under statusetiketten, formuleret som en konstatering om målet.
        /// Bruger svar-tælleren frem for den afrundede tabsprocent: "59 / 60" siger
        /// noget "0 %" ville skjule.
        /// </summary>
        private string BuildStatusDetail()
        {
            var t = _result.TargetStats;
            if (t.Sent == 0)
                return "Der blev ikke gennemført nogen målinger.";

            var basis = $"Enheden svarede på {t.ReplyCountDisplay} ping over {_result.DurationSeconds} sekunder";

            if (t.Replies == 0)
                return basis + ". Der kom intet svar fra enheden i hele testperioden.";

            return t.Lost == 0
                ? basis + $" — uden et eneste tabt svar. Gennemsnitlig svartid {t.AvgDisplay}."
                : basis + $" ({t.Lost} tabt, {t.LossDisplay}). Gennemsnitlig svartid {t.AvgDisplay}.";
        }

        private static Brush ParseBrush(string hex, Brush fallback)
        {
            // Farverne kommer fra vores egen verdict-logik, men et ugyldigt hex må
            // ikke kunne vælte rapportvinduet.
            try
            {
                var brush = (Brush?)new BrushConverter().ConvertFromString(hex);
                if (brush == null) return fallback;
                brush.Freeze();
                return brush;
            }
            catch (Exception ex) when (ex is FormatException or NotSupportedException)
            {
                return fallback;
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void CopyText_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(BuildReportText());
                FlashButton(CopyTextButton, "Kopieret ✓");
            }
            catch (Exception ex)
            {
                // Udklipsholderen kan være låst af et andet program — vis det,
                // i stedet for at lade knappen se ud som om den virkede.
                CrashLog.Write("ConnectionTestReportWindow.CopyText", ex);
                MessageBox.Show(this, "Teksten kunne ikke kopieres: " + ex.Message,
                    "Fejl", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CopyImage_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetImage(RenderCapture());
                FlashButton(CopyImageButton, "Kopieret ✓");
            }
            catch (Exception ex)
            {
                CrashLog.Write("ConnectionTestReportWindow.CopyImage", ex);
                MessageBox.Show(this, "Billedet kunne ikke kopieres: " + ex.Message,
                    "Fejl", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Tegner rapportfladen (uden knapper) til en bitmap. 2× skalering, så
        /// billedet stadig er skarpt når det vises i en mail eller på en skærm med
        /// anden DPI end afsenderens.
        /// </summary>
        private BitmapSource RenderCapture()
        {
            const double scale = 2.0;

            CaptureRoot.UpdateLayout();
            var size = CaptureRoot.RenderSize;
            if (size.Width <= 0 || size.Height <= 0)
                throw new InvalidOperationException(
                    "Rapporten er ikke tegnet færdig endnu. Prøv igen, når vinduet er synligt.");

            // Baggrunden tegnes eksplicit: CaptureRoot's Background dækker kun dens
            // eget areal, og et transparent hjørne ville blive sort i mange mailklienter.
            var brush = new VisualBrush(CaptureRoot);
            brush.Freeze();

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var bg = (Brush)FindResource("DarkBackgroundBrush");
                dc.DrawRectangle(bg, null, new Rect(new Point(0, 0), size));
                dc.DrawRectangle(brush, null, new Rect(new Point(0, 0), size));
            }

            var bmp = new RenderTargetBitmap(
                (int)Math.Ceiling(size.Width * scale),
                (int)Math.Ceiling(size.Height * scale),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bmp.Render(visual);

            // Uden alfa-kanal: Word og Outlook ignorerer alfa i en 32-bit DIB fra
            // udklipsholderen og tolker den som gennemsigtig, hvilket de tegner sort.
            // Billedet er allerede fuldt ugennemsigtigt, så konverteringen taber intet.
            var opaque = new FormatConvertedBitmap(bmp, PixelFormats.Bgr24, null, 0);
            opaque.Freeze();
            return opaque;
        }

        // Knappernes oprindelige tekst, fanget FØR nogen flash. Uden dette ville et
        // dobbeltklik inden for flash-perioden fange "Kopieret ✓" som "original" og
        // efterlade knappen med den tekst permanent.
        private readonly System.Collections.Generic.Dictionary<System.Windows.Controls.Button, object> _buttonLabels = new();

        private async void FlashButton(System.Windows.Controls.Button button, string text)
        {
            // async void: event-handler-flade. Krop pakket i try/catch jf. CLAUDE.md.
            try
            {
                if (!_buttonLabels.TryGetValue(button, out var original))
                {
                    original = button.Content;
                    _buttonLabels[button] = original;
                }

                button.Content = text;
                await System.Threading.Tasks.Task.Delay(1500);
                button.Content = original;
            }
            catch (Exception ex)
            {
                CrashLog.Write("ConnectionTestReportWindow.FlashButton", ex);
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PNG-billede (*.png)|*.png|Tekstfil (*.txt)|*.txt",
                DefaultExt = "png",
                AddExtension = true,
                FileName = $"m1scan-forbindelsesbevis-{_result.StartedAt:yyyy-MM-dd_HHmm}"
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                // Formatet følger det VALGTE filter, ikke filnavnets endelse: vælger
                // brugeren "Tekstfil" og skriver "bevis.png", skal der stadig skrives
                // tekst — ellers får de PNG-bytes i en fil de bad om som tekst.
                if (dialog.FilterIndex == 1)
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(RenderCapture()));
                    using var stream = File.Create(dialog.FileName);
                    encoder.Save(stream);
                }
                else
                {
                    File.WriteAllText(dialog.FileName, BuildReportText(), Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                CrashLog.Write("ConnectionTestReportWindow.Save", ex);
                MessageBox.Show(this, "Rapporten kunne ikke gemmes: " + ex.Message,
                    "Fejl", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Tekstversionen af beviset. Ordnet så modtageren læser konklusionen først
        /// og kontrolmålingen sidst — samme prioritering som vinduet.
        /// </summary>
        private string BuildReportText()
        {
            var t = _result.TargetStats;
            var r = _result.ReferenceStats;

            var sb = new StringBuilder();
            sb.AppendLine("M1Scan — Forbindelsesbevis");
            sb.AppendLine(new string('=', 34));
            sb.AppendLine($"Enhed:    {TargetLabel()}");
            sb.AppendLine($"Målt:     {_result.StartedAt:dd-MM-yyyy 'kl.' HH:mm:ss} ({_result.DurationSeconds} sekunder)");
            sb.AppendLine();
            sb.AppendLine($"RESULTAT: {_result.TargetStatusLabel}");
            sb.AppendLine(BuildStatusDetail());
            sb.AppendLine();
            sb.AppendLine("Enhedens forbindelse");
            sb.AppendLine(new string('-', 34));
            sb.AppendLine($"  Svar:       {t.ReplyCountDisplay} ping");
            sb.AppendLine($"  Svartid:    {t.AvgDisplay} i snit (maks {t.MaxDisplay})");
            sb.AppendLine($"  Udsving:    {t.JitterDisplay} jitter");
            sb.AppendLine($"  Pakketab:   {t.LossDisplay}");
            sb.AppendLine();
            sb.AppendLine("Kontrolmåling — internet (1.1.1.1)");
            sb.AppendLine(new string('-', 34));
            sb.AppendLine($"  Svar:       {r.ReplyCountDisplay} ping");
            sb.AppendLine($"  Svartid:    {r.AvgDisplay} i snit (maks {r.MaxDisplay})");
            sb.AppendLine($"  Udsving:    {r.JitterDisplay} jitter");
            sb.AppendLine($"  Pakketab:   {r.LossDisplay}");
            sb.AppendLine($"  {(_result.ReferenceUnreliable
                ? "-> Egen forbindelse var ustabil; tallene kan være påvirket."
                : "-> Egen forbindelse var stabil; målingen er pålidelig.")}");
            sb.AppendLine();
            sb.AppendLine("Fuld analyse");
            sb.AppendLine(new string('-', 34));
            sb.AppendLine(_result.Verdict);
            sb.AppendLine();
            sb.AppendLine($"Rapport dannet {DateTime.Now:dd-MM-yyyy HH:mm} med M1Scan.");
            return sb.ToString();
        }
    }
}
