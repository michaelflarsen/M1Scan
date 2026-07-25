using System.Windows;
using System.Windows.Input;

namespace M1Scan.Views
{
    /// <summary>
    /// Lille dialog til at give en enhed sit eget navn. Viser hvad det automatiske
    /// opslag fandt, så brugeren kan se hvad de overstyrer.
    /// </summary>
    public partial class RenameDeviceDialog : Window
    {
        /// <summary>Det indtastede navn. Tom streng betyder "fjern overstyringen".</summary>
        public string? EnteredName { get; private set; }

        public RenameDeviceDialog(string detectedName, string currentCustomName,
                                   string ipAddress, string macAddress)
        {
            InitializeComponent();

            IpText.Text  = ipAddress;
            MacText.Text = macAddress;

            // Vis det automatisk fundne navn — men kun hvis det er et rigtigt navn og
            // ikke bare IP'en gentaget, og ikke brugerens eget navn spejlet tilbage.
            DetectedText.Text =
                string.IsNullOrWhiteSpace(detectedName) || detectedName == ipAddress || detectedName == currentCustomName
                    ? "(intet navn fundet)"
                    : detectedName;

            NameBox.Text = currentCustomName;
            NameBox.SelectAll();

            // Fokus i tekstfeltet når dialogen åbner, så man kan skrive med det samme.
            Loaded += (_, _) => NameBox.Focus();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            EnteredName = NameBox.Text.Trim();
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        // Enter gemmer. IsDefault på knappen dækker det normalt, men TextBox'en
        // sluger tasten i nogle temaer.
        private void NameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            Save_Click(sender, e);
            e.Handled = true;
        }
    }
}
