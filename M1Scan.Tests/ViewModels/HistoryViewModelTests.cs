using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using M1Scan.Models;
using M1Scan.Services;
using M1Scan.ViewModels;
using Xunit;

namespace M1Scan.Tests.ViewModels
{
    /// <summary>
    /// HistoryViewModel er inert ved konstruktion — det rigtige arbejde sker i
    /// OnActivated() (IActivatablePage), så konstruktøren rører hverken netværk
    /// eller Application.Current. Se CLAUDE.md's navigation/IActivatablePage-afsnit.
    /// </summary>
    public class HistoryViewModelTests
    {
        private sealed class FakeHistoryService : IHistoryService
        {
            public List<HistorySample> SamplesToReturn = new();
            public List<ScanSummary> ScansToReturn = new();
            public List<DeviceEvent> EventsToReturn = new();
            public Exception? ThrowOnGetSamples;

            public Task InitializeAsync() => Task.CompletedTask;
            public Task RecordSampleAsync(DateTimeOffset ts, double? wanAvg, double? wanJitter, double? wanLoss,
                                           double? gwAvg, double? gwJitter, double? gwLoss,
                                           int? healthScore, string? healthGrade) => Task.CompletedTask;
            public Task RecordScanAsync(DateTimeOffset ts, int hostCount, int reachableCount, bool wasComplete) => Task.CompletedTask;
            public Task RecordDeviceEventAsync(DateTimeOffset ts, string mac, string eventType, string? name) => Task.CompletedTask;

            public Task<IReadOnlyList<HistorySample>> GetSamplesAsync(DateTimeOffset from, DateTimeOffset to)
            {
                if (ThrowOnGetSamples != null) throw ThrowOnGetSamples;
                return Task.FromResult<IReadOnlyList<HistorySample>>(SamplesToReturn);
            }
            public Task<IReadOnlyList<ScanSummary>> GetScansAsync(DateTimeOffset from, DateTimeOffset to) =>
                Task.FromResult<IReadOnlyList<ScanSummary>>(ScansToReturn);
            public Task<IReadOnlyList<DeviceEvent>> GetDeviceEventsAsync(DateTimeOffset from, DateTimeOffset to) =>
                Task.FromResult<IReadOnlyList<DeviceEvent>>(EventsToReturn);

            public Task RecordPortEventAsync(DateTimeOffset ts, string mac, int port, bool isOpen) => Task.CompletedTask;
            public Task<IReadOnlyList<PortEvent>> GetPortHistoryAsync(string mac, DateTimeOffset from, DateTimeOffset to) =>
                Task.FromResult<IReadOnlyList<PortEvent>>(Array.Empty<PortEvent>());
            public Task ClearPortHistoryAsync(string mac) => Task.CompletedTask;

            public Task UpsertPingTargetAsync(string id, string hostOrIp, string? description) => Task.CompletedTask;
            public Task RemovePingTargetAsync(string id) => Task.CompletedTask;
            public Task RecordPingSampleAsync(string targetId, DateTimeOffset ts, double? latencyMs) => Task.CompletedTask;
            public Task<double> GetUptimePercentAsync(string targetId, DateTimeOffset from, DateTimeOffset to) =>
                Task.FromResult(0d);

            public Task RecordTraceSampleAsync(string target, int hopNumber, string? ipAddress, DateTimeOffset ts, double? latencyMs) => Task.CompletedTask;
            public Task<IReadOnlyList<TraceSample>> GetTraceSamplesAsync(string target, DateTimeOffset from, DateTimeOffset to) =>
                Task.FromResult<IReadOnlyList<TraceSample>>(Array.Empty<TraceSample>());

            public void StartBackgroundSampling() { }
            public void StopBackgroundSampling() { }
            public void Dispose() { }
        }

        [Fact]
        public void Constructor_DoesNotLoadAnything()
        {
            // Inert konstruktør er selve pointen med IActivatablePage-mønsteret —
            // en side brugeren aldrig åbner må ikke lave arbejde ved opstart.
            var fake = new FakeHistoryService();
            var vm = new HistoryViewModel(fake);

            Assert.Empty(vm.Samples);
            Assert.Empty(vm.Scans);
            Assert.Empty(vm.Events);
            Assert.False(vm.IsLoading);
        }

        [Fact]
        public async Task OnActivated_LoadsSamplesScansAndEvents()
        {
            var fake = new FakeHistoryService
            {
                SamplesToReturn = { new HistorySample { Timestamp = DateTimeOffset.UtcNow, HealthScore = 96, HealthGrade = "A", WanAvgMs = 12.3 } },
                ScansToReturn = { new ScanSummary { Timestamp = DateTimeOffset.UtcNow, HostCount = 20, ReachableCount = 12, WasComplete = true } },
                EventsToReturn = { new DeviceEvent { Timestamp = DateTimeOffset.UtcNow, Mac = "AA-BB-CC-DD-EE-FF", EventType = DeviceEventType.NewDevice, Name = "Sonos" } },
            };
            var vm = new HistoryViewModel(fake);

            await vm.RefreshCommand.ExecuteAsync(null);

            Assert.Single(vm.Samples);
            Assert.Equal(96, vm.Samples[0].Score);
            Assert.Single(vm.Scans);
            Assert.Contains("12 enheder online", vm.Scans[0].Summary);
            Assert.Single(vm.Events);
            Assert.Contains("Sonos", vm.Events[0].Summary);
            Assert.False(vm.IsLoading);
            Assert.Contains("Opdateret", vm.StatusMessage);
        }

        [Fact]
        public async Task OnActivated_IncompleteScan_SummaryReflectsThat()
        {
            var fake = new FakeHistoryService
            {
                ScansToReturn = { new ScanSummary { Timestamp = DateTimeOffset.UtcNow, HostCount = 0, ReachableCount = 3, WasComplete = false } },
            };
            var vm = new HistoryViewModel(fake);

            await vm.RefreshCommand.ExecuteAsync(null);

            Assert.Contains("Ufuldstændig scanning", vm.Scans[0].Summary);
        }

        [Fact]
        public async Task RefreshAsync_ServiceThrows_SetsErrorStatusAndClearsIsLoading()
        {
            var fake = new FakeHistoryService { ThrowOnGetSamples = new InvalidOperationException("db is locked") };
            var vm = new HistoryViewModel(fake);

            await vm.RefreshCommand.ExecuteAsync(null);

            Assert.False(vm.IsLoading);
            Assert.Contains("db is locked", vm.StatusMessage);
        }

        [Fact]
        public void OnActivated_Interface_TriggersRefresh()
        {
            var fake = new FakeHistoryService
            {
                ScansToReturn = { new ScanSummary { Timestamp = DateTimeOffset.UtcNow, HostCount = 1, ReachableCount = 1, WasComplete = true } },
            };
            IActivatablePage vm = new HistoryViewModel(fake);

            vm.OnActivated();

            // Fakens Task'er er alle allerede-fuldførte, så AsyncRelayCommand.Execute
            // (fire-and-forget) løber synkront til ende uden ægte async-gap.
            var history = (HistoryViewModel)vm;
            Assert.Single(history.Scans);
        }
    }
}
