using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace M1Scan.Services
{
    /// <summary>
    /// Lookup Country and ASN for IP addresses via ip-api.com batch endpoint.
    ///
    /// PRIVATLIV OG TRANSPORT — læs før dette bruges et nyt sted:
    ///
    /// Opslaget sender brugerens traceroute-hop (altså deres rute ud gennem ISP'en) til
    /// en tredjepart. Derfor er funktionen som standard SLÅET FRA og skal aktiveres
    /// eksplicit af brugeren; se <see cref="IsEnabled"/>.
    ///
    /// ip-api.com's gratis niveau understøtter kun HTTP — HTTPS svarer 403 og kræver
    /// betalt abonnement (verificeret). Trafikken er derfor ulæselig-sikret: enhver
    /// på ruten kan både læse hvilke IP'er der slås op OG ændre svaret. Country og ASN
    /// behandles derfor som upålidelige visningsstrenge og saniteres (se Sanitize).
    ///
    /// Vil man have både privatliv og integritet, er den rigtige løsning en lokal
    /// MaxMind GeoLite2-database indlejret i assemblyen — præcis samme mønster som
    /// OUI-tabellen i Utils/OuiLookup.cs bruger. Det fjerner tredjeparten helt.
    ///
    /// Rate limits: 15 requests per minute per IP address (free tier), max 100 IP'er
    /// pr. batch-kald. Session cache prevents repeated lookups during a session.
    /// Note: ip-api.com free tier is for non-commercial use only.
    /// </summary>
    public interface IGeoIpService
    {
        /// <summary>
        /// Om geo-opslag er tilladt. Default false — intet forlader maskinen før
        /// brugeren aktivt slår det til.
        /// </summary>
        bool IsEnabled { get; set; }

        /// <summary>
        /// Batch lookup of country and ASN for a set of public IP addresses.
        /// Private/reserved IPs are filtered out before the API call.
        /// Results are cached in memory for the lifetime of the process.
        /// Returns a dict mapping IP -> (Country, Asn); private IPs are absent from the dict.
        /// </summary>
        Task<Dictionary<string, (string Country, string Asn)>> LookupBatchAsync(
            IEnumerable<string> ips,
            CancellationToken ct = default);
    }

    public class GeoIpService : IGeoIpService
    {
        private static readonly HttpClient _httpClient = new();
        private const string BatchEndpoint = "http://ip-api.com/batch";

        // ip-api afviser batches over 100 IP'er. Uden opdeling fejlede hele kaldet
        // stille når der blev sendt flere.
        private const int MaxBatchSize = 100;

        // Loft på hvor mange tegn vi accepterer fra et felt. Svaret kommer over HTTP
        // og kan være manipuleret; en uendelig lang streng skal ikke kunne sprænge UI'et.
        private const int MaxFieldLength = 64;

        // Loft på cachen, så en langvarig session ikke vokser ubegrænset.
        private const int MaxCacheEntries = 5_000;

        /// <inheritdoc />
        public bool IsEnabled { get; set; }

        // Session-scoped in-memory cache (process lifetime, not persistent to disk).
        // Thread-safe via lock; no expiry (ASN/country for a given IP don't change within a session).
        private static readonly Dictionary<string, (string Country, string Asn)> _cache = new();
        private static readonly object _cacheLock = new();

        // Private/reserved IP ranges per RFC1918, RFC3927, RFC6598, RFC4291, etc.
        private static readonly (IPAddress Low, IPAddress High)[] PrivateRanges = new[]
        {
            (IPAddress.Parse("10.0.0.0"),       IPAddress.Parse("10.255.255.255")),       // RFC1918
            (IPAddress.Parse("172.16.0.0"),     IPAddress.Parse("172.31.255.255")),       // RFC1918
            (IPAddress.Parse("192.168.0.0"),    IPAddress.Parse("192.168.255.255")),      // RFC1918
            (IPAddress.Parse("127.0.0.0"),      IPAddress.Parse("127.255.255.255")),      // Loopback
            (IPAddress.Parse("169.254.0.0"),    IPAddress.Parse("169.254.255.255")),      // Link-local
            (IPAddress.Parse("100.64.0.0"),     IPAddress.Parse("100.127.255.255")),      // Carrier-grade NAT (RFC6598)
        };

        public GeoIpService()
        {
            _httpClient.Timeout = TimeSpan.FromSeconds(10);
        }

        /// <summary>
        /// Check if an IP is in a private/reserved range.
        /// </summary>
        private static bool IsPrivateIp(string ipStr)
        {
            if (!IPAddress.TryParse(ipStr, out var ip))
                return true; // Invalid IPs treated as private, skip them.

            foreach (var (low, high) in PrivateRanges)
            {
                if (CompareIps(ip, low) >= 0 && CompareIps(ip, high) <= 0)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Compare two IP addresses. Returns negative if a < b, 0 if equal, positive if a > b.
        /// </summary>
        private static int CompareIps(IPAddress a, IPAddress b)
        {
            byte[] aBytes = a.GetAddressBytes();
            byte[] bBytes = b.GetAddressBytes();

            for (int i = 0; i < Math.Min(aBytes.Length, bBytes.Length); i++)
            {
                if (aBytes[i] != bBytes[i])
                    return aBytes[i].CompareTo(bBytes[i]);
            }

            return aBytes.Length.CompareTo(bBytes.Length);
        }

        public async Task<Dictionary<string, (string Country, string Asn)>> LookupBatchAsync(
            IEnumerable<string> ips,
            CancellationToken ct = default)
        {
            // Slået fra som standard: intet forlader maskinen uden brugerens samtykke.
            if (!IsEnabled)
                return new Dictionary<string, (string, string)>();

            // Filter to public IPs only.
            var publicIps = ips.Where(ip => !IsPrivateIp(ip)).Distinct().ToList();

            if (publicIps.Count == 0)
                return new Dictionary<string, (string, string)>();

            // Check cache for IPs we've already looked up.
            var result = new Dictionary<string, (string, string)>();
            var missingIps = new List<string>();

            lock (_cacheLock)
            {
                foreach (var ip in publicIps)
                {
                    if (_cache.TryGetValue(ip, out var cached))
                    {
                        result[ip] = cached;
                    }
                    else
                    {
                        missingIps.Add(ip);
                    }
                }
            }

            // If all IPs are cached, return immediately.
            if (missingIps.Count == 0)
                return result;

            try
            {
                // Opdel i batches af højst 100 — ip-api afviser større.
                for (int offset = 0; offset < missingIps.Count; offset += MaxBatchSize)
                {
                    var chunk = missingIps.Skip(offset).Take(MaxBatchSize).ToList();
                    await LookupChunkAsync(chunk, result, ct);
                }

                return result;
            }
            catch (HttpRequestException ex)
            {
                System.Diagnostics.Debug.WriteLine($"GeoIpService: HTTP error: {ex.Message}");
                return new Dictionary<string, (string, string)>();
            }
            catch (JsonException ex)
            {
                System.Diagnostics.Debug.WriteLine($"GeoIpService: JSON parse error: {ex.Message}");
                return new Dictionary<string, (string, string)>();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GeoIpService: Unexpected error: {ex.Message}");
                return new Dictionary<string, (string, string)>();
            }
        }

        private async Task LookupChunkAsync(
            List<string> chunk,
            Dictionary<string, (string, string)> result,
            CancellationToken ct)
        {
            // Build batch request: {"query":"x.x.x.x","fields":"query,country,as,status"}
            var queries = chunk.Select(ip => new { query = ip, fields = "query,country,as,status" }).ToList();
            var json = JsonSerializer.Serialize(queries);
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            using var response = await _httpClient.PostAsync(BatchEndpoint, content, ct);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Array)
            {
                System.Diagnostics.Debug.WriteLine($"GeoIpService: Unexpected response format (expected array, got {root.ValueKind})");
                return;
            }

            // Kun IP'er vi selv har spurgt om accepteres. Svaret kommer over HTTP og
            // kan være manipuleret undervejs; uden dette filter kunne en angriber
            // indsætte geo-data for vilkårlige adresser i vores resultat og cache.
            var requested = new HashSet<string>(chunk, StringComparer.OrdinalIgnoreCase);

            foreach (var item in root.EnumerateArray())
            {
                string? ip = item.TryGetProperty("query", out var q) ? q.GetString() : null;
                string? country = item.TryGetProperty("country", out var c) ? c.GetString() : null;
                string? asn = item.TryGetProperty("as", out var a) ? a.GetString() : null;
                string? status = item.TryGetProperty("status", out var s) ? s.GetString() : null;

                if (status != "success") continue;
                if (string.IsNullOrEmpty(ip) || !requested.Contains(ip)) continue;

                var safeCountry = Sanitize(country);
                if (string.IsNullOrEmpty(safeCountry)) continue;

                var safeAsn = Sanitize(asn);
                if (string.IsNullOrEmpty(safeAsn)) safeAsn = "—";

                result[ip] = (safeCountry, safeAsn);

                lock (_cacheLock)
                {
                    // Simpel loft-håndtering: ryd cachen når den bliver for stor.
                    // Geo-data er billige at hente igen, og en session der rammer
                    // 5000 unikke offentlige IP'er er ekstraordinær.
                    if (_cache.Count >= MaxCacheEntries) _cache.Clear();
                    _cache[ip] = (safeCountry, safeAsn);
                }
            }
        }

        /// <summary>
        /// Gør en streng fra det (ukrypterede) HTTP-svar sikker at vise: fjerner
        /// kontroltegn og afkorter. Værdien ender i UI'et, og transporten kan være
        /// manipuleret, så den behandles som fjendtligt input — ikke som data.
        /// </summary>
        private static string Sanitize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            var cleaned = new string(value
                .Where(ch => !char.IsControl(ch))
                .Take(MaxFieldLength)
                .ToArray())
                .Trim();

            return cleaned;
        }
    }
}
