using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using M1Scan.Models;
using M1Scan.Services;
using M1Scan.Utils;

namespace M1Scan.ViewModels
{
    public class TracerouteViewModel : ObservableObject
    {
        private readonly ITracerouteService _tracerouteService;
        private readonly IGeoIpService _geoIpService;
        private readonly IHistoryService _historyService;
        private CancellationTokenSource? _traceCts;

        private ObservableCollection<TraceHopInfo> _hops = new();
        private string _targetInput = "8.8.8.8";
        private bool _isTracing;
        private bool _isProbing;
        private bool _isTraceComplete;
        private string _statusMessage = "Ready to trace";
        private int _traceProgress;
        private double _maxLatency = 100;
        private bool _geoLookupEnabled; // default FRA — se GeoLookupEnabled

        public ObservableCollection<TraceHopInfo> Hops
        {
            get => _hops;
            set => SetProperty(ref _hops, value);
        }

        public string TargetInput
        {
            get => _targetInput;
            set => SetProperty(ref _targetInput, value);
        }

        public bool IsTracing
        {
            get => _isTracing;
            set { if (SetProperty(ref _isTracing, value)) OnPropertyChanged(nameof(CanEditTarget)); }
        }

        public bool IsTraceComplete
        {
            get => _isTraceComplete;
            set => SetProperty(ref _isTraceComplete, value);
        }

        public bool IsProbing
        {
            get => _isProbing;
            set { if (SetProperty(ref _isProbing, value)) OnPropertyChanged(nameof(CanEditTarget)); }
        }

        /// <summary>Målfeltet må ikke redigeres mens en trace ELLER en løbende probe
        /// kører — en løbende probe mærker sine historik-samples med det mål der var
        /// aktivt da probe'en startede (se ToggleProbingAsync's probedTarget), så at
        /// redigere feltet midt i en probe ville være misvisende for brugeren selvom
        /// det ikke længere kan korrumpere historikken.</summary>
        public bool CanEditTarget => !IsTracing && !IsProbing;

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public int TraceProgress
        {
            get => _traceProgress;
            set => SetProperty(ref _traceProgress, value);
        }

        public double MaxLatency
        {
            get => _maxLatency;
            set => SetProperty(ref _maxLatency, value);
        }

        /// <summary>
        /// Slår opslag af land/ASN pr. hop til. Default FRA: opslaget sender rutens
        /// offentlige IP'er — altså brugerens vej ud gennem ISP'en — til ip-api.com,
        /// og gratisniveauet kan kun nås over HTTP (ukrypteret). Det skal være
        /// brugerens eget valg, ikke en skjult sideeffekt af at køre en traceroute.
        /// Valget huskes i %APPDATA%\M1Scan\traceroute_settings.json.
        /// </summary>
        public bool GeoLookupEnabled
        {
            get => _geoLookupEnabled;
            set
            {
                if (!SetProperty(ref _geoLookupEnabled, value)) return;
                _geoIpService.IsEnabled = value;
                OnPropertyChanged(nameof(GeoLookupHint));
                SaveSettings();
            }
        }

        public string GeoLookupHint => _geoLookupEnabled
            ? "Land/ASN slås op via ip-api.com over ukrypteret HTTP — rutens IP'er sendes til tredjepart."
            : "Land/ASN vises ikke. Intet forlader maskinen.";

        public ICommand TraceCommand { get; }
        public ICommand ProbeLoopCommand { get; }
        public ICommand CancelTraceCommand { get; }
        public AsyncRelayCommand LoadHistoryCommand { get; }

        /// <summary>Tidligere gemte probe-samples for det aktuelle mål (seneste 24 timer), hentet fra HistoryService.</summary>
        public ObservableCollection<TraceSample> HistoricalSamples
        {
            get => _historicalSamples;
            set => SetProperty(ref _historicalSamples, value);
        }
        private ObservableCollection<TraceSample> _historicalSamples = new();

        public TracerouteViewModel(ITracerouteService tracerouteService, IGeoIpService geoIpService, IHistoryService historyService)
        {
            _tracerouteService = tracerouteService;
            _geoIpService = geoIpService;
            _historyService = historyService;

            LoadSettings();
            _geoIpService.IsEnabled = _geoLookupEnabled;

            TraceCommand = new RelayCommand(_ => _ = ExecuteTraceAsync(), _ => !IsTracing && !IsProbing);
            ProbeLoopCommand = new RelayCommand(_ => _ = ToggleProbingAsync(), _ => IsTraceComplete || IsProbing);
            CancelTraceCommand = new RelayCommand(_ => CancelTrace(), _ => IsTracing || IsProbing);
            LoadHistoryCommand = new AsyncRelayCommand(_ => LoadHistoryAsync(), onError: ex => StatusMessage = $"Fejl: {ex.Message}");
        }

