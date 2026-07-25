using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace M1Scan.Services
{
    /// <summary>
    /// Brugerens egne navne på enheder, slået op på MAC-adresse.
    ///
    /// Hvorfor det er nødvendigt: et hostnavn kan komme fra tre kilder — enhedens eget
    /// mDNS-navn, routerens PTR-record og NetBIOS — og de er ofte uenige. Ingen af dem
    /// har altid ret:
    ///
    ///   • En PTR-record kan pege på en TJENESTE i stedet for maskinen
    ///     (192.168.5.4 svarede "seer.dk", men maskinen hedder TechnoBunker).
    ///   • Et mDNS-navn kan være en generisk fabriksstreng, mens routerens
    ///     DHCP-navn er det brugeren selv har sat ("Android_Z4LT3W20" vs
    ///     "Ann-Sophies-A36").
    ///
    /// Der findes ikke en rangordning der rammer rigtigt for alle enheder, så det sidste
    /// ord må være brugerens. Navnet gemmes på MAC-adressen og følger derfor enheden
    /// selv om den får en ny IP.
    ///
    /// Adskilt fra <see cref="IMacAliasService"/> med vilje: dét er producent-aliaser
    /// på OUI-præfiks (alle enheder fra samme fabrikant), mens dette er ét navn til
    /// én bestemt enhed. At blande dem i samme fil ville også kræve en migrering af
    /// brugerens eksisterende mac-aliases.json.
    /// </summary>
    public interface IDeviceNameService
    {
        Task LoadAsync();

        /// <summary>Brugerens navn for denne MAC, eller null.</summary>
        string? Lookup(string mac);

        /// <summary>Sætter (eller med tom/null tekst: fjerner) navnet for en MAC.</summary>
        Task SetAsync(string mac, string? name);

        /// <summary>Alle navne som et snapshot (MAC → navn).</summary>
        IReadOnlyDictionary<string, string> GetAll();
    }

    public class DeviceNameService : IDeviceNameService
    {
        private readonly string _filePath;

        // ConcurrentDictionary: Lookup kaldes fra scan-trådene mens UI-tråden kan
        // være i gang med at gemme et nyt navn.
        private ConcurrentDictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

        public DeviceNameService()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "M1Scan");

            // I/O-fejl her må ikke forhindre appen i at starte — navne er en
            // bekvemmelighed, ikke en forudsætning. SetAsync opretter mappen igen.
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DeviceNameService: kunne ikke oprette {dir}: {ex.Message}");
            }

            _filePath = Path.Combine(dir, "device-names.json");
        }

        /// <summary>Ensretter MAC-format, så "aa:bb:.." og "AA-BB-.." rammer samme nøgle.</summary>
        public static string Normalize(string mac) =>
            (mac ?? string.Empty).Replace(":", "").Replace("-", "").Replace(".", "").ToUpperInvariant();

        public async Task LoadAsync()
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    _names = new(StringComparer.OrdinalIgnoreCase);
                    return;
                }

                var json = await File.ReadAllTextAsync(_filePath);
                var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                _names = loaded is null
                    ? new(StringComparer.OrdinalIgnoreCase)
                    : new(loaded, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DeviceNameService.LoadAsync fejlede: {ex.Message}");
                _names = new(StringComparer.OrdinalIgnoreCase);
            }
        }

        public string? Lookup(string mac)
        {
            if (string.IsNullOrEmpty(mac)) return null;
            return _names.TryGetValue(Normalize(mac), out var name) ? name : null;
        }

        public async Task SetAsync(string mac, string? name)
        {
            var key = Normalize(mac);

            // Kun en fuld, gyldig MAC (12 hex-tegn) identificerer én enhed. Længde-
            // tjekket alene ville fx acceptere "GGGGGGGGGGGG" som en gyldig nøgle —
            // ufarligt med de kaldesteder der findes i dag (alle giver mac fra
            // host.MacAddress, som appen selv formaterer fra ARP-tabellen), men
            // metoden er public API på servicen og bør ikke stole på det.
            if (key.Length != 12 || !key.All(Uri.IsHexDigit)) return;

            var trimmed = name?.Trim();
            if (string.IsNullOrEmpty(trimmed)) _names.TryRemove(key, out _);
            else                               _names[key] = trimmed;

            await SaveAsync();
        }

        public IReadOnlyDictionary<string, string> GetAll() =>
            new Dictionary<string, string>(_names, StringComparer.OrdinalIgnoreCase);

        private async Task SaveAsync()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                var json = JsonSerializer.Serialize(_names, new JsonSerializerOptions { WriteIndented = true });

                // Atomisk: skriv til temp og byt ind. En afbrudt WriteAllText ville
                // efterlade en tom fil hvor brugerens navne før lå.
                var tmp = _filePath + ".tmp";
                await File.WriteAllTextAsync(tmp, json);
                File.Move(tmp, _filePath, overwrite: true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DeviceNameService.SaveAsync fejlede: {ex.Message}");
            }
        }
    }
}
