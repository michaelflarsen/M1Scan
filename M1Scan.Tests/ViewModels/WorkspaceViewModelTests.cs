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
    /// AddEntry manglede IP-validering, i modsætning til sin søster BulkAdd (som er
    /// beskyttet af sin canExecute) og OpenInBrowserCommand i NetworkScanViewModel
    /// (som kræver IPAddress.TryParse på hosten). Uden valideringen endte fri tekst i
    /// PingEntry.IpAddress, som WorkspaceView.xaml senere bygger direkte ind i
    /// "http://{IpAddress}" uden yderligere kontrol.
    /// </summary>
    public class WorkspaceViewModelTests
    {
        // Minimale fakes — WorkspaceViewModel starter ingen timere før OnActivated()
        // kaldes (se IActivatablePage), så disse behøver ikke gøre andet.
        private sealed class FakeIpConfigService : IIpConfigService
        {
            public Task<IpConfigResult> SetStaticIpAsync(string a, string b, string c, string d) =>
                Task.FromResult(IpConfigResult.Ok());
            public Task<IpConfigResult> SetDhcpAsync(string a) => Task.FromResult(IpConfigResult.Ok());
            public Task<IpConfigResult> ResetNetworkAdapterAsync(string a) => Task.FromResult(IpConfigResult.Ok());
            public Task<IpConfigResult> FlushDnsAsync() => Task.FromResult(IpConfigResult.Ok());
            public Task<IpConfigResult> RenewDhcpAsync(string a) => Task.FromResult(IpConfigResult.Ok());
        }

        private sealed class FakeExportService : IExportService
        {
            public Task ExportHostsAsync(IEnumerable<HostInfo> hosts, string filePath) => Task.CompletedTask;
            public Task ExportWatchListAsync(IEnumerable<PingEntry> entries, string filePath) => Task.CompletedTask;
        }

        private static WorkspaceViewModel CreateSut() =>
            new(new FakeIpConfigService(), new FakeExportService());

        [Fact]
        public void AddEntryCommand_RejectsNonIpText()
        {
            // WorkspaceViewModel indlæser brugerens gemte watchlist fra
            // %APPDATA%\M1Scan\workspace.json ved konstruktion, så listen er ikke
            // nødvendigvis tom — testen måler ændringen, ikke et antaget udgangspunkt.
            var vm = CreateSut();
            var before = vm.WatchList.Count;
            vm.NewIpInput = "ikke-en-ip";

            vm.AddEntryCommand.Execute(null);

            Assert.Equal(before, vm.WatchList.Count);
            vm.Dispose();
        }

        [Fact]
        public void AddEntryCommand_AcceptsValidIp()
        {
            // Meget usandsynlig at kollidere med en rigtig entry i brugerens watchlist.
            const string testIp = "203.0.113.77"; // TEST-NET-3 (RFC 5737) — reserveret til dokumentation

            var vm = CreateSut();
            try
            {
                // En tidligere fejlet testkørsel kan have efterladt entryen (assert
                // der fejler før oprydning når at køre) — start fra et kendt punkt.
                var stale = vm.WatchList.Count(e => e.IpAddress == testIp);
                for (int i = 0; i < stale; i++)
                    vm.RemoveEntryCommand.Execute(vm.WatchList.First(e => e.IpAddress == testIp));

                var before = vm.WatchList.Count;
                vm.NewIpInput = testIp;

                vm.AddEntryCommand.Execute(null);

                Assert.Equal(before + 1, vm.WatchList.Count);
                Assert.Contains(vm.WatchList, e => e.IpAddress == testIp);
            }
            finally
            {
                // try/finally, ikke kode efter assert: skal også køre hvis assertet
                // ovenfor fejler, ellers forurener testen brugerens rigtige fil.
                foreach (var e in vm.WatchList.Where(e => e.IpAddress == testIp).ToList())
                    vm.RemoveEntryCommand.Execute(e);
                vm.Dispose();
            }
        }

        [Fact]
        public void AddEntryCommand_ClearsInputOnlyWhenAccepted()
        {
            var vm = CreateSut();
            vm.NewIpInput = "ikke-en-ip";

            vm.AddEntryCommand.Execute(null);

            // Ugyldigt input skal blive stående, så brugeren kan rette det —
            // ikke forsvinde stille.
            Assert.Equal("ikke-en-ip", vm.NewIpInput);
            vm.Dispose();
        }
    }
}