        private async Task LoadHistoryAsync()
        {
            if (string.IsNullOrWhiteSpace(TargetInput)) return;

            var to = DateTimeOffset.UtcNow;
            var from = to - TimeSpan.FromHours(24);
            var samples = await _historyService.GetTraceSamplesAsync(TargetInput, from, to);
            HistoricalSamples = new ObservableCollection<TraceSample>(samples);
        }

        // ── Persistering af geo-samtykket ────────────────────────────────────

        private sealed class TracerouteSettings
        {
            public int version { get; set; } = 1;
            public bool geoLookupEnabled { get; set; }
        }

        private static readonly string SettingsPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "M1Scan", "traceroute_settings.json");

        private void LoadSettings()
        {
            try
            {
                if (!System.IO.File.Exists(SettingsPath)) return;
                var json = System.IO.File.ReadAllText(SettingsPath);
                var s = System.Text.Json.JsonSerializer.Deserialize<TracerouteSettings>(json);
                if (s != null) _geoLookupEnabled = s.geoLookupEnabled;
            }
            catch { /* korrupt fil → behold default (fra) */ }
        }

        private void SaveSettings()
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SettingsPath)!);
                var json = System.Text.Json.JsonSerializer.Serialize(
                    new TracerouteSettings { geoLookupEnabled = _geoLookupEnabled },
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

                var tmp = SettingsPath + ".tmp";
                System.IO.File.WriteAllText(tmp, json);
                System.IO.File.Move(tmp, SettingsPath, overwrite: true);
            }
            catch { /* kan ikke gemme → valget gælder kun denne session */ }
        }

        private async Task ExecuteTraceAsync()
        {
            if (string.IsNullOrWhiteSpace(TargetInput))
            {
                StatusMessage = "Angiv værtsnavn eller IP-adresse";
                return;
            }

            // Stop any active probe before starting new trace
            if (IsProbing)
            {
                _traceCts?.Cancel();
                await Task.Delay(100); // Brief delay to allow probe cancellation
            }

            IsTracing = true;
            IsTraceComplete = false;
            TraceProgress = 0;
            Hops.Clear();
            StatusMessage = "Sporer rute...";

            _traceCts?.Dispose();
            _traceCts = new CancellationTokenSource();

            try
            {
                await foreach (var hop in _tracerouteService.TraceRouteAsync(
                    TargetInput,
                    maxHops: 32,
                    timeoutMs: 600,
                    ct: _traceCts.Token))
                {
                    if (_traceCts.Token.IsCancellationRequested)
                        break;

                    // Marshal UI updates to dispatcher thread — Hops.Add must run on UI thread
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        Hops.Add(hop);
                        TraceProgress = (int)(100.0 * Hops.Count / 32);
                        UpdateMaxLatency();
                    });
                }

                // Trace completed — now enrich in parallel (DNS, Country, ASN)
                if (Hops.Count > 0 && !_traceCts.Token.IsCancellationRequested)
                {
                    await EnrichHopsAsync(_traceCts.Token);
                    StatusMessage = $"Sporing fuldført — {Hops.Count} hop'er";
                }
                else if (Hops.Count == 0)
                {
                    StatusMessage = "Sporing fuldført — ingen svar";
                }

                // Vis evt. tidligere probe-data for dette mål, så en genkendt rute
                // straks viser sin historik uden en ekstra manuel handling.
                _ = LoadHistoryAsync();
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Sporing annulleret";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Fejl: {ex.Message}";
            }
            finally
            {
                IsTracing = false;
                IsTraceComplete = Hops.Count > 0;
                _traceCts?.Dispose();
                _traceCts = null;
            }
        }

        private async Task EnrichHopsAsync(CancellationToken ct)
        {
            if (Hops.Count == 0)
                return;

            try
            {
                // Update status while enriching
                await Application.Current.Dispatcher.InvokeAsync(() =>
                    StatusMessage = "🔄 Slår værtsnavne, land og ASN op...");

                // Phase 1: Parallel reverse-DNS resolution
                var dnsTask = Task.WhenAll(
                    Hops.Where(h => !string.IsNullOrEmpty(h.IpAddress) && string.IsNullOrEmpty(h.HostName))
                        .Select(h => ResolveAndSetHostNameAsync(h, ct)));

                await dnsTask;

                if (ct.IsCancellationRequested)
                    return;

                // Phase 2: Batch geo/ASN lookup for public IPs
                var publicIps = Hops
                    .Where(h => !string.IsNullOrEmpty(h.IpAddress))
                    .Select(h => h.IpAddress!)
                    .ToList();

                if (publicIps.Count > 0)
                {
                    var geoData = await _geoIpService.LookupBatchAsync(publicIps, ct);

                    // Marshal geo data updates to UI thread
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        foreach (var hop in Hops.Where(h => !string.IsNullOrEmpty(h.IpAddress)))
                        {
                            if (geoData.TryGetValue(hop.IpAddress!, out var result))
                            {
                                hop.Country = result.Country;
                                hop.Asn = result.Asn;
                            }
                            else
                            {
                                // Private IP — mark as such
                                hop.Country = "—";
                            }
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
                // Enrichment was cancelled — that's OK
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"EnrichHopsAsync error: {ex.Message}");
                // Don't crash the trace just because enrichment failed
            }
        }

        private async Task ResolveAndSetHostNameAsync(TraceHopInfo hop, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(hop.IpAddress))
                return;

            try
            {
                var hostName = await _tracerouteService.ResolveHostNameAsync(hop.IpAddress, 1500, ct);
                if (!string.IsNullOrEmpty(hostName))
                {
                    // Marshal UI update to dispatcher thread
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                        hop.HostName = hostName);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // DNS failed — just skip hostname for this hop
            }
        }

        private void UpdateMaxLatency()
        {
            if (Hops.Count == 0)
            {
                MaxLatency = 100;
                return;
            }

            // Only recalc if the last hop might exceed current max
            double currentMax = MaxLatency / 1.2; // Back out the 20% padding
            double lastHopAvg = Hops[Hops.Count - 1].LatencySeries.Avg;

            // If last hop is lower, max hasn't changed
            if (lastHopAvg <= currentMax && MaxLatency >= 100)
                return;

            // Recalc max across all hops
            double max = 0;
            foreach (var hop in Hops)
            {
                if (hop.LatencySeries.Avg > max)
                    max = hop.LatencySeries.Avg;
            }

            // Add 20% padding
            MaxLatency = Math.Max(100, max * 1.2);
        }

        private async Task ToggleProbingAsync()
        {
            if (IsProbing)
            {
                CancelTrace();
                return;
            }

            if (Hops.Count == 0)
                return;

            IsProbing = true;
            StatusMessage = "🔄 Løbende probe kører...";
            _traceCts?.Dispose();
            _traceCts = new CancellationTokenSource();

            try
            {
                var hopsList = new System.Collections.Generic.List<TraceHopInfo>(Hops);

                // Fastfryses ved probe-start: TargetInput er en live UI-bunden property,
                // og hvis brugeren skriver et nyt mål i tekstfeltet mens denne probe
                // (mod DET GAMLE mål) stadig kører, skal historik-rækkerne nedenfor
                // stadig mærkes med det mål der faktisk blev probet — ikke det brugeren
                // efterfølgende skrev.
                var probedTarget = TargetInput;

                await foreach (var hop in _tracerouteService.ContinuousProbeAsync(
                    hopsList,
                    timeoutMs: 600,
                    delayBetweenHopsMs: 2000,
                    ct: _traceCts.Token))
                {
                    if (_traceCts.Token.IsCancellationRequested)
                        break;

                    // Check if hop still exists in Hops collection (handles case where user clears hops during probing)
                    var hopStillValid = Hops.Any(h => h.HopNumber == hop.HopNumber && h.IpAddress == hop.IpAddress);
                    if (!hopStillValid)
                    {
                        // Hops collection was cleared or modified — stop probing gracefully
                        StatusMessage = "Probe stoppet (rute blev ændret)";
                        break;
                    }

                    // Marshal MaxLatency update to UI thread
                    await Application.Current.Dispatcher.InvokeAsync(() => UpdateMaxLatency());

                    // Fire-and-forget: probe-løkken må aldrig blive langsommere fordi
                    // historik-skrivningen er langsom — HistoryService fanger selv sine
                    // fejl. hop.LatencySeries.Current er den sample der lige blev
                    // tilføjet af ContinuousProbeAsync.
                    _ = _historyService.RecordTraceSampleAsync(
                        probedTarget, hop.HopNumber, hop.IpAddress,
                        DateTimeOffset.UtcNow, hop.LatencySeries.Current);
                }

                StatusMessage = $"Probe stoppet";
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Probe stoppet";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Fejl ved probe: {ex.Message}";
            }
            finally
            {
                IsProbing = false;
                _traceCts?.Dispose();
                _traceCts = null;
            }
        }

        private void CancelTrace()
        {
            _traceCts?.Cancel();
        }
    }
}
