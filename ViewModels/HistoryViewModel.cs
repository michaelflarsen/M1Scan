using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using M1Scan.Models;
using M1Scan.Services;
using M1Scan.Utils;

namespace M1Scan.ViewModels
{
    /// <summary>Visningsklar netværksscore-sample til tidslinjen.</summary>
    public class HistorySamplePoint
    {
        public string Time  { get; init; } = string.Empty;
        public int?   Score { get; init; }
        public string Grade { get; init; } = "—";
        public string WanDisplay { get; init; } = "—";
    }

    /// <summary>Visningsklart scan-sammendrag.</summary>
    public class ScanSummaryEntry
    {
        public string Time    { get; init; } = string.Empty;
        public string Summary { get; init; } = string.Empty;
    }

    /// <summary>Visningsklar enheds-hændelse.</summary>
    public class DeviceEventEntry
    {
        public string Time    { get; init; } = string.Empty;
        public string Summary { get; init; } = string.Empty;
    }

    /// <summary>
    /// Historik-siden: viser data indsamlet af IHistoryService's baggrundssampler.
    /// Selve indsamlingen kører uafhængigt af denne ViewModel (startes i
    /// MainViewModel's composition root) — denne side henter blot et snapshot af
    /// de seneste 24 timer, hver gang den aktiveres.
    /// </summary>
    public class HistoryViewModel : ObservableObject, IActivatablePage
    {
        private readonly IHistoryService _historyService;

        private ObservableCollection<HistorySamplePoint> _samples = new();
        private ObservableCollection<ScanSummaryEntry>   _scans   = new();
        private ObservableCollection<DeviceEventEntry>   _events  = new();
        private bool   _isLoading;
        private string _statusMessage = "—";

        public ObservableCollection<HistorySamplePoint> Samples
        {
            get => _samples;
            set => SetProperty(ref _samples, value);
        }

        public ObservableCollection<ScanSummaryEntry> Scans
        {
            get => _scans;
            set => SetProperty(ref _scans, value);
        }

        public ObservableCollection<DeviceEventEntry> Events
        {
            get => _events;
            set => SetProperty(ref _events, value);
        }

        public bool IsLoading
        {
            get => _isLoading;
            set => SetProperty(ref _isLoading, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public AsyncRelayCommand RefreshCommand { get; }

        public HistoryViewModel(IHistoryService historyService)
        {
            _historyService = historyService;
            RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync(), _ => !IsLoading, OnCommandError);
        }

        private void OnCommandError(Exception ex) => StatusMessage = $"Fejl: {ex.Message}";

        // ── IActivatablePage ─────────────────────────────────────────────────
        public void OnActivated()   => RefreshCommand.Execute(null);
        public void OnDeactivated() { /* ingen løbende baggrundsarbejde på selve siden */ }

        private async Task RefreshAsync()
        {
            IsLoading = true;
            try
            {
                var to   = DateTimeOffset.UtcNow;
                var from = to - TimeSpan.FromHours(24);

                var samplesTask = _historyService.GetSamplesAsync(from, to);
                var scansTask   = _historyService.GetScansAsync(from, to);
                var eventsTask  = _historyService.GetDeviceEventsAsync(from, to);

                await Task.WhenAll(samplesTask, scansTask, eventsTask);

                Samples = new ObservableCollection<HistorySamplePoint>(
                    (await samplesTask)
                        .OrderByDescending(s => s.Timestamp)
                        .Take(200)
                        .Select(s => new HistorySamplePoint
                        {
                            Time  = s.Timestamp.ToLocalTime().ToString("HH:mm:ss"),
                            Score = s.HealthScore,
                            Grade = s.HealthGrade ?? "—",
                            WanDisplay = s.WanAvgMs.HasValue ? $"{s.WanAvgMs.Value:F0} ms" : "—"
                        }));

                Scans = new ObservableCollection<ScanSummaryEntry>(
                    (await scansTask).Select(sc => new ScanSummaryEntry
                    {
                        Time    = sc.Timestamp.ToLocalTime().ToString("dd/MM HH:mm"),
                        Summary = sc.WasComplete
                            ? $"{sc.ReachableCount} enheder online (ud af {sc.HostCount} fundet)"
                            : $"Ufuldstændig scanning — {sc.ReachableCount} enheder fundet"
                    }));

                Events = new ObservableCollection<DeviceEventEntry>(
                    (await eventsTask).Select(ev => new DeviceEventEntry
                    {
                        Time    = ev.Timestamp.ToLocalTime().ToString("dd/MM HH:mm"),
                        Summary = ev.EventType == DeviceEventType.NewDevice
                            ? $"Ny enhed: {(string.IsNullOrWhiteSpace(ev.Name) ? ev.Mac : ev.Name)}"
                            : $"{ev.Mac}: {ev.EventType}"
                    }));

                StatusMessage = $"Opdateret — {Samples.Count} målinger, {Scans.Count} scans, {Events.Count} hændelser (seneste 24t)";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Fejl: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }
    }
}
