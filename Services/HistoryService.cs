using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Microsoft.Data.Sqlite;
using M1Scan.Models;
using M1Scan.Utils;
using Timer = System.Timers.Timer;

namespace M1Scan.Services
{
    /// <summary>
    /// Historik-persistens: latency/jitter/loss/score-samples, scan-sammendrag og
    /// enheds-events i en SQLite-database (%APPDATA%\M1Scan\history.db).
    ///
    /// Samplingen kører i sin EGEN baggrundstimer, uafhængig af HomeViewModel's
    /// UI-sampler (som kun kører mens Dashboard er synligt, jf. IActivatablePage).
    /// Formålet er at fange udfald der sker mens brugeren er på en anden side —
    /// "have svaret klar før du spørger".
    ///
    /// Skriveadgang serialiseres med en SemaphoreSlim, samme mønster som
    /// NetworkService._sweepGate: SQLite tillader kun én writer ad gangen.
    /// </summary>
    public interface IHistoryService : IDisposable
    {
        Task InitializeAsync();
        Task RecordSampleAsync(DateTimeOffset ts, double? wanAvg, double? wanJitter, double? wanLoss,
                                double? gwAvg, double? gwJitter, double? gwLoss,
                                int? healthScore, string? healthGrade);
        Task RecordScanAsync(DateTimeOffset ts, int hostCount, int reachableCount, bool wasComplete);
        Task RecordDeviceEventAsync(DateTimeOffset ts, string mac, string eventType, string? name);
        Task<IReadOnlyList<HistorySample>> GetSamplesAsync(DateTimeOffset from, DateTimeOffset to);
        Task<IReadOnlyList<ScanSummary>> GetScansAsync(DateTimeOffset from, DateTimeOffset to);
        Task<IReadOnlyList<DeviceEvent>> GetDeviceEventsAsync(DateTimeOffset from, DateTimeOffset to);

        // ── Ping monitor ─────────────────────────────────────────────────────
        Task UpsertPingTargetAsync(string id, string hostOrIp, string? description);
        Task RemovePingTargetAsync(string id);
        Task RecordPingSampleAsync(string targetId, DateTimeOffset ts, double? latencyMs);
        Task<double> GetUptimePercentAsync(string targetId, DateTimeOffset from, DateTimeOffset to);

        // ── Traceroute path-monitor ──────────────────────────────────────────
        Task RecordTraceSampleAsync(string target, int hopNumber, string? ipAddress, DateTimeOffset ts, double? latencyMs);
        Task<IReadOnlyList<TraceSample>> GetTraceSamplesAsync(string target, DateTimeOffset from, DateTimeOffset to);

        void StartBackgroundSampling();
        void StopBackgroundSampling();
    }

    public class HistoryService : IHistoryService, IDisposable
    {
        private const string WanProbeHost = "1.1.1.1";
        private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan Retention = TimeSpan.FromDays(30);

        private readonly string _dbPath;
        private readonly string _connectionString;
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly INetworkService? _networkService;

        private Timer? _sampleTimer;
        private int _sampleInFlight;
        private volatile bool _disposed;

        // Rullende jitter/loss-vindue til baggrundssampleren — separat fra
        // HomeViewModel.LatencySeries, som er UI-bundet og kun lever mens
        // Dashboard er aktivt. Lille fast vindue (12 samples ~ 2 min ved 10s
        // interval) er nok til at udlede jitter/loss for hvert gemte punkt.
        private readonly Queue<double?> _wanWindow = new();
        private readonly Queue<double?> _gwWindow = new();
        private const int WindowCapacity = 12;

        public HistoryService(INetworkService? networkService = null)
            : this(DefaultDbPath(), networkService)
        {
        }

        /// <summary>Intern konstruktør til tests, som skal pege på en midlertidig
        /// databasefil i stedet for den rigtige %APPDATA%-sti.</summary>
        internal HistoryService(string dbPath, INetworkService? networkService = null)
        {
            var dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir))
            {
                // I/O-fejl her må ikke forhindre appen i at starte — historik er en
                // bekvemmelighed, ikke en forudsætning.
                try { Directory.CreateDirectory(dir); }
                catch (Exception ex) { CrashLog.Write("HistoryService.ctor", ex); }
            }

