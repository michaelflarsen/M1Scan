using System.Windows;
using System.Windows.Input;

namespace M1Scan.Views
{
    /// <summary>Lille dialog der beder om varigheden af en forbindelsestest.</summary>
    public partial class ConnectionTestDialog : Window
    {
        public int DurationSeconds { get; private set; }

        public ConnectionTestDialog(string targetLabel)
        {
            InitializeComponent();
            DescriptionText.Text = $"Pinger \"{targetLabel}\" og en fast internet-reference (1.1.1.1) samtidig i den valgte periode, så du kan se om udsving skyldes din egen forbindelse eller enheden/internettet efter den.";
            Loaded += (_, _) => { DurationBox.Focus(); DurationBox.SelectAll(); };
        }

        private void Start_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(DurationBox.Text.Trim(), out int seconds) || seconds < 5 || seconds > 300)
            {
                ErrorText.Text = "Angiv et tal mellem 5 og 300 sekunder.";
                ErrorText.Visibility = Visibility.Visible;
                return;
            }

            DurationSeconds = seconds;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void DurationBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            Start_Click(sender, e);
            e.Handled = true;
        }
    }
}
