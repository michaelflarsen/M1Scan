using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using M1Scan.Models;
using M1Scan.Services;
using M1Scan.Utils;

namespace M1Scan.Views
{
    /// <summary>
    /// Viser en enheds port-historik (hvornår 80/443/8080/502 er set skifte
    /// mellem åben og lukket). Data hentes af NetworkScanViewModel og gives med
    /// i konstruktøren; "Ryd historik" skriver selv til HistoryService, da det
    /// er den eneste handling i denne dialog der ændrer noget.
    /// </summary>
    public partial class PortHistoryDialog : Window
    {
        private readonly IHistoryService _historyService;
        private readonly string _mac;
        private readonly string _deviceLabel;

        private class PortEventRow
        {
            public string TimestampText { get; init; } = string.Empty;
            public string PortText { get; init; } = string.Empty;
            public string StatusText { get; init; } = string.Empty;
        }

        public PortHistoryDialog(string deviceLabel, string mac, IReadOnlyList<PortEvent> events, IHistoryService historyService)
        {
            InitializeComponent();

            _historyService = historyService;
            _mac = mac;
            _deviceLabel = deviceLabel;

            TitleText.Text = $"Port-historik — {deviceLabel}";
            Render(events);
        }

        private void Render(IReadOnlyList<PortEvent> events)
        {
            var rows = events
                .Select(e => new PortEventRow
                {
                    TimestampText = e.Timestamp.LocalDateTime.ToString("dd/MM/yyyy HH:mm:ss"),
                    PortText = e.Port.ToString(),
                    StatusText = e.IsOpen ? "Åbnede" : "Lukkede",
                })
                .ToList();

            EventsList.ItemsSource = rows;
            EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ClearButton.IsEnabled = rows.Count > 0;
        }

        // async void: kun WPF's event-plumbing kan kalde denne, så en undtagelse der
        // undslipper rammer Dispatcher'en direkte — pak den i try/catch (samme regel
        // som CLAUDE.md sætter for DispatcherTimer.Tick/event-handlere).
        private async void Clear_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var result = MessageBox.Show(this,
                    $"Slet al port-historik for {_deviceLabel}? Dette kan ikke fortrydes.",
                    "Ryd historik", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (result != MessageBoxResult.Yes) return;

                ClearButton.IsEnabled = false;
                await _historyService.ClearPortHistoryAsync(_mac);
                Render(Array.Empty<PortEvent>());
            }
            catch (Exception ex)
            {
                CrashLog.Write("PortHistoryDialog.Clear_Click", ex);
                MessageBox.Show(this, $"Kunne ikke rydde historik: {ex.Message}", "Fejl",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                ClearButton.IsEnabled = true;
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    }
}