            _dbPath = dbPath;
            _connectionString = new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString();
            _networkService = networkService;
        }

        private static string DefaultDbPath() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "M1Scan", "history.db");

        public async Task InitializeAsync()
        {
            try
            {
                await _writeGate.WaitAsync();
                try
                {
                    using var conn = new SqliteConnection(_connectionString);
                    await conn.OpenAsync();

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = @"
                            CREATE TABLE IF NOT EXISTS samples (
                                id INTEGER PRIMARY KEY AUTOINCREMENT,
                                timestamp TEXT NOT NULL,
                                wan_avg_ms REAL, wan_jitter_ms REAL, wan_loss_pct REAL,
                                gw_avg_ms REAL, gw_jitter_ms REAL, gw_loss_pct REAL,
                                health_score INTEGER, health_grade TEXT
                            );
                            CREATE INDEX IF NOT EXISTS idx_samples_ts ON samples(timestamp);

                            CREATE TABLE IF NOT EXISTS scans (
                                id INTEGER PRIMARY KEY AUTOINCREMENT,
                                timestamp TEXT NOT NULL,
                                host_count INTEGER, reachable_count INTEGER, was_complete INTEGER
                            );
                            CREATE INDEX IF NOT EXISTS idx_scans_ts ON scans(timestamp);

                            CREATE TABLE IF NOT EXISTS device_events (
                                id INTEGER PRIMARY KEY AUTOINCREMENT,
                                timestamp TEXT NOT NULL,
                                mac TEXT NOT NULL, event_type TEXT NOT NULL, name TEXT
                            );
                            CREATE INDEX IF NOT EXISTS idx_events_ts ON device_events(timestamp);

                            CREATE TABLE IF NOT EXISTS ping_targets (
                                id TEXT PRIMARY KEY,
                                host_or_ip TEXT NOT NULL,
                                description TEXT
                            );

                            CREATE TABLE IF NOT EXISTS ping_samples (
                                id INTEGER PRIMARY KEY AUTOINCREMENT,
                                target_id TEXT NOT NULL,
                                timestamp TEXT NOT NULL,
                                latency_ms REAL
                            );
                            CREATE INDEX IF NOT EXISTS idx_ping_samples_target_ts ON ping_samples(target_id, timestamp);

                            CREATE TABLE IF NOT EXISTS trace_samples (
                                id INTEGER PRIMARY KEY AUTOINCREMENT,
                                target TEXT NOT NULL,
                                hop_number INTEGER NOT NULL,
                                ip_address TEXT,
                                timestamp TEXT NOT NULL,
                                latency_ms REAL
                            );
                            CREATE INDEX IF NOT EXISTS idx_trace_samples_target_ts ON trace_samples(target, timestamp);";
                        await cmd.ExecuteNonQueryAsync();
                    }

                    var cutoff = DateTimeOffset.UtcNow - Retention;
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "DELETE FROM samples WHERE timestamp < @cutoff;" +
                                           "DELETE FROM scans WHERE timestamp < @cutoff;" +
                                           "DELETE FROM device_events WHERE timestamp < @cutoff;" +
                                           "DELETE FROM ping_samples WHERE timestamp < @cutoff;" +
                                           "DELETE FROM trace_samples WHERE timestamp < @cutoff;";
                        cmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("O"));
                        await cmd.ExecuteNonQueryAsync();
                    }
                }
                finally { _writeGate.Release(); }
            }
            catch (Exception ex)
            {
                // Historik-databasen er ikke en forudsætning for appens kernefunktion —
                // en fejl her logges, men må aldrig vælte opstarten.
                CrashLog.Write("HistoryService.InitializeAsync", ex);
            }
        }

        public async Task RecordSampleAsync(DateTimeOffset ts, double? wanAvg, double? wanJitter, double? wanLoss,
                                             double? gwAvg, double? gwJitter, double? gwLoss,
                                             int? healthScore, string? healthGrade)
        {
            try
            {
                await _writeGate.WaitAsync();
                try
                {
                    using var conn = new SqliteConnection(_connectionString);
                    await conn.OpenAsync();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        INSERT INTO samples
                            (timestamp, wan_avg_ms, wan_jitter_ms, wan_loss_pct,
                             gw_avg_ms, gw_jitter_ms, gw_loss_pct, health_score, health_grade)
                        VALUES
                            (@ts, @wanAvg, @wanJitter, @wanLoss, @gwAvg, @gwJitter, @gwLoss, @score, @grade);";
                    cmd.Parameters.AddWithValue("@ts", ts.ToString("O"));
                    cmd.Parameters.AddWithValue("@wanAvg", (object?)wanAvg ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@wanJitter", (object?)wanJitter ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@wanLoss", (object?)wanLoss ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@gwAvg", (object?)gwAvg ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@gwJitter", (object?)gwJitter ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@gwLoss", (object?)gwLoss ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@score", (object?)healthScore ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@grade", (object?)healthGrade ?? DBNull.Value);
                    await cmd.ExecuteNonQueryAsync();
                }
                finally { _writeGate.Release(); }
            }
            catch (Exception ex) { CrashLog.Write("HistoryService.RecordSampleAsync", ex); }
        }

        public async Task RecordScanAsync(DateTimeOffset ts, int hostCount, int reachableCount, bool wasComplete)
        {
            try
            {
                await _writeGate.WaitAsync();
                try
                {
                    using var conn = new SqliteConnection(_connectionString);
                    await conn.OpenAsync();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        INSERT INTO scans (timestamp, host_count, reachable_count, was_complete)
                        VALUES (@ts, @hostCount, @reachable, @complete);";
                    cmd.Parameters.AddWithValue("@ts", ts.ToString("O"));
                    cmd.Parameters.AddWithValue("@hostCount", hostCount);
                    cmd.Parameters.AddWithValue("@reachable", reachableCount);
                    cmd.Parameters.AddWithValue("@complete", wasComplete ? 1 : 0);
                    await cmd.ExecuteNonQueryAsync();
                }
                finally { _writeGate.Release(); }
            }
            catch (Exception ex) { CrashLog.Write("HistoryService.RecordScanAsync", ex); }
        }

        public async Task RecordDeviceEventAsync(DateTimeOffset ts, string mac, string eventType, string? name)
        {
            try
            {
                await _writeGate.WaitAsync();
                try
                {
                    using var conn = new SqliteConnection(_connectionString);
                    await conn.OpenAsync();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        INSERT INTO device_events (timestamp, mac, event_type, name)
                        VALUES (@ts, @mac, @type, @name);";
                    cmd.Parameters.AddWithValue("@ts", ts.ToString("O"));
                    cmd.Parameters.AddWithValue("@mac", mac);
                    cmd.Parameters.AddWithValue("@type", eventType);
                    cmd.Parameters.AddWithValue("@name", (object?)name ?? DBNull.Value);
                    await cmd.ExecuteNonQueryAsync();
                }
                finally { _writeGate.Release(); }
            }
            catch (Exception ex) { CrashLog.Write("HistoryService.RecordDeviceEventAsync", ex); }
        }

        public async Task<IReadOnlyList<HistorySample>> GetSamplesAsync(DateTimeOffset from, DateTimeOffset to)
        {
            var results = new List<HistorySample>();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT timestamp, wan_avg_ms, wan_jitter_ms, wan_loss_pct,
                           gw_avg_ms, gw_jitter_ms, gw_loss_pct, health_score, health_grade
                    FROM samples WHERE timestamp >= @from AND timestamp <= @to
                    ORDER BY timestamp ASC;";
                cmd.Parameters.AddWithValue("@from", from.ToString("O"));
                cmd.Parameters.AddWithValue("@to", to.ToString("O"));

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    results.Add(new HistorySample
                    {
                        Timestamp   = DateTimeOffset.Parse(reader.GetString(0)),
                        WanAvgMs    = reader.IsDBNull(1) ? null : reader.GetDouble(1),
                        WanJitterMs = reader.IsDBNull(2) ? null : reader.GetDouble(2),
                        WanLossPct  = reader.IsDBNull(3) ? null : reader.GetDouble(3),
                        GwAvgMs     = reader.IsDBNull(4) ? null : reader.GetDouble(4),
                        GwJitterMs  = reader.IsDBNull(5) ? null : reader.GetDouble(5),
                        GwLossPct   = reader.IsDBNull(6) ? null : reader.GetDouble(6),
                        HealthScore = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                        HealthGrade = reader.IsDBNull(8) ? null : reader.GetString(8),
                    });
                }
            }
            catch (Exception ex) { CrashLog.Write("HistoryService.GetSamplesAsync", ex); }
            return results;
        }

        public async Task<IReadOnlyList<ScanSummary>> GetScansAsync(DateTimeOffset from, DateTimeOffset to)
        {
            var results = new List<ScanSummary>();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT timestamp, host_count, reachable_count, was_complete
                    FROM scans WHERE timestamp >= @from AND timestamp <= @to
                    ORDER BY timestamp DESC;";
                cmd.Parameters.AddWithValue("@from", from.ToString("O"));
                cmd.Parameters.AddWithValue("@to", to.ToString("O"));

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    results.Add(new ScanSummary
                    {
                        Timestamp      = DateTimeOffset.Parse(reader.GetString(0)),
                        HostCount      = reader.GetInt32(1),
                        ReachableCount = reader.GetInt32(2),
                        WasComplete    = reader.GetInt32(3) != 0,
                    });
                }
            }
            catch (Exception ex) { CrashLog.Write("HistoryService.GetScansAsync", ex); }
            return results;
        }

        public async Task<IReadOnlyList<DeviceEvent>> GetDeviceEventsAsync(DateTimeOffset from, DateTimeOffset to)
        {
            var results = new List<DeviceEvent>();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT timestamp, mac, event_type, name
                    FROM device_events WHERE timestamp >= @from AND timestamp <= @to
                    ORDER BY timestamp DESC;";
                cmd.Parameters.AddWithValue("@from", from.ToString("O"));
                cmd.Parameters.AddWithValue("@to", to.ToString("O"));

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    results.Add(new DeviceEvent
                    {
                        Timestamp = DateTimeOffset.Parse(reader.GetString(0)),
                        Mac       = reader.GetString(1),
                        EventType = reader.GetString(2),
                        Name      = reader.IsDBNull(3) ? null : reader.GetString(3),
                    });
                }
            }
            catch (Exception ex) { CrashLog.Write("HistoryService.GetDeviceEventsAsync", ex); }
            return results;
        }

        // ── Ping monitor ─────────────────────────────────────────────────────

        public async Task UpsertPingTargetAsync(string id, string hostOrIp, string? description)
        {
            try
            {
                await _writeGate.WaitAsync();
                try
                {
                    using var conn = new SqliteConnection(_connectionString);
                    await conn.OpenAsync();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        INSERT INTO ping_targets (id, host_or_ip, description)
                        VALUES (@id, @host, @desc)
                        ON CONFLICT(id) DO UPDATE SET host_or_ip = @host, description = @desc;";
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.Parameters.AddWithValue("@host", hostOrIp);
                    cmd.Parameters.AddWithValue("@desc", (object?)description ?? DBNull.Value);
                    await cmd.ExecuteNonQueryAsync();
                }
                finally { _writeGate.Release(); }
            }
            catch (Exception ex) { CrashLog.Write("HistoryService.UpsertPingTargetAsync", ex); }
        }

        public async Task RemovePingTargetAsync(string id)
        {
            try
            {
                await _writeGate.WaitAsync();
                try
                {
                    using var conn = new SqliteConnection(_connectionString);
                    await conn.OpenAsync();
                    using var cmd = conn.CreateCommand();
                    // Målets egne samples bevares med vilje — de er historisk data om
                    // en periode der rent faktisk blev målt, og skal ikke forsvinde
                    // bare fordi brugeren stopper med at overvåge målet i dag.
                    cmd.CommandText = "DELETE FROM ping_targets WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", id);
                    await cmd.ExecuteNonQueryAsync();
                }
                finally { _writeGate.Release(); }
            }
            catch (Exception ex) { CrashLog.Write("HistoryService.RemovePingTargetAsync", ex); }
        }

        public async Task RecordPingSampleAsync(string targetId, DateTimeOffset ts, double? latencyMs)
        {
            try
            {
                await _writeGate.WaitAsync();
                try
                {
                    using var conn = new SqliteConnection(_connectionString);
                    await conn.OpenAsync();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        INSERT INTO ping_samples (target_id, timestamp, latency_ms)
                        VALUES (@targetId, @ts, @latency);";
                    cmd.Parameters.AddWithValue("@targetId", targetId);
                    cmd.Parameters.AddWithValue("@ts", ts.ToString("O"));
                    cmd.Parameters.AddWithValue("@latency", (object?)latencyMs ?? DBNull.Value);
                    await cmd.ExecuteNonQueryAsync();
                }
                finally { _writeGate.Release(); }
            }
            catch (Exception ex) { CrashLog.Write("HistoryService.RecordPingSampleAsync", ex); }
        }

        public async Task<double> GetUptimePercentAsync(string targetId, DateTimeOffset from, DateTimeOffset to)
        {
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT COUNT(*), SUM(CASE WHEN latency_ms IS NOT NULL THEN 1 ELSE 0 END)
                    FROM ping_samples
                    WHERE target_id = @targetId AND timestamp >= @from AND timestamp <= @to;";
                cmd.Parameters.AddWithValue("@targetId", targetId);
                cmd.Parameters.AddWithValue("@from", from.ToString("O"));
                cmd.Parameters.AddWithValue("@to", to.ToString("O"));

                using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync() && !reader.IsDBNull(0))
                {
                    long total = reader.GetInt64(0);
                    if (total == 0) return 0;
                    long online = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                    return 100.0 * online / total;
                }
                return 0;
            }
            catch (Exception ex)
            {
                CrashLog.Write("HistoryService.GetUptimePercentAsync", ex);
                return 0;
            }
        }

        // ── Traceroute path-monitor ──────────────────────────────────────────

        public async Task RecordTraceSampleAsync(string target, int hopNumber, string? ipAddress, DateTimeOffset ts, double? latencyMs)
        {
            try
            {
                await _writeGate.WaitAsync();
                try
                {
                    using var conn = new SqliteConnection(_connectionString);
                    await conn.OpenAsync();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        INSERT INTO trace_samples (target, hop_number, ip_address, timestamp, latency_ms)
                        VALUES (@target, @hop, @ip, @ts, @latency);";
                    cmd.Parameters.AddWithValue("@target", target);
                    cmd.Parameters.AddWithValue("@hop", hopNumber);
                    cmd.Parameters.AddWithValue("@ip", (object?)ipAddress ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@ts", ts.ToString("O"));
                    cmd.Parameters.AddWithValue("@latency", (object?)latencyMs ?? DBNull.Value);
                    await cmd.ExecuteNonQueryAsync();
                }
                finally { _writeGate.Release(); }
            }
            catch (Exception ex) { CrashLog.Write("HistoryService.RecordTraceSampleAsync", ex); }
        }

        public async Task<IReadOnlyList<TraceSample>> GetTraceSamplesAsync(string target, DateTimeOffset from, DateTimeOffset to)
        {
            var results = new List<TraceSample>();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT timestamp, hop_number, ip_address, latency_ms
                    FROM trace_samples
                    WHERE target = @target AND timestamp >= @from AND timestamp <= @to
                    ORDER BY timestamp DESC;";
                cmd.Parameters.AddWithValue("@target", target);
                cmd.Parameters.AddWithValue("@from", from.ToString("O"));
                cmd.Parameters.AddWithValue("@to", to.ToString("O"));

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    results.Add(new TraceSample
                    {
                        Timestamp = DateTimeOffset.Parse(reader.GetString(0)),
                        HopNumber = reader.GetInt32(1),
                        IpAddress = reader.IsDBNull(2) ? null : reader.GetString(2),
                        LatencyMs = reader.IsDBNull(3) ? null : reader.GetDouble(3),
                    });
                }
            }
            catch (Exception ex) { CrashLog.Write("HistoryService.GetTraceSamplesAsync", ex); }
            return results;
        }

        // ── Baggrundssampling ────────────────────────────────────────────────

        public void StartBackgroundSampling()
        {
            if (_sampleTimer != null) return; // allerede startet

            _sampleTimer = new Timer(SampleInterval.TotalMilliseconds) { AutoReset = true };
            _sampleTimer.Elapsed += OnSampleTick;
            _sampleTimer.Start();
        }

        public void StopBackgroundSampling()
        {
            if (_sampleTimer == null) return;
            _sampleTimer.Stop();
            _sampleTimer.Elapsed -= OnSampleTick;
            _sampleTimer.Dispose();
            _sampleTimer = null;
        }

        private void OnSampleTick(object? sender, ElapsedEventArgs e)
        {
            // System.Timers.Timer kalder handleren på en pool-tråd; en fire-and-forget
            // Task her er sikker fordi SampleOnceAsync selv fanger alle undtagelser.
            _ = SampleOnceAsync();
        }

        private async Task SampleOnceAsync()
        {
            if (_disposed) return;
            // Ingen overlap — et langsomt tick må aldrig stable sig op, samme
            // mønster som HomeViewModel.SampleOnceAsync.
            if (Interlocked.CompareExchange(ref _sampleInFlight, 1, 0) != 0) return;
            try
            {
                var gwIp = await GetGatewayIpAsync();

                var wanTask = PingOnceAsync(WanProbeHost);
                var gwTask  = gwIp != null ? PingOnceAsync(gwIp) : Task.FromResult<double?>(null);
                double? wan = await wanTask;
                double? gw  = await gwTask;

                var (wanAvg, wanJitter, wanLoss) = UpdateWindow(_wanWindow, wan);
                var (gwAvg, gwJitter, gwLoss) = gwIp != null
                    ? UpdateWindow(_gwWindow, gw)
                    : (null, null, null);

                var health = HealthScore.Compute(
                    BuildSeries(_wanWindow),
                    gwIp != null ? BuildSeries(_gwWindow) : null,
                    fastestDnsMs: null, dnsAttempted: false);

                await RecordSampleAsync(DateTimeOffset.UtcNow,
                    wanAvg, wanJitter, wanLoss, gwAvg, gwJitter, gwLoss,
                    health.IsValid ? health.Score : null,
                    health.IsValid ? health.Grade : null);
            }
            catch (Exception ex)
            {
                // Enkeltstående sample-fejl må aldrig stoppe fremtidige samples.
                CrashLog.Write("HistoryService.SampleOnceAsync", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _sampleInFlight, 0);
            }
        }

        /// <summary>Bygger en midlertidig LatencySeries af vinduets samples, kun for
        /// at genbruge HealthScore.Compute's eksisterende beregningslogik uden at
        /// duplikere den.</summary>
        private static LatencySeries BuildSeries(Queue<double?> window)
        {
            var series = new LatencySeries { Label = "" };
            foreach (var sample in window)
                series.Add(sample);
            return series;
        }

        private static (double? avg, double? jitter, double? loss) UpdateWindow(Queue<double?> window, double? latencyMs)
        {
            if (window.Count >= WindowCapacity)
                window.Dequeue();
            window.Enqueue(latencyMs);

            var valid = window.Where(s => s.HasValue).Select(s => s!.Value).ToList();
            int lossCount = window.Count(s => !s.HasValue);

            double? avg = valid.Count > 0 ? valid.Average() : null;
            double? jitter = valid.Count > 1 ? valid.Average(v => Math.Abs(v - avg!.Value)) : (double?)0;
            double? loss = window.Count > 0 ? 100.0 * lossCount / window.Count : null;

            return (avg, jitter, loss);
        }

        private async Task<string?> GetGatewayIpAsync()
        {
            if (_networkService == null) return null;
            try
            {
                var adapters = await _networkService.GetNetworkAdaptersAsync();
                var best = adapters
                    .Where(a => a.IsConnected && !string.IsNullOrEmpty(a.Gateway) && !a.Gateway!.Contains(':'))
                    .FirstOrDefault();
                return best?.Gateway;
            }
            catch { return null; } // gateway-opslag er best-effort for baggrundssampleren
        }

        private static async Task<double?> PingOnceAsync(string host)
        {
            try
            {
                using var ping  = new Ping();
                var       reply = await ping.SendPingAsync(host, 1500);
                return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
            }
            catch
            {
                return null;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopBackgroundSampling();
            _writeGate.Dispose();
        }
    }
}
