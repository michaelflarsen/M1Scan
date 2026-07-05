using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Threading.Tasks;
using M1Scan.Models;

namespace M1Scan.Services
{
    public interface IExportService
    {
        Task ExportHostsAsync(IEnumerable<HostInfo> hosts, string filePath);
        Task ExportWatchListAsync(IEnumerable<PingEntry> entries, string filePath);
    }

    // Eksporterer scan-resultater / watch list til CSV eller JSON. Formatet
    // afgøres af filendelsen på den valgte fil (.json → JSON, ellers CSV), så
    // ViewModel'en blot skal videregive stien fra en SaveFileDialog.
    public class ExportService : IExportService
    {
        // Tillad æ/ø/å m.fl. uescapet i JSON (Latin-1 Supplement), så danske
        // hostnames/beskrivelser er læsbare direkte i en teksteditor.
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.BasicLatin, UnicodeRanges.Latin1Supplement)
        };

        private sealed record HostRow(
            string IpAddress, string HostName, string MacAddress, string Vendor,
            string Status, int ResponseTimeMs, string OsGuess, string NetBiosName,
            bool Port80Open, bool Port443Open, bool Port8080Open, bool Port502Open,
            string LastSeen);

        private sealed record WatchRow(
            string IpAddress, string Description, string Status, long RoundtripMs,
            string LastChecked, bool? Port80Open, bool? Port443Open, bool? Port8080Open);

        public async Task ExportHostsAsync(IEnumerable<HostInfo> hosts, string filePath)
        {
            var rows = hosts.Select(h => new HostRow(
                h.IpAddress, h.HostName, h.MacAddress, h.Vendor,
                h.Status, h.ResponseTime, h.OsGuess, h.NetBiosName,
                h.IsPort80Open, h.IsPort443Open, h.IsPort8080Open, h.IsPort502Open,
                h.LastSeenFormatted)).ToList();

            if (IsJson(filePath))
                await WriteJsonAsync(rows, filePath);
            else
                await WriteCsvAsync(rows, filePath,
                    new[] { "IpAddress", "HostName", "MacAddress", "Vendor", "Status", "ResponseTimeMs", "OsGuess", "NetBiosName", "Port80Open", "Port443Open", "Port8080Open", "Port502Open", "LastSeen" },
                    r => new object?[] { r.IpAddress, r.HostName, r.MacAddress, r.Vendor, r.Status, r.ResponseTimeMs, r.OsGuess, r.NetBiosName, r.Port80Open, r.Port443Open, r.Port8080Open, r.Port502Open, r.LastSeen });
        }

        public async Task ExportWatchListAsync(IEnumerable<PingEntry> entries, string filePath)
        {
            var rows = entries.Select(e => new WatchRow(
                e.IpAddress, e.Description, e.StatusText, e.RoundtripMs,
                e.LastCheckedFormatted, e.Port80Open, e.Port443Open, e.Port8080Open)).ToList();

            if (IsJson(filePath))
                await WriteJsonAsync(rows, filePath);
            else
                await WriteCsvAsync(rows, filePath,
                    new[] { "IpAddress", "Description", "Status", "RoundtripMs", "LastChecked", "Port80Open", "Port443Open", "Port8080Open" },
                    r => new object?[] { r.IpAddress, r.Description, r.Status, r.RoundtripMs, r.LastChecked, r.Port80Open, r.Port443Open, r.Port8080Open });
        }

        private static bool IsJson(string filePath) =>
            string.Equals(Path.GetExtension(filePath), ".json", StringComparison.OrdinalIgnoreCase);

        private static async Task WriteJsonAsync<T>(List<T> rows, string filePath)
        {
            var json = JsonSerializer.Serialize(rows, JsonOptions);
            await File.WriteAllTextAsync(filePath, json, Encoding.UTF8);
        }

        private static async Task WriteCsvAsync<T>(
            List<T> rows, string filePath, string[] header, Func<T, object?[]> selector)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(',', header.Select(EscapeCsv)));
            foreach (var row in rows)
                sb.AppendLine(string.Join(',', selector(row).Select(v => EscapeCsv(FormatField(v)))));

            await File.WriteAllTextAsync(filePath, sb.ToString(), Encoding.UTF8);
        }

        private static string FormatField(object? value) => value switch
        {
            null => string.Empty,
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };

        // RFC4180-agtig escaping: felter med komma, citationstegn eller linjeskift
        // pakkes i "..." med interne citationstegn fordoblet.
        private static string EscapeCsv(string field)
        {
            if (field.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return field;
            return "\"" + field.Replace("\"", "\"\"") + "\"";
        }
    }
}
