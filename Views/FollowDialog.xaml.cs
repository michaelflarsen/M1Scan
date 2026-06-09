using System;
using System.Windows;
using M1Scan.Services;

namespace M1Scan.Views
{
    public partial class FollowDialog : Window
    {
        private readonly IIpConfigService _ipConfigService;
        private readonly string _adapterSystemName;
        private string _prefix = string.Empty;

        public FollowDialog(
            IIpConfigService ipConfigService,
            string adapterSystemName,
            string adapterDescription,
            string currentIp,
            string suggestedIp,
            string subnetMask,
            string gateway)
        {
            InitializeComponent();
            _ipConfigService   = ipConfigService;
            _adapterSystemName = adapterSystemName;

            AdapterInfoText.Text = adapterDescription;
            CurrentIpText.Text   = currentIp;
            MaskBox.Text         = subnetMask;
            GatewayBox.Text      = gateway;

            var parts = suggestedIp.Split('.');
            if (parts.Length == 4)
            {
                _prefix          = $"{parts[0]}.{parts[1]}.{parts[2]}.";
                PrefixLabel.Text = _prefix;
                OctetBox.Text    = parts[3];
            }
            else
            {
                PrefixLabel.Text = string.Empty;
                OctetBox.Text    = suggestedIp;
            }
        }

        private async void Apply_Click(object sender, RoutedEventArgs e)
        {
            ErrorText.Visibility   = Visibility.Collapsed;
            ApplyButton.IsEnabled  = false;
            CancelButton.IsEnabled = false;

            try
            {
                var octetRaw = OctetBox.Text.Trim();

                if (!string.IsNullOrEmpty(_prefix))
                {
                    if (!int.TryParse(octetRaw, out int octet) || octet < 1 || octet > 254)
                    {
                        ShowError("Ugyldigt sidst octet — angiv et tal mellem 1 og 254.");
                        return;
                    }
                }

                string ip = _prefix + octetRaw;

                bool ok = await _ipConfigService.SetStaticIpAsync(
                    _adapterSystemName,
                    ip,
                    MaskBox.Text.Trim(),
                    GatewayBox.Text.Trim());

                if (ok)
                    DialogResult = true;
                else
                    ShowError("Kunne ikke anvende IP-ændringen.\nKontrollér at IP, maske og gateway er gyldige, og at appen kører som administrator.");
            }
            catch (Exception ex)
            {
                ShowError($"Fejl: {ex.Message}");
            }
            finally
            {
                ApplyButton.IsEnabled  = true;
                CancelButton.IsEnabled = true;
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void ShowError(string message)
        {
            ErrorText.Text       = message;
            ErrorText.Visibility = Visibility.Visible;
        }
    }
}
