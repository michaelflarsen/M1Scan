using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using M1Scan.Models;
using M1Scan.Services;
using M1Scan.ViewModels;
using Xunit;

namespace M1Scan.Tests.ViewModels
{
    /// <summary>
    /// PingMonitorViewModel persisterer sin mål-liste i den rigtige
    /// %APPDATA%\M1Scan\ping_monitor_targets.json (samme mønster som
    /// WorkspaceViewModelTests bruger for workspace.json) — testene bruger derfor
    /// TEST-NET-3-adresser (RFC 5737) og rydder altid op i et try/finally, så
    /// brugerens rigtige mål-liste ikke forurenes af en fejlet testkørsel.
    ///
    /// Konstruktøren starter INGEN timere/pinging (kun OnActivated() gør det, se
    /// IActivatablePage), så disse tests rammer aldrig et rigtigt netværk.
    /// </summary>
    public class PingMonitorViewModelTests
    {
        private sealed class FakeHistoryService : IHistoryService
        {
            public Task InitializeAsync() => Task.CompletedTask;
            public Task RecordSampleAsync(DateTimeOffset ts, double? wanAvg, double? wanJitter, double? wanLoss,
                                           double? gwAvg, double? gwJitter, double? gwLoss,
                                           int? healthScore, string? healthGrade) => Task.CompletedTask;
            public Task RecordScanAsync(DateTimeOffset ts, int hostCount, int reachableCount, bool wasComplete) => Task.CompletedTask;
            public Task RecordDeviceEventAsync(DateTimeOffset ts, string mac, string eventType, string? name) => Task.CompletedTask;
            public Task<IReadOnlyList<HistorySample>> GetSamplesAsync(DateTimeOffset from, DateTimeOffset to) =>
                Task.FromResult<IReadOnlyList<HistorySample>>(Array.Empty<HistorySample>());
            public Task<IReadOnlyList<ScanSummary>> GetScansAsync(DateTimeOffset from, DateTimeOffset to) =>
                Task.FromResult<IReadOnlyList<ScanSummary>>(Array.Empty<ScanSummary>());
            public Task<IReadOnlyList<DeviceEvent>> GetDeviceEventsAsync(DateTimeOffset from, DateTimeOffset to) =>
                Task.FromResult<IReadOnlyList<DeviceEvent>>(Array.Empty<DeviceEvent>());

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

        private static PingMonitorViewModel CreateSut() => new(new FakeHistoryService());

        // Fjerner enhver TEST-NET-3-rest fra en tidligere fejlet kørsel, så testene
        // altid starter fra et kendt punkt uden at røre brugerens rigtige mål.
        private static void RemoveAll(PingMonitorViewModel vm, string hostOrIp)
        {
            foreach (var t in vm.Targets.Where(t => t.HostOrIp.Equals(hostOrIp, StringComparison.OrdinalIgnoreCase)).ToList())
                vm.RemoveTargetCommand.Execute(t);
        }

        [Fact]
        public void AddTargetCommand_RejectsEmptyHost()
        {
            var vm = CreateSut();
            var before = vm.Targets.Count;
            vm.NewHostInput = "   ";

            vm.AddTargetCommand.Execute(null);

            Assert.Equal(before, vm.Targets.Count);
            vm.Dispose();
        }

        [Fact]
        public void AddTargetCommand_AddsValidHost_AndClearsInputs()
        {
            const string testHost = "203.0.113.21"; // TEST-NET-3 (RFC 5737)

            var vm = CreateSut();
            try
            {
                RemoveAll(vm, testHost);
                var before = vm.Targets.Count;
                vm.NewHostInput = "  " + testHost + "  ";
                vm.NewDescriptionInput = "Test-beskrivelse";

                vm.AddTargetCommand.Execute(null);

                Assert.Equal(before + 1, vm.Targets.Count);
                Assert.Contains(vm.Targets, t => t.HostOrIp == testHost && t.Description == "Test-beskrivelse");
                // Input-felterne skal ryddes ved succes, så brugeren kan tilføje det næste mål.
                Assert.Equal(string.Empty, vm.NewHostInput);
                Assert.Equal(string.Empty, vm.NewDescriptionInput);
            }
            finally
            {
                RemoveAll(vm, testHost);
                vm.Dispose();
            }
        }

        [Fact]
        public void AddTargetCommand_RejectsCaseInsensitiveDuplicate()
        {
            const string testHost = "203.0.113.22";

            var vm = CreateSut();
            try
            {
                RemoveAll(vm, testHost);
                vm.NewHostInput = testHost;
                vm.AddTargetCommand.Execute(null);
                var afterFirst = vm.Targets.Count;

                // Samme host i en anden case skal opfattes som samme mål.
                vm.NewHostInput = testHost.ToUpperInvariant();
                vm.AddTargetCommand.Execute(null);

                Assert.Equal(afterFirst, vm.Targets.Count);
                Assert.Contains("overvåges allerede", vm.StatusMessage);
            }
            finally
            {
                RemoveAll(vm, testHost);
                vm.Dispose();
            }
        }

        [Fact]
        public void RemoveTargetCommand_RemovesTarget()
        {
            const string testHost = "203.0.113.23";

            var vm = CreateSut();
            RemoveAll(vm, testHost);
            vm.NewHostInput = testHost;
            vm.AddTargetCommand.Execute(null);
            var target = vm.Targets.Single(t => t.HostOrIp == testHost);

            vm.RemoveTargetCommand.Execute(target);

            Assert.DoesNotContain(vm.Targets, t => t.HostOrIp == testHost);
            vm.Dispose();
        }

        [Theory]
        [InlineData(0, 1)]      // under minimum klemmes op til 1
        [InlineData(-5, 1)]
        [InlineData(61, 60)]    // over maksimum klemmes ned til 60
        [InlineData(1000, 60)]
        [InlineData(15, 15)]    // gyldig værdi ændres ikke
        public void IntervalSeconds_ClampsToValidRange(int input, int expected)
        {
            var vm = CreateSut();
            vm.IntervalSeconds = input;
            Assert.Equal(expected, vm.IntervalSeconds);
            vm.Dispose();
        }

        [Fact]
        public void TestConnectionCommand_CannotExecute_WhileTargetIsTesting()
        {
            var vm = CreateSut();
            var target = new PingMonitorTarget { HostOrIp = "203.0.113.24" };

            Assert.True(vm.TestConnectionCommand.CanExecute(target));

            target.IsTesting = true;
            Assert.False(vm.TestConnectionCommand.CanExecute(target));

            target.IsTesting = false;
            Assert.True(vm.TestConnectionCommand.CanExecute(target));

            vm.Dispose();
        }
    }
}
