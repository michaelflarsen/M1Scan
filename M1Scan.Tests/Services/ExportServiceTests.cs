using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using M1Scan.Models;
using M1Scan.Services;
using Xunit;

namespace M1Scan.Tests.Services
{
    /// <summary>
    /// CSV-eksport. Hostname, NetBIOS-navn og vendor kommer fra de enheder vi scanner
    /// — altså fra kilder vi ikke kontrollerer — så escaping er en sikkerhedsgrænse,
    /// ikke kosmetik.
    /// </summary>
    public class ExportServiceTests : IDisposable
    {
        private readonly string _dir;

        public ExportServiceTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "m1scan-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private string PathFor(string name) => Path.Combine(_dir, name);

        private static HostInfo Host(string ip, string hostName = "", string netbios = "", string vendor = "")
            => new() { IpAddress = ip, HostName = hostName, NetBiosName = netbios, Vendor = vendor };

        [Theory]
        // Excel/Sheets tolker disse tegn som starten på en formel.
        [InlineData("=cmd|'/c calc'!A1")]
        [InlineData("+1+1")]
        [InlineData("-2+3")]
        [InlineData("@SUM(A1:A9)")]
        public async Task ExportHosts_Csv_NeutralizesFormulaInjectionInHostName(string malicious)
        {
            var file = PathFor("scan.csv");

            await new ExportService().ExportHostsAsync(new[] { Host("192.168.1.5", hostName: malicious) }, file);

            var csv = await File.ReadAllTextAsync(file);
            // Feltet skal indledes med apostrof, så cellen bliver ren tekst.
            Assert.Contains("'" + malicious.TrimStart(), csv.Replace("\"", ""));
            // Og formlen må ikke stå som første tegn i en celle.
            Assert.DoesNotContain($",{malicious}", csv);
        }

        [Fact]
        public async Task ExportHosts_Csv_NeutralizesFormulaInNetBiosAndVendor()
        {
            var file = PathFor("scan.csv");

            await new ExportService().ExportHostsAsync(
                new[] { Host("192.168.1.5", netbios: "=HYPERLINK(1)", vendor: "@evil") }, file);

            var csv = await File.ReadAllTextAsync(file);
            Assert.Contains("'=HYPERLINK(1)", csv);
            Assert.Contains("'@evil", csv);
        }

        [Fact]
        public async Task ExportHosts_Csv_LeavesNumericFieldsUnquotedAndUnprefixed()
        {
            var file = PathFor("scan.csv");

            // ResponseTime er et tal: det må IKKE få apostrof, ellers holder kolonnen
            // op med at være numerisk i regnearket.
            var host = Host("192.168.1.5", hostName: "nas");
            host.ResponseTime = 12;

            await new ExportService().ExportHostsAsync(new[] { host }, file);

            var csv = await File.ReadAllTextAsync(file);
            Assert.Contains(",12,", csv);
            Assert.DoesNotContain(",'12,", csv);
        }

        [Fact]
        public async Task ExportHosts_Csv_QuotesFieldsContainingCommasAndQuotes()
        {
            var file = PathFor("scan.csv");

            await new ExportService().ExportHostsAsync(
                new[] { Host("192.168.1.5", hostName: "nas,backup", vendor: "Acme \"Pro\"") }, file);

            var csv = await File.ReadAllTextAsync(file);
            Assert.Contains("\"nas,backup\"", csv);
            Assert.Contains("\"Acme \"\"Pro\"\"\"", csv);
        }

        [Fact]
        public async Task ExportHosts_Csv_WritesHeaderAndOneRowPerHost()
        {
            var file = PathFor("scan.csv");

            await new ExportService().ExportHostsAsync(
                new[] { Host("192.168.1.5"), Host("192.168.1.6") }, file);

            var lines = await File.ReadAllLinesAsync(file);
            Assert.StartsWith("IpAddress,HostName,MacAddress", lines[0]);
            Assert.Equal(3, lines.Length); // header + 2 rækker
        }

        [Fact]
        public async Task ExportHosts_JsonExtension_WritesJsonNotCsv()
        {
            var file = PathFor("scan.json");

            await new ExportService().ExportHostsAsync(new[] { Host("192.168.1.5", hostName: "nas") }, file);

            var json = await File.ReadAllTextAsync(file);
            Assert.StartsWith("[", json.TrimStart());
            Assert.Contains("\"IpAddress\": \"192.168.1.5\"", json);
        }

        [Fact]
        public async Task ExportWatchList_Csv_NeutralizesFormulaInDescription()
        {
            var file = PathFor("watch.csv");
            var entries = new List<PingEntry>
            {
                new() { IpAddress = "10.0.0.1", Description = "=1+1" }
            };

            await new ExportService().ExportWatchListAsync(entries, file);

            var csv = await File.ReadAllTextAsync(file);
            Assert.Contains("'=1+1", csv);
        }
    }
}
