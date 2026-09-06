using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using M1Scan.Models;
using M1Scan.Services;
using M1Scan.Utils;
using M1Scan.Views;

namespace M1Scan.ViewModels
{
    /// <summary>
    /// Ping monitor: overvåger flere mål samtidig (IP/hostname), hver med egen
    /// sparkline, uptime %, min/avg/max/jitter/loss. Målene selv (hvad brugeren vil
    /// overvåge) gemmes i en lille JSON-fil; de faktiske målinger over tid gemmes i
    /// HistoryService's ping_samples-tabel, så uptime% kan vises over dage, ikke kun
    /// siden appen startede.
    ///
    /// Pinger KUN mens siden er synlig (IActivatablePage) — samme begrundelse som
    /// WorkspaceViewModel/HomeViewModel: ingen baggrundstrafik brugeren ikke har bedt om.
    /// </summary>
    public class PingMonitorViewModel : ObservableObject, IDisposable, IActivatablePage
    {
        private static readonly string PersistPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "M1Scan", "ping_monitor_targets.json");

        // Fast internet-reference til "min forbindelse vs. mål"-testen. Cloudflares
        // resolver: stabil, hurtig og uafhængig af brugerens eget netværk/ISP-DNS.
        private const string ReferenceHost = "1.1.1.1";

        private readonly IHistoryService _historyService;
        private readonly DispatcherTimer _pingTimer;
        private readonly DispatcherTimer _uptimeTimer;

        private string _newHostInput = string.Empty;
        private string _newDescriptionInput = string.Empty;
        private int _intervalSeconds = 3;
        private string _statusMessage = "Klar";
        private int _pingInFlight;

        public ObservableCollection<PingMonitorTarget> Targets { get; } = new();

        public string NewHostInput
        {
            get => _newHostInput;
            set => SetProperty(ref _newHostInput, value);
        }

        public string NewDescriptionInput
        {
            get => _newDescriptionInput;
            set => SetProperty(ref _newDescriptionInput, value);
        }

        public int IntervalSeconds
        {
            get => _intervalSeconds;
            set
            {
                int clamped = Math.Clamp(value, 1, 60);
                if (SetProperty(ref _intervalSeconds, clamped))
                    _pingTimer.Interval = TimeSpan.FromSeconds(clamped);
            }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public RelayCommand AddTargetCommand { get; }
        public RelayCommand RemoveTargetCommand { get; }
        public AsyncRelayCommand TestConnectionCommand { get; }

        public PingMonitorViewModel(IHistoryService historyService)
        {
            _historyService = historyService;

            // Timerne startes IKKE her — kun i OnActivated(), se IActivatablePage.
            _pingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_intervalSeconds) };
            _pingTimer.Tick += OnPingTick;

            // Uptime% læses fra HistoryService (SQLite) og opdateres derfor sjældnere
            // end selve pingene — et helt query pr. mål hvert 3. sekund er overkill
            // for et tal der kun skal vise en langsom trend, ikke live-status.
            _uptimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _uptimeTimer.Tick += OnUptimeTick;

            AddTargetCommand = new RelayCommand(_ => AddTarget());
            RemoveTargetCommand = new RelayCommand(param => RemoveTarget(param as PingMonitorTarget));
            // canExecute er pr.-parameter (target.IsTesting), ikke pr.-kommando, så en
            // igangværende test på ét kort ikke deaktiverer knappen på alle de andre —
            // AsyncRelayCommand's egen IsRunning-lås dækker kun genindtræden på samme kald.
            TestConnectionCommand = new AsyncRelayCommand(
                param => TestConnectionAsync(param as PingMonitorTarget),
                param => (param as PingMonitorTarget)?.IsTesting != true,
                ex => StatusMessage = "Test fejlede: " + ex.Message);

