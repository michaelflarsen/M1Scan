using System;
using System.Windows;
using M1Scan.Services;

namespace M1Scan.Views
{
    public partial class FollowDialog : Window
    {
        private readonly IIpConfigService _ipConfigService;
        private readonly string _adapterSystemName;

        /// <param name="targetEntryIp">The watched entry's IP — used only as a context hint label.</param>
        public FollowDialog(
            IIpConfigService ipConfigService,
            string adapterSystemName,
            string adapterDescription,
            string currentIp,
            string suggestedIp,
            string subnetMask,
            string gateway,
            string targetEntryIp)
        {
            InitializeComponent();
            _ipConfigService   = ipConfigService;
            _adapterSystemName = adapterSystemName;

            AdapterInfoText.Text = adapterDescription;
            CurrentIpText.Text   = currentIp;
            IpBox.Text           = suggestedIp;
            MaskBox.Text         = subnetMask;
            GatewayBox.Text      = gateway;

            // Fix 3: show context — the entry IP is what motivated opening this dialog,
            // but the suggested IP is always in the same /24 as the active adapter.
            ContextHintText.Text = string.IsNullOrWhiteSpace(targetEntryIp)
                ? "Ny IP sættes på den aktive adapter."
                : $"For at nå {targetEntryIp} skal du være på samme subnet.";
        }

        private async void Apply_Click(object sender, RoutedEventArgs e)
        {
            ErrorText.Visibility   = Visibility.Collapsed;
            ApplyButton.IsEnabled  = false;
            CancelButton.IsEnabled = false;

            try
            {
                bool ok = await _ipConfigService.SetStaticIpAsync(
                    _adapterSystemName,
                    IpBox.Text.Trim(),
                    MaskBox.Text.Trim(),
                    GatewayBox.Text.Trim());

                if (ok)
                {
                    DialogResult = true;
                }
                else
                {
                    ShowError("Kunne ikke anvende IP-ændringen.\nKontrollér at IP, maske og gateway er gyldige, og at appen kører som administrator.");
                }
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
