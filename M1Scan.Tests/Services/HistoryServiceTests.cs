using System;
using System.IO;
using System.Threading.Tasks;
using M1Scan.Models;
using M1Scan.Services;
using Xunit;

namespace M1Scan.Tests.Services
{
    /// <summary>
    /// Historik-persistenslaget. Hver test bruger sin egen midlertidige SQLite-fil
    /// (ikke den rigtige %APPDATA%-database), så testene er isolerede fra hinanden
    /// og fra udviklerens rigtige historik.
    /// </summary>
    public class HistoryServiceTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly HistoryService _svc;

        public HistoryServiceTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"m1scan-history-test-{Guid.NewGuid():N}.db");
            _svc = new HistoryService(_dbPath);
        }

        public void Dispose()
        {
            _svc.Dispose();
            try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* best effort cleanup */ }
        }

        [Fact]
        public async Task InitializeAsync_IsIdempotent()
        {
            await _svc.InitializeAsync();
            await _svc.InitializeAsync(); // andet kald må ikke fejle på "table already exists"

            var samples = await _svc.GetSamplesAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue);
            Assert.Empty(samples);
        }

        [Fact]
        public async Task RecordSampleAsync_RoundTripsThroughGetSamplesAsync()
        {
            await _svc.InitializeAsync();
            var ts = DateTimeOffset.UtcNow;

            await _svc.RecordSampleAsync(ts, wanAvg: 12.5, wanJitter: 1.1, wanLoss: 0,
                                          gwAvg: 3.0, gwJitter: 0.5, gwLoss: 0,
                                          healthScore: 96, healthGrade: "A");

            var samples = await _svc.GetSamplesAsync(ts.AddMinutes(-1), ts.AddMinutes(1));

            var sample = Assert.Single(samples);
            Assert.Equal(12.5, sample.WanAvgMs);
            Assert.Equal(1.1, sample.WanJitterMs);
            Assert.Equal(0, sample.WanLossPct);
            Assert.Equal(3.0, sample.GwAvgMs);
            Assert.Equal(96, sample.HealthScore);
            Assert.Equal("A", sample.HealthGrade);
        }

        [Fact]
        public async Task RecordSampleAsync_AllowsNullGatewayFields()
        {
            // Ingen gateway kendt (fx ingen aktiv adapter) — gateway-felterne skal
            // kunne være null uden at INSERT fejler.
            await _svc.InitializeAsync();
            var ts = DateTimeOffset.UtcNow;

            await _svc.RecordSampleAsync(ts, wanAvg: 10, wanJitter: 1, wanLoss: 0,
                                          gwAvg: null, gwJitter: null, gwLoss: null,
                                          healthScore: null, healthGrade: null);

            var sample = Assert.Single(await _svc.GetSamplesAsync(ts.AddMinutes(-1), ts.AddMinutes(1)));
            Assert.Null(sample.GwAvgMs);
            Assert.Null(sample.HealthScore);
        }

        [Fact]
        public async Task RecordScanAsync_RoundTripsThroughGetScansAsync()
        {
            await _svc.InitializeAsync();
            var ts = DateTimeOffset.UtcNow;

            await _svc.RecordScanAsync(ts, hostCount: 20, reachableCount: 12, wasComplete: true);

            var scan = Assert.Single(await _svc.GetScansAsync(ts.AddMinutes(-1), ts.AddMinutes(1)));
            Assert.Equal(20, scan.HostCount);
            Assert.Equal(12, scan.ReachableCount);
            Assert.True(scan.WasComplete);
        }

        [Fact]
        public async Task RecordDeviceEventAsync_RoundTripsThroughGetDeviceEventsAsync()
        {
            await _svc.InitializeAsync();
            var ts = DateTimeOffset.UtcNow;

            await _svc.RecordDeviceEventAsync(ts, "AA-BB-CC-DD-EE-FF", DeviceEventType.NewDevice, "Sonos");

            var ev = Assert.Single(await _svc.GetDeviceEventsAsync(ts.AddMinutes(-1), ts.AddMinutes(1)));
            Assert.Equal("AA-BB-CC-DD-EE-FF", ev.Mac);
            Assert.Equal(DeviceEventType.NewDevice, ev.EventType);
            Assert.Equal("Sonos", ev.Name);
        }

        [Fact]
        public async Task InitializeAsync_RemovesSamplesOlderThanRetentionWindow()
        {
            await _svc.InitializeAsync();

            var old   = DateTimeOffset.UtcNow.AddDays(-31);
            var fresh = DateTimeOffset.UtcNow.AddDays(-1);

            await _svc.RecordSampleAsync(old, 10, 1, 0, null, null, null, null, null);
            await _svc.RecordSampleAsync(fresh, 10, 1, 0, null, null, null, null, null);

            // Genkør InitializeAsync for at trigge retention-oprydningen, ligesom
            // ved appens næste opstart.
            await _svc.InitializeAsync();

            var remaining = await _svc.GetSamplesAsync(DateTimeOffset.UtcNow.AddDays(-60), DateTimeOffset.UtcNow);
            var single = Assert.Single(remaining);
            Assert.True(single.Timestamp > old);
        }

        [Fact]
        public async Task ConcurrentWrites_AreSerializedWithoutError()
        {
            // SQLite tillader kun én writer ad gangen — HistoryService's interne
            // SemaphoreSlim skal forhindre "database is locked"-fejl ved samtidige
            // skrivninger, som kan ske hvis baggrundssampleren og en scan-completion
            // rammer samtidig.
            await _svc.InitializeAsync();
            var ts = DateTimeOffset.UtcNow;

            var tasks = new Task[20];
            for (int i = 0; i < tasks.Length; i++)
            {
                var offset = i;
                tasks[i] = _svc.RecordSampleAsync(ts.AddSeconds(offset), offset, 1, 0, null, null, null, null, null);
            }
            await Task.WhenAll(tasks);

            var samples = await _svc.GetSamplesAsync(ts.AddSeconds(-1), ts.AddSeconds(30));
            Assert.Equal(20, samples.Count);
        }

        // ── Ping monitor ─────────────────────────────────────────────────────

        [Fact]
        public async Task UpsertPingTargetAsync_InsertsThenUpdatesOnConflict()
        {
            await _svc.InitializeAsync();
            var id = Guid.NewGuid().ToString("N");

            await _svc.UpsertPingTargetAsync(id, "1.1.1.1", "Cloudflare");
            await _svc.UpsertPingTargetAsync(id, "1.0.0.1", "Cloudflare (alt)");

            // Ingen direkte "get target"-metode i IHistoryService — verificeres
            // indirekte via at et ping-sample for samme id kan gemmes uden fejl
            // (ville fejle på en FK-lignende antagelse hvis upsert ikke virkede).
            await _svc.RecordPingSampleAsync(id, DateTimeOffset.UtcNow, 12.3);
            var uptime = await _svc.GetUptimePercentAsync(id, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
            Assert.Equal(100, uptime);
        }

        [Fact]
        public async Task RemovePingTargetAsync_DoesNotDeleteExistingSamples()
        {
            // Målets historiske samples er data om en periode der faktisk blev målt
            // og skal bevares, selvom brugeren stopper med at overvåge målet i dag.
            await _svc.InitializeAsync();
            var id = Guid.NewGuid().ToString("N");
            var ts = DateTimeOffset.UtcNow;

            await _svc.UpsertPingTargetAsync(id, "1.1.1.1", null);
            await _svc.RecordPingSampleAsync(id, ts, 10);
            await _svc.RemovePingTargetAsync(id);

            var uptime = await _svc.GetUptimePercentAsync(id, ts.AddMinutes(-1), ts.AddMinutes(1));
            Assert.Equal(100, uptime);
        }

        [Fact]
        public async Task GetUptimePercentAsync_ComputesPercentFromMixedSamples()
        {
            await _svc.InitializeAsync();
            var id = Guid.NewGuid().ToString("N");
            var ts = DateTimeOffset.UtcNow;

            await _svc.UpsertPingTargetAsync(id, "10.0.0.1", null);
            await _svc.RecordPingSampleAsync(id, ts, 5);
            await _svc.RecordPingSampleAsync(id, ts.AddSeconds(1), 6);
            await _svc.RecordPingSampleAsync(id, ts.AddSeconds(2), null);  // tabt
            await _svc.RecordPingSampleAsync(id, ts.AddSeconds(3), null);  // tabt

            var uptime = await _svc.GetUptimePercentAsync(id, ts.AddMinutes(-1), ts.AddMinutes(1));
            Assert.Equal(50, uptime);
        }

        [Fact]
        public async Task GetUptimePercentAsync_NoSamplesReturnsZero()
        {
            await _svc.InitializeAsync();
            var id = Guid.NewGuid().ToString("N");

            var uptime = await _svc.GetUptimePercentAsync(id, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow);
            Assert.Equal(0, uptime);
        }

        [Fact]
        public async Task InitializeAsync_RemovesPingSamplesOlderThanRetentionWindow()
        {
            await _svc.InitializeAsync();
            var id = Guid.NewGuid().ToString("N");
            await _svc.UpsertPingTargetAsync(id, "1.1.1.1", null);

            var old   = DateTimeOffset.UtcNow.AddDays(-31);
            var fresh = DateTimeOffset.UtcNow.AddDays(-1);
            await _svc.RecordPingSampleAsync(id, old, null);   // offline, så et overlevende "old"-sample ville sænke uptime
            await _svc.RecordPingSampleAsync(id, fresh, 10);   // online

            await _svc.InitializeAsync(); // trigger retention-oprydning

            // Hvis "old" (offline) stadig var i databasen, ville uptime være 50%.
            // Er den korrekt ryddet op, er kun "fresh" (online) tilbage: 100%.
            var uptime = await _svc.GetUptimePercentAsync(id, DateTimeOffset.UtcNow.AddDays(-60), DateTimeOffset.UtcNow);
            Assert.Equal(100, uptime);
        }

        // ── Traceroute path-monitor ──────────────────────────────────────────

        [Fact]
        public async Task RecordTraceSampleAsync_RoundTripsThroughGetTraceSamplesAsync()
        {
            await _svc.InitializeAsync();
            var ts = DateTimeOffset.UtcNow;

            await _svc.RecordTraceSampleAsync("8.8.8.8", hopNumber: 3, "192.168.1.1", ts, 5.5);

            var sample = Assert.Single(await _svc.GetTraceSamplesAsync("8.8.8.8", ts.AddMinutes(-1), ts.AddMinutes(1)));
            Assert.Equal(3, sample.HopNumber);
            Assert.Equal("192.168.1.1", sample.IpAddress);
            Assert.Equal(5.5, sample.LatencyMs);
        }

        [Fact]
        public async Task RecordTraceSampleAsync_AllowsNullLatencyForTimeout()
        {
            await _svc.InitializeAsync();
            var ts = DateTimeOffset.UtcNow;

            await _svc.RecordTraceSampleAsync("8.8.8.8", hopNumber: 5, null, ts, null);

            var sample = Assert.Single(await _svc.GetTraceSamplesAsync("8.8.8.8", ts.AddMinutes(-1), ts.AddMinutes(1)));
            Assert.Null(sample.IpAddress);
            Assert.Null(sample.LatencyMs);
        }

        [Fact]
        public async Task GetTraceSamplesAsync_KeepsDifferentTargetsSeparate()
        {
            await _svc.InitializeAsync();
            var ts = DateTimeOffset.UtcNow;

            await _svc.RecordTraceSampleAsync("8.8.8.8", 1, "10.0.0.1", ts, 5);
            await _svc.RecordTraceSampleAsync("1.1.1.1", 1, "10.0.0.1", ts, 7);

            var samplesForGoogle = await _svc.GetTraceSamplesAsync("8.8.8.8", ts.AddMinutes(-1), ts.AddMinutes(1));
            var single = Assert.Single(samplesForGoogle);
            Assert.Equal(5, single.LatencyMs);
        }

        [Fact]
        public async Task InitializeAsync_RemovesTraceSamplesOlderThanRetentionWindow()
        {
            await _svc.InitializeAsync();

            var old   = DateTimeOffset.UtcNow.AddDays(-31);
            var fresh = DateTimeOffset.UtcNow.AddDays(-1);
            await _svc.RecordTraceSampleAsync("8.8.8.8", 1, "10.0.0.1", old, 5);
            await _svc.RecordTraceSampleAsync("8.8.8.8", 1, "10.0.0.1", fresh, 7);

            await _svc.InitializeAsync(); // trigger retention-oprydning

            var remaining = await _svc.GetTraceSamplesAsync("8.8.8.8", DateTimeOffset.UtcNow.AddDays(-60), DateTimeOffset.UtcNow);
            var single = Assert.Single(remaining);
            Assert.True(single.Timestamp > old);
        }
    }
}
