using System;
using System.Windows;
using System.Windows.Input;

namespace M1Scan.Views
{
    public partial class ConnectionTestProgressDialog : Window
    {
        private int _remainingSeconds;

        public ConnectionTestProgressDialog(string targetLabel, int totalSeconds)
        {
            InitializeComponent();
            TargetText.Text = $"Tester forbindelse til: {targetLabel}";
            _remainingSeconds = totalSeconds;
            ProgressBar.Maximum = totalSeconds;
            UpdateCountdown();
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Cancel_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
        }

        public void UpdateProgress(int elapsedSeconds, int targetPings, int targetReplies, int referencePings, int referenceReplies)
        {
            ProgressBar.Value = elapsedSeconds;
            TargetStatsText.Text = $"{targetReplies} / {targetPings} pings";
            ReferenceStatsText.Text = $"{referenceReplies} / {referencePings} pings";
            _remainingSeconds = (int)ProgressBar.Maximum - elapsedSeconds;
            UpdateCountdown();
        }

        private void UpdateCountdown()
        {
            int minutes = _remainingSeconds / 60;
            int seconds = _remainingSeconds % 60;
            CountdownText.Text = $"{minutes:D2}:{seconds:D2}";
        }

        public void OnTestComplete()
        {
            StatusText.Text = "Test gennemført — luk for at se rapport";
            ProgressBar.Value = ProgressBar.Maximum;
            CountdownText.Text = "00:00";
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
