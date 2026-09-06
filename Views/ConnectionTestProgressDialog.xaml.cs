using System;
using System.Windows;
using System.Windows.Input;

namespace M1Scan.Views
{
    public partial class ConnectionTestProgressDialog : Window
    {
        private int _remainingSeconds;
        private bool _testComplete;

        /// <summary>Rejses når brugeren trykker Stop/Esc, så ViewModel kan cancelere den løbende test.</summary>
        public event Action? OnCancelled;

        public ConnectionTestProgressDialog(string targetLabel, int totalSeconds)
        {
            InitializeComponent();
            TargetText.Text = $"Tester forbindelse til: {targetLabel}";
            _remainingSeconds = totalSeconds;
            ProgressBar.Maximum = totalSeconds;
            UpdateCountdown();

            // Show() giver ikke altid vinduet keyboard-fokus automatisk — uden dette
            // ville Esc gå til det bagvedliggende MainWindow i stedet for denne dialog.
            Loaded += (_, _) => Focus();

            // Fanger ALLE veje vinduet kan lukkes på (Alt+F4, Windows-luk, ikke kun
            // Stop-knappen/Esc) — ellers kører ping-løkken videre usynligt i baggrunden
            // i op til hele testens varighed, præcis den "frossen"-oplevelse dette
            // vindue skal forhindre.
            Closing += (_, _) =>
            {
                if (!_testComplete)
                    OnCancelled?.Invoke();
            };
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
            _testComplete = true;
            StatusText.Text = "Test gennemført — luk for at se rapport";
            ProgressBar.Value = ProgressBar.Maximum;
            CountdownText.Text = "00:00";
        }

        // Cancellation håndteres ensartet af Closing-handleren ovenfor, uanset om
        // vinduet lukkes via denne knap, Esc, Alt+F4 eller Windows-luk-knappen.
        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    }
}
