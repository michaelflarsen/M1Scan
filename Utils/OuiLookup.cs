using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using M1Scan.Services;

namespace M1Scan.Utils
{
    // MAC-vendor-opslag ud fra IEEE's officielle MA-L-register (~39.700 OUI'er),
    // indlejret i assemblyen som en gzip-komprimeret "OUI|Vendor"-tekstfil
    // (Resources/Data/oui.txt.gz — regenerér med scripts/update-oui.py).
    // Lazy + trådsikker: filen læses og udpakkes kun ved første opslag.
    // MAC-aliaser overrides OUI-opslag hvis de er registreret via SetMacAliasService.
    public static class OuiLookup
    {
        private const string ResourceName = "M1Scan.Resources.Data.oui.txt.gz";
        private static IMacAliasService? _macAliasService;

        private static readonly Lazy<Dictionary<string, string>> _oui =
            new(LoadFromEmbeddedResource, isThreadSafe: true);

        public static void SetMacAliasService(IMacAliasService service)
        {
            _macAliasService = service;
        }

        private static Dictionary<string, string> LoadFromEmbeddedResource()
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using var resStream = asm.GetManifestResourceStream(ResourceName);
                if (resStream == null)
                {
                    System.Diagnostics.Debug.WriteLine($"OuiLookup: indlejret ressource '{ResourceName}' ikke fundet.");
                    return dict;
                }

                using var gzip = new GZipStream(resStream, CompressionMode.Decompress);
                using var reader = new StreamReader(gzip, System.Text.Encoding.UTF8);

                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    int sep = line.IndexOf('|');
                    if (sep != 6) continue; // OUI er altid 6 hex-tegn
                    dict[line[..sep]] = line[(sep + 1)..];
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"OuiLookup init failed: {ex.Message}");
            }
            return dict;
        }

        public static string Lookup(string mac)
        {
            if (string.IsNullOrEmpty(mac)) return string.Empty;

            var normalized = mac.Replace(":", "").Replace("-", "").Replace(".", "");
            if (normalized.Length < 6) return string.Empty;

            // Tjek MAC-aliaser først (overrides OUI)
            if (_macAliasService != null)
            {
                var alias = _macAliasService.Lookup(normalized);
                if (!string.IsNullOrEmpty(alias))
                    return alias;
            }

            // Fallback til OUI-lookup
            return LookupOuiOnly(normalized);
        }

        public static string LookupOuiOnly(string mac)
        {
            if (string.IsNullOrEmpty(mac)) return string.Empty;
            var normalized = mac.Replace(":", "").Replace("-", "").Replace(".", "");
            if (normalized.Length < 6) return string.Empty;
            var oui = normalized.Substring(0, 6).ToUpperInvariant();
            return _oui.Value.TryGetValue(oui, out var vendor) ? vendor : string.Empty;
        }
    }
}
