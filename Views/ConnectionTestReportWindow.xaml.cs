using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using M1Scan.Models;
using M1Scan.Utils;

namespace M1Scan.Views
{
    /// <summary>
    /// Viser resultatet af en samtidig ping-test mod en internet-reference og et
    /// brugervalgt mål, så man visuelt kan se om udsving stammer fra egen forbindelse
    /// (begge linjer rammes) eller fra målet/internettet efter det (kun mål-linjen).
    /// </summary>
    public partial class ConnectionTestReportWindow : Window
    {
        private readonly ConnectionTestResult _result;

        public ConnectionTestReportWindow(ConnectionTestResult result)
        {
            InitializeComponent();
            _result = result;

            var target = string.IsNullOrWhiteSpace(result.TargetDescription)
                ? result.TargetHostOrIp
                : $"{result.TargetDescription} ({result.TargetHostOrIp})";

            TitleText.Text = $"Forbindelsesrapport — {target}";
            SubtitleText.Text = $"Startet {result.StartedAt:dd-MM-yyyy HH:mm:ss} · {result.DurationSeconds} sekunder";
            TargetSeriesLabel.Text = $"Mål · {target}";

            VerdictText.Text = result.Verdict;
            VerdictText.Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter()
                .ConvertFromString(result.VerdictColorHex)!;

            ReferenceSparkline.Values = result.ReferenceSeries.Values;
            ReferenceStatsText.Text = FormatStats(result.ReferenceSeries);

            TargetSparkline.Values = result.TargetSeries.Values;
            TargetStatsText.Text = FormatStats(result.TargetSeries);
        }

        private static string FormatStats(LatencySeries s) =>
            $"Avg: {s.AvgDisplay}   Max: {s.MaxDisplay}   Jitter: {s.JitterDisplay}   Tab: {s.LossDisplay}";

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Tekstfil (*.txt)|*.txt",
                FileName = $"m1scan-forbindelsestest-{_result.StartedAt:yyyy-MM-dd_HHmm}.txt"
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                File.WriteAllText(dialog.FileName, BuildReportText(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                CrashLog.Write("ConnectionTestReportWindow.Save", ex);
                MessageBox.Show(this, "Rapporten kunne ikke gemmes: " + ex.Message,
                    "Fejl", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string BuildReportText()
        {
            var target = string.IsNullOrWhiteSpace(_result.TargetDescription)
                ? _result.TargetHostOrIp
                : $"{_result.TargetDescription} ({_result.TargetHostOrIp})";

            var sb = new StringBuilder();
            sb.AppendLine("M1Scan — Forbindelsesrapport");
            sb.AppendLine(new string('=', 40));
            sb.AppendLine($"Mål:      {target}");
            sb.AppendLine($"Startet:  {_result.StartedAt:dd-MM-yyyy HH:mm:ss}");
            sb.AppendLine($"Varighed: {_result.DurationSeconds} sekunder");
            sb.AppendLine();
            sb.AppendLine("Konklusion:");
            sb.AppendLine(_result.Verdict);
            sb.AppendLine();
            AppendSeries(sb, "Internet-reference (1.1.1.1)", _result.ReferenceSeries);
            sb.AppendLine();
            AppendSeries(sb, $"Mål ({target})", _result.TargetSeries);
            return sb.ToString();
        }

        private static void AppendSeries(StringBuilder sb, string title, LatencySeries s)
        {
            sb.AppendLine(title);
            sb.AppendLine(new string('-', title.Length));
            sb.AppendLine($"  Gennemsnit: {s.AvgDisplay}");
            sb.AppendLine($"  Maks:       {s.MaxDisplay}");
            sb.AppendLine($"  Jitter:     {s.JitterDisplay}");
            sb.AppendLine($"  Pakketab:   {s.LossDisplay}");
            sb.AppendLine($"  Målinger:   {s.SampleCount.ToString(CultureInfo.InvariantCulture)}");
        }
    }
}