            LoadTargets();
        }

        private void AddTarget()
        {
            var host = NewHostInput.Trim();
            if (string.IsNullOrWhiteSpace(host)) return;

            if (Targets.Any(t => t.HostOrIp.Equals(host, StringComparison.OrdinalIgnoreCase)))
            {
                StatusMessage = $"'{host}' overvåges allerede";
                return;
            }

            var target = new PingMonitorTarget { HostOrIp = host, Description = NewDescriptionInput.Trim() };
            Targets.Add(target);
            _ = _historyService.UpsertPingTargetAsync(target.Id, target.HostOrIp, target.Description);

            NewHostInput = string.Empty;
            NewDescriptionInput = string.Empty;
            SaveTargets();
        }

        private void RemoveTarget(PingMonitorTarget? target)
        {
            if (target == null) return;
            Targets.Remove(target);
            _ = _historyService.RemovePingTargetAsync(target.Id);
            SaveTargets();
        }

        /// <summary>
        /// Kort, samtidig ping-burst mod målet og en fast internet-reference (1.1.1.1).
        /// Kører uafhængigt af den løbende overvågnings-timer, så intervallet ikke
        /// påvirker testens tæthed. Formålet er at kunne skelne "min forbindelse er
        /// dårlig" (begge serier rammes) fra "målet/vejen til det er dårlig" (kun
        /// mål-serien rammes).
        /// </summary>
        private async Task TestConnectionAsync(PingMonitorTarget? target)
        {
            if (target == null) return;

            var inputDialog = new ConnectionTestDialog(
                string.IsNullOrWhiteSpace(target.Description) ? target.HostOrIp : target.Description)
            { Owner = Application.Current?.MainWindow };

            if (inputDialog.ShowDialog() != true) return;

            int seconds = inputDialog.DurationSeconds;
            target.IsTesting = true;
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();

            var cts = new System.Threading.CancellationTokenSource();
            var progressDialog = new ConnectionTestProgressDialog(
                string.IsNullOrWhiteSpace(target.Description) ? target.HostOrIp : target.Description,
                seconds)
            { Owner = Application.Current?.MainWindow };

            progressDialog.OnCancelled += () => cts.Cancel();
            progressDialog.Show();

            try
            {
                var referenceSeries = new LatencySeries { Label = "Internet (1.1.1.1)" };
                var targetSeries = new LatencySeries { Label = target.HostOrIp };

                var referenceStats = new ConnectionTestStats();
                var targetStats = new ConnectionTestStats();

                var startedAt = DateTime.Now;
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
                int elapsedSeconds = 0;

                while (DateTime.UtcNow < deadline && !cts.Token.IsCancellationRequested)
                {
                    var nextTick = DateTime.UtcNow + TimeSpan.FromSeconds(1);

                    var refTask = PingOnceAsync(ReferenceHost);
                    var targetTask = PingOnceAsync(target.HostOrIp);
                    await Task.WhenAll(refTask, targetTask);

                    referenceSeries.Add(refTask.Result);
                    targetSeries.Add(targetTask.Result);
                    referenceStats.Add(refTask.Result);
                    targetStats.Add(targetTask.Result);

                    elapsedSeconds++;
                    // Opdater progress-dialog på UI-tråden
                    if (Application.Current != null)
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                            progressDialog.UpdateProgress(elapsedSeconds,
                                (int)targetStats.Sent, (int)targetStats.Replies,
                                (int)referenceStats.Sent, (int)referenceStats.Replies));

                    var remaining = nextTick - DateTime.UtcNow;
                    if (remaining > TimeSpan.Zero)
                        await Task.Delay(remaining, cts.Token);
                }

                if (cts.Token.IsCancellationRequested)
                {
                    progressDialog.Close();
                    return;
                }

                var result = new ConnectionTestResult
                {
                    TargetHostOrIp = target.HostOrIp,
                    TargetDescription = target.Description,
                    StartedAt = startedAt,
                    DurationSeconds = seconds,
                    IntervalSeconds = 1,
                    ReferenceSeries = referenceSeries,
                    TargetSeries = targetSeries,
                    ReferenceStats = referenceStats,
                    TargetStats = targetStats,
                    Verdict = BuildVerdictText(referenceStats, targetStats, out var verdictColor),
                    VerdictColorHex = verdictColor,
                    TargetStatusLabel = BuildTargetStatusLabel(targetStats, out var statusColor),
                    TargetStatusColorHex = statusColor,
                    ReferenceUnreliable = referenceStats.HasProblem
                };

                // Marker testen som fuldført
                if (Application.Current != null)
                    await Application.Current.Dispatcher.InvokeAsync(() => progressDialog.OnTestComplete());

                var reportWindow = new ConnectionTestReportWindow(result)
                { Owner = Application.Current?.MainWindow };
                progressDialog.Close();
                reportWindow.ShowDialog();
            }
            catch (OperationCanceledException)
            {
                progressDialog.Close();
            }
            finally
            {
                cts.Dispose();
                target.IsTesting = false;
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }

        /// <summary>
        /// Sammenligner reference- og mål-serien og formulerer en dansk konklusion.
        /// Tærsklerne er bevidst grove (denne test skal pege i en retning, ikke
        /// erstatte en rigtig netværksanalyse): "problem" på en serie betyder
        /// mærkbart tab eller jitter, ikke blot at avg-latency er høj (høj avg kan
        /// være helt normalt for et geografisk fjernt mål).
        /// </summary>
        private static string BuildVerdictText(ConnectionTestStats reference, ConnectionTestStats target, out string colorHex)
        {
            bool refProblem = reference.HasProblem;
            bool targetProblem = target.HasProblem;

            if (target.LossPercent >= 95)
            {
                colorHex = "#F44336";
                return refProblem
                    ? "Målet svarer stort set ikke — men din internetforbindelse er også ustabil lige nu, så det kan skyldes din egen linje."
                    : "Målet svarer stort set ikke, mens internet-referencen er stabil. Problemet ligger ved målet selv (enheden er nede, eller noget på vejen til den blokerer), ikke ved din forbindelse.";
            }

            if (refProblem && targetProblem)
            {
                colorHex = "#FF9800";
                return "Både internet-referencen og målet viser udsving/tab i samme periode. Det peger på din egen forbindelse (router, WiFi eller ISP) som årsagen — ikke på målet.";
            }

            if (targetProblem && !refProblem)
            {
                colorHex = "#FF9800";
                return "Internet-referencen er stabil, men målet viser udsving/tab. Problemet sidder efter din forbindelse — ved målet selv, eller på vejen derhen.";
            }

            if (refProblem && !targetProblem)
            {
                colorHex = "#FF9800";
                return "Målet er faktisk stabilt, men din internet-reference viser udsving. Din forbindelse til internettet er muligvis mere ustabil end forbindelsen til dette lokale mål.";
            }

            colorHex = "#4CAF50";
            return "Begge forbindelser er stabile i testperioden. Ingen tegn på problemer, hverken lokalt eller hos målet.";
        }

        /// <summary>
        /// Kort statusetiket om MÅLET ALENE, til rapportens header.
        ///
        /// Deler tærskel med <see cref="BuildVerdictText"/> via
        /// <see cref="ConnectionTestStats.HasProblem"/>, så de to ikke kan drifte fra
        /// hinanden ved senere rettelser. De kan dog godt vise forskellig FARVE — og
        /// det er med vilje: verdict'en bedømmer også brugerens egen linje, så et
        /// stabilt mål kan stå grønt i headeren mens verdict'en er orange, fordi
        /// brugerens eget internet var ustabilt. Headeren udtaler sig kun om enheden.
        /// </summary>
        private static string BuildTargetStatusLabel(ConnectionTestStats target, out string colorHex)
        {
            if (target.Sent == 0)
            {
                colorHex = "#8fa3bf";
                return "INGEN MÅLINGER";
            }

            if (target.Replies == 0)
            {
                colorHex = "#F44336";
                return "OFFLINE — INTET SVAR";
            }

            if (target.LossPercent >= 95)
            {
                colorHex = "#F44336";
                return "OFFLINE — SVARER STORT SET IKKE";
            }

            if (target.HasProblem)
            {
                colorHex = "#FF9800";
                return "ONLINE — MEN USTABIL";
            }

            colorHex = "#4CAF50";
            return "ONLINE — STABIL FORBINDELSE";
        }

        // ── IActivatablePage ─────────────────────────────────────────────────

        public void OnActivated()
        {
            _pingTimer.Start();
            _uptimeTimer.Start();
            _ = RefreshUptimeAsync();
            OnPingTick(null, EventArgs.Empty); // ping med det samme, så listen ikke står tom
        }

        public void OnDeactivated()
        {
            _pingTimer.Stop();
            _uptimeTimer.Stop();
        }

        // DispatcherTimer.Tick er en async void-flade; alt fanges her.
        private async void OnPingTick(object? sender, EventArgs e)
        {
            try { await PingAllAsync(); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { CrashLog.Write("PingMonitorViewModel.PingAll", ex); }
        }

        private async void OnUptimeTick(object? sender, EventArgs e)
        {
            try { await RefreshUptimeAsync(); }
            catch (Exception ex) { CrashLog.Write("PingMonitorViewModel.RefreshUptime", ex); }
        }

        private async Task PingAllAsync()
        {
            // Ingen overlap — et langsomt tick må aldrig stable sig op.
            if (System.Threading.Interlocked.CompareExchange(ref _pingInFlight, 1, 0) != 0) return;
            try
            {
                var targets = Targets.ToList();
                if (targets.Count == 0) return;

                var tasks = targets.Select(async target =>
                {
                    double? ms = await PingOnceAsync(target.HostOrIp);
                    var status = ms.HasValue ? PingMonitorStatus.Online : PingMonitorStatus.Offline;
                    var ts = DateTimeOffset.UtcNow;

                    if (Application.Current != null)
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            target.Series.Add(ms);
                            target.Status = status;
                            target.LastChecked = DateTime.Now;
                        });

                    // Fire-and-forget: overvågningen må aldrig blive langsommere fordi
                    // historik-skrivningen er langsom — HistoryService fanger selv sine fejl.
                    _ = _historyService.RecordPingSampleAsync(target.Id, ts, ms);
                });

                await Task.WhenAll(tasks);
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _pingInFlight, 0);
            }
        }

        private async Task RefreshUptimeAsync()
        {
            var to = DateTimeOffset.UtcNow;
            var from = to - TimeSpan.FromDays(1);

            foreach (var target in Targets.ToList())
            {
                try
                {
                    var uptime = await _historyService.GetUptimePercentAsync(target.Id, from, to);
                    target.UptimePercent = uptime;
                    target.IsUptimeLoaded = true;
                }
                catch (Exception ex) { CrashLog.Write("PingMonitorViewModel.RefreshUptime", ex); }
            }
        }

        private static async Task<double?> PingOnceAsync(string hostOrIp)
        {
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(hostOrIp, 1500);
                return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
            }
            catch
            {
                return null;
            }
        }

        // ── Persistens af mål-listen (JSON) ──────────────────────────────────

        private class PersistedTarget
        {
            public string Id { get; set; } = string.Empty;
            public string HostOrIp { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
        }

        private void LoadTargets()
        {
            try
            {
                if (!File.Exists(PersistPath)) return;
                var json = File.ReadAllText(PersistPath);
                var loaded = JsonSerializer.Deserialize<List<PersistedTarget>>(json);
                if (loaded == null) return;

                foreach (var p in loaded)
                {
                    var target = new PingMonitorTarget
                    {
                        Id = string.IsNullOrEmpty(p.Id) ? Guid.NewGuid().ToString("N") : p.Id,
                        HostOrIp = p.HostOrIp,
                        Description = p.Description
                    };
                    Targets.Add(target);
                    _ = _historyService.UpsertPingTargetAsync(target.Id, target.HostOrIp, target.Description);
                }
            }
            catch (Exception ex)
            {
                // Korrupt/manglende fil er ikke fatalt — siden starter blot uden mål.
                CrashLog.Write("PingMonitorViewModel.LoadTargets", ex);
            }
        }

        private void SaveTargets()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PersistPath)!);
                var list = Targets.Select(t => new PersistedTarget
                {
                    Id = t.Id,
                    HostOrIp = t.HostOrIp,
                    Description = t.Description
                }).ToList();
                var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });

                // Atomisk: skriv til temp og byt ind, så en afbrudt skrivning ikke
                // efterlader en tom fil hvor brugerens mål-liste før lå.
                var tmp = PersistPath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, PersistPath, overwrite: true);
            }
            catch (Exception ex)
            {
                CrashLog.Write("PingMonitorViewModel.SaveTargets", ex);
            }
        }

        public void Dispose()
        {
            _pingTimer.Stop();
            _pingTimer.Tick -= OnPingTick;
            _uptimeTimer.Stop();
            _uptimeTimer.Tick -= OnUptimeTick;
        }
    }
}
