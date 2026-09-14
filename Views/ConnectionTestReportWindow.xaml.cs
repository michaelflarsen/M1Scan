using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
    /// internet-reference, formuleret til en læser der ikke ved, hvad et millisekund
    /// er (bygherre, driftschef, forsikringssagsbehandler). Dom først (badge og
    /// resumé uden tal), tal derefter (hvert med en ord-vurdering ved siden af),
    /// teknik til sidst og foldet væk. Se forbindelsesbevis-redesign.md.
    ///
    /// Rapporten bruges som dokumentation over for en tredjepart, ofte fjernt fra
    /// selve enheden — derfor kan indholdet kopieres både som tekst og som billede.
    /// </summary>
    public partial class ConnectionTestReportWindow : Window
    {
        private readonly ConnectionTestResult _result;

        public ConnectionTestReportWindow(ConnectionTestResult result)
        {
            InitializeComponent();
            _result = result;

            var target = TargetLabel();
            var t = result.TargetStats;
            var r = result.ReferenceStats;

            TitleText.Text = "Forbindelsesbevis";
            // Med sagsnummer bærer headeren SAGEN (jf. briefets layout: "Sag 2026-0418 ·
            // dato · varighed") — enheden er stadig identificeret over grafen. Uden
            // sagsnummer falder vi tilbage til at vise enheden her, så headeren ikke
            // bliver tom for den der bare tester en forbindelse uden at dokumentere en sag.
            SubtitleText.Text = string.IsNullOrWhiteSpace(result.CaseNumber)
                ? $"{target} · {result.StartedAt:dd-MM-yyyy 'kl.' HH:mm} · {result.DurationSeconds} sekunder"
                : $"Sag {result.CaseNumber} · {result.StartedAt:dd-MM-yyyy 'kl.' HH:mm} · {result.DurationSeconds} sekunder";

            var badgeBrush = ParseBrush(result.BadgeColorHex, Brushes.Gray);
            BadgeText.Text = result.Badge;
            BadgePill.Background = badgeBrush;

            SummaryText.Text = result.Summary;

            BuildReplyDots();

            // Målets tal kommer fra Stats (hele testen), ikke fra serien (rullende
            // vindue) — ellers ville en lang test rapportere færre ping end den tog.
            BuildMetricCards(t);

            TargetSeriesLabel.Text = $"{target} — svartid over tid";
            TargetSparkline.Values = result.TargetSeries.Values;
            TargetAxisText.Text = BuildGraphConclusion(t);

            // Kontrolmåling: forklaret ved sit formål. Genbruger den eksisterende
            // sammenligningslogik (BuildVerdictText i PingMonitorViewModel), som
            // allerede dækker alle fire kombinationer af egen/målets tilstand —
            // ikke kun "begge er fine".
            ReferenceMeaningText.Text = result.Verdict;

            // Felter der ikke er udfyldt udelades helt frem for at vise tomme rækker
            // (briefets afsnit "Entydig identifikation").
            var identLines = new List<string> { $"Enhed: {target}" };
            if (!string.IsNullOrWhiteSpace(result.Location))
                identLines.Add($"Anlæg/lokation: {result.Location}");
            if (!string.IsNullOrWhiteSpace(result.Operator))
                identLines.Add($"Målt af: {result.Operator}");
            identLines.Add($"Målemetode: {t.Sent} ICMP-ping med {result.IntervalSeconds} sekunds mellemrum");
            identLines.Add($"Værktøj: M1Scan v{AppVersion}");
            ReferenceStatsText.Text = string.Join("\n", identLines);

            TechnicalDetailsText.Text =
                $"Kontrolmåling (internet 1.1.1.1): {r.ReplyCountDisplay} svar · {r.AvgDisplay} i snit · udsving {r.JitterDisplay} · tab {r.LossDisplay}";

            FooterText.Text =
                $"Målt med M1Scan · {t.Sent} ping med {result.IntervalSeconds} sekunds mellemrum · " +
                $"rapport dannet {DateTime.Now:dd-MM-yyyy HH:mm}";
        }

        private void LightModeCheckBox_Changed(object sender, RoutedEventArgs e) =>
            ApplyTheme(LightModeCheckBox.IsChecked == true);

        /// <summary>
        /// Lys variant til print og mail (forbindelsesbevis-redesign.md, "Lys variant
        /// til print og mail"): mørk tekst på hvid baggrund, valgt af brugeren i
        /// stedet for appens mørke skærm-tema. Kun CaptureRoot's farver ændres —
        /// datalaget og layoutet er identisk i begge varianter.
        /// </summary>
        private void ApplyTheme(bool light)
        {
            Brush pageBg = light ? Brushes.White : (Brush)FindResource("DarkBackgroundBrush");
            Brush cardBg = MakeBrush(light, 0xF1, 0xF3, 0xF6, 0x1C, 0x25, 0x35);
            Brush subtleBg = MakeBrush(light, 0xEE, 0xF1, 0xF5, 0x15, 0x1C, 0x28);
            Brush disclaimerBg = MakeBrush(light, 0xE8, 0xEC, 0xF1, 0x12, 0x18, 0x1F);
            Brush disclaimerBorder = MakeBrush(light, 0xC7, 0xCF, 0xDA, 0x3A, 0x46, 0x58);
            Brush primaryText = light ? new SolidColorBrush(Color.FromRgb(0x16, 0x20, 0x2E)) : Brushes.White;
            Brush bodyText = MakeBrush(light, 0x2A, 0x35, 0x42, 0xCF, 0xD8, 0xDC);
            Brush mutedText = MakeBrush(light, 0x5A, 0x6B, 0x80, 0x8F, 0xA3, 0xBF);
            Brush fadedText = MakeBrush(light, 0x7A, 0x86, 0x99, 0x7B, 0x8F, 0xA8);
            Brush dimText = MakeBrush(light, 0x8A, 0x97, 0xA8, 0x5A, 0x70, 0x86);
            Brush accentBlue = MakeBrush(light, 0x0D, 0x5C, 0xAB, 0x64, 0xB5, 0xF6);
            Brush sparklineStroke = MakeBrush(light, 0xE6, 0x51, 0x00, 0xFF, 0xB7, 0x4D);

            CaptureRoot.Background = pageBg;
            TitleText.Foreground = primaryText;
            SubtitleText.Foreground = mutedText;
            SummaryText.Foreground = bodyText;

            ReplyDotsBorder.Background = subtleBg;
            ReplySummaryText.Foreground = mutedText;

            AvgCard.Background = cardBg;
            MaxCard.Background = cardBg;
            JitterCard.Background = cardBg;
            AvgLabelText.Foreground = mutedText;
            MaxLabelText.Foreground = mutedText;
            JitterLabelText.Foreground = mutedText;
            AvgValueText.Foreground = primaryText;
            MaxValueText.Foreground = primaryText;
            AvgExplainText.Foreground = fadedText;
            MaxExplainText.Foreground = fadedText;
            JitterExplainText.Foreground = fadedText;
            // AvgWordText/MaxWordText/JitterWordText er altid farvet efter karakteren
            // (ConnectionGrading), uafhængigt af tema — de røres ikke her.

            GraphCard.Background = cardBg;
            TargetSeriesLabel.Foreground = primaryText;
            TargetAxisText.Foreground = bodyText;
            TargetSparkline.GridLabelBrush = fadedText;
            TargetSparkline.Stroke = sparklineStroke;

            ReferenceBorder.Background = subtleBg;
            ReferenceHeaderText.Foreground = accentBlue;
            ReferenceMeaningText.Foreground = bodyText;

            DisclaimerBorder.Background = disclaimerBg;
            DisclaimerBorder.BorderBrush = disclaimerBorder;
            DisclaimerText.Foreground = fadedText;

            // Expanderens indre content-område arver en mørk baggrund fra
            // MaterialDesign-temaet uafhængigt af CaptureRoot — skal sættes eksplicit,
            // ellers bliver den lyse variants tekst usynlig oven på en mørk flade.
            TechExpander.Background = pageBg;
            TechExpander.Foreground = mutedText;
            ReferenceStatsText.Foreground = bodyText;
            TechnicalDetailsText.Foreground = fadedText;

            FooterText.Foreground = dimText;

            // Print-hensigten fra briefet ("@media print skal folde de tekniske
            // oplysninger ud"): den lyse variant er beviset til at sende videre, så
            // de skal med uden et ekstra klik.
            TechExpander.IsExpanded = light;
        }

        private static SolidColorBrush MakeBrush(bool light,
            byte lightR, byte lightG, byte lightB, byte darkR, byte darkG, byte darkB)
        {
            var brush = new SolidColorBrush(light
                ? Color.FromRgb(lightR, lightG, lightB)
                : Color.FromRgb(darkR, darkG, darkB));
            brush.Freeze();
            return brush;
        }

        private static string AppVersion =>
            System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

        private string TargetLabel() =>
            string.IsNullOrWhiteSpace(_result.TargetDescription)
                ? _result.TargetHostOrIp
                : $"{_result.TargetDescription} ({_result.TargetHostOrIp})";

        /// <summary>
        /// Én prik pr. ping i rækkefølge (grøn = svar, rød = tabt). Ved lange
        /// målinger (over ca. 300 ping) repræsenterer hver prik i stedet et vindue af
        /// ping, farvet rødt hvis bare ét svar mangler i det — ellers ville en 1-times
        /// test kræve tusindvis af prikker.
        /// </summary>
        private void BuildReplyDots()
        {
            const int maxDots = 300;
            var sequence = _result.TargetStats.ReplySequence;

            if (sequence.Count == 0)
            {
                ReplyDots.ItemsSource = null;
                ReplySummaryText.Text = "Ingen målinger gennemført.";
                return;
            }

            var okBrush = ParseBrush("#4CAF50", Brushes.Green);
            var lostBrush = ParseBrush("#F44336", Brushes.Red);

            int lost = sequence.Count(ok => !ok);
            int replies = sequence.Count - lost;
            double percent = 100.0 * replies / sequence.Count;

            if (sequence.Count <= maxDots)
            {
                ReplyDots.ItemsSource = sequence.Select(ok => ok ? okBrush : lostBrush).ToList();
                ReplySummaryText.Text = lost == 0
                    ? $"{replies} ud af {sequence.Count} svar · {percent:F0} % — intet tab"
                    : $"{replies} ud af {sequence.Count} svar · {percent:F0} % — {lost} tabt";
                return;
            }

            int windowSize = (int)Math.Ceiling(sequence.Count / (double)maxDots);
            var dots = new List<Brush>();
            for (int i = 0; i < sequence.Count; i += windowSize)
            {
                bool windowOk = true;
                for (int j = i; j < Math.Min(i + windowSize, sequence.Count); j++)
                {
                    if (!sequence[j]) { windowOk = false; break; }
                }
                dots.Add(windowOk ? okBrush : lostBrush);
            }

            ReplyDots.ItemsSource = dots;
            ReplySummaryText.Text = lost == 0
                ? $"{replies} ud af {sequence.Count} svar · {percent:F0} % — intet tab (hver prik = {windowSize} ping)"
                : $"{replies} ud af {sequence.Count} svar · {percent:F0} % — {lost} tabt (hver prik = {windowSize} ping, rød hvis mindst ét svar mangler)";
        }

        /// <summary>
        /// Fylder de tre metric-kort (svartid, langsomste, stabilitet) med tal + en
        /// ord-vurdering af samme vægt som tallet + en forklarende linje. Intet tal
        /// står alene uden en dom ved siden af, jf. briefets acceptkriterie #5.
        /// </summary>
        private void BuildMetricCards(ConnectionTestStats t)
        {
            if (t.Replies == 0)
            {
                AvgValueText.Text = "—";
                AvgWordText.Text = "Intet svar";
                AvgExplainText.Text = "Der kom ingen svar fra enheden i testperioden.";
                MaxValueText.Text = "—";
                MaxWordText.Text = "Intet svar";
                MaxExplainText.Text = "Der kom ingen svar fra enheden i testperioden.";
                JitterWordText.Text = "Intet svar";
                JitterExplainText.Text = "Stabilitet kan ikke vurderes uden svar.";
                return;
            }

            var avgGrade = ConnectionGrading.GradeLatency(t.AvgMs);
            AvgValueText.Text = t.AvgDisplay;
            AvgWordText.Text = avgGrade.Word;
            AvgWordText.Foreground = ParseBrush(avgGrade.ColorHex, Brushes.White);
            AvgExplainText.Text = avgGrade.Explanation;

            var maxGrade = ConnectionGrading.GradeLatency(t.MaxMs);
            MaxValueText.Text = t.MaxDisplay;
            MaxWordText.Text = maxGrade.Word;
            MaxWordText.Foreground = ParseBrush(maxGrade.ColorHex, Brushes.White);
            MaxExplainText.Text = maxGrade.Explanation;

            var jitterGrade = ConnectionGrading.GradeJitter(t.JitterMs);
            JitterWordText.Text = jitterGrade.Word;
            JitterWordText.Foreground = ParseBrush(jitterGrade.ColorHex, Brushes.White);
            JitterExplainText.Text = $"{jitterGrade.Explanation} ({t.JitterMs.ToString("0.0", CultureInfo.InvariantCulture)} ms)";
        }

        /// <summary>
        /// Billedteksten under grafen: en konklusion om hvor kurven lå i forhold til
        /// farvezonerne, ikke en aksebeskrivelse (briefets afsnit "Farvezoner i grafen").
        /// </summary>
        private static string BuildGraphConclusion(ConnectionTestStats t)
        {
            if (t.Replies == 0) return string.Empty;

            if (t.MaxMs < 80)
                return "Linjen holder sig fladt i det grønne felt hele vejen igennem — den rører aldrig det gule.";
            if (t.MaxMs < 200)
                return "Linjen bevæger sig ind i det gule felt undervejs, men når aldrig det røde.";
            return "Linjen rammer det røde felt undervejs — svartiden var tidvis høj nok til at mærkes i daglig brug.";
        }

        private static Brush ParseBrush(string hex, Brush fallback)
        {
            // Farverne kommer fra vores egen klassificerings-logik, men et ugyldigt
            // hex må ikke kunne vælte rapportvinduet.
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
            // Brushen kan IKKE fryses: den peger på et levende UIElement (CaptureRoot),
            // og Freeze() på en VisualBrush med et UIElement-Visual kaster altid
            // "This Freezable cannot be frozen".
            var brush = new VisualBrush(CaptureRoot);

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
        /// Tekstversionen af beviset. Ordnet så modtageren læser dommen først og
        /// kontrolmålingen sidst — samme prioritering som vinduet.
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
            sb.AppendLine($"KARAKTER: {_result.Badge}");
            sb.AppendLine(_result.Summary);
            sb.AppendLine();
            sb.AppendLine(ReplySummaryText.Text);
            sb.AppendLine();
            sb.AppendLine("Enhedens forbindelse");
            sb.AppendLine(new string('-', 34));
            if (t.Replies == 0)
            {
                sb.AppendLine("  Der kom intet svar fra enheden i hele testperioden.");
            }
            else
            {
                var avgGrade = ConnectionGrading.GradeLatency(t.AvgMs);
                var maxGrade = ConnectionGrading.GradeLatency(t.MaxMs);
                var jitterGrade = ConnectionGrading.GradeJitter(t.JitterMs);
                sb.AppendLine($"  Svartid:    {t.AvgDisplay} i snit — {avgGrade.Word}");
                sb.AppendLine($"  Langsomste: {t.MaxDisplay} — {maxGrade.Word}");
                sb.AppendLine($"  Stabilitet: {jitterGrade.Word} ({t.JitterDisplay})");
                sb.AppendLine($"  Pakketab:   {t.LossDisplay}");
            }
            sb.AppendLine();
            sb.AppendLine("Kontrolmåling — internet (1.1.1.1)");
            sb.AppendLine(new string('-', 34));
            sb.AppendLine($"  Svar:       {r.ReplyCountDisplay} ping");
            sb.AppendLine($"  Svartid:    {r.AvgDisplay} i snit (maks {r.MaxDisplay})");
            sb.AppendLine($"  Udsving:    {r.JitterDisplay} jitter");
            sb.AppendLine($"  Pakketab:   {r.LossDisplay}");
            sb.AppendLine();
            sb.AppendLine(_result.Verdict);
            sb.AppendLine();
            sb.AppendLine("Hvad beviset dækker");
            sb.AppendLine(new string('-', 34));
            sb.AppendLine("Målingen dokumenterer, at enheden var tilgængelig på netværket i hele");
            sb.AppendLine("perioden. Den siger ikke noget om, hvorvidt enhedens interne funktioner virkede.");
            sb.AppendLine();
            sb.AppendLine($"Rapport dannet {DateTime.Now:dd-MM-yyyy HH:mm} med M1Scan.");
            return sb.ToString();
        }
    }
}
