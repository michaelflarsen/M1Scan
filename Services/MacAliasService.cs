using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace M1Scan.Services
{
    public interface IMacAliasService
    {
        Task LoadAsync();
        Task SaveAsync();
        Task AddOrUpdateAsync(string macPrefix, string description);
        Task RemoveAsync(string macPrefix);
        string? Lookup(string mac);
        Dictionary<string, string> GetAll();
    }

    public class MacAliasService : IMacAliasService
    {
        private readonly string _filePath;

        // ConcurrentDictionary, IKKE Dictionary: Lookup() kaldes fra alle scan-tråde
        // via den statiske OuiLookup, mens UI-tråden muterer tabellen i
        // AddOrUpdateAsync/RemoveAsync. Samtidig read+write på en almindelig
        // Dictionary kan korruptere den og give et permanent CPU-spin.
        private ConcurrentDictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);

        public MacAliasService()
        {
            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "M1Scan");

            // Constructoren kaldes fra MainViewModel's constructor, som kaldes fra
            // MainWindow's constructor. En I/O-fejl her (offline roaming-profil,
            // ACL-problem) må ikke forhindre appen i at starte — aliaser er en
            // valgfri funktion, ikke en forudsætning. SaveAsync opretter mappen igen.
            try { Directory.CreateDirectory(appDataPath); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MacAliasService: kunne ikke oprette {appDataPath}: {ex.Message}");
            }

            _filePath = Path.Combine(appDataPath, "mac-aliases.json");
        }

        public async Task LoadAsync()
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    _aliases = new(StringComparer.OrdinalIgnoreCase);
                    return;
                }

                var json = await File.ReadAllTextAsync(_filePath);
                var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                _aliases = loaded is null
                    ? new(StringComparer.OrdinalIgnoreCase)
                    : new(loaded, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MacAliasService.LoadAsync failed: {ex.Message}");
                _aliases = new(StringComparer.OrdinalIgnoreCase);
            }
        }

        public async Task SaveAsync()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                var json = JsonSerializer.Serialize(_aliases, new JsonSerializerOptions { WriteIndented = true });

                // Skriv til en temp-fil og byt ind: File.WriteAllTextAsync trunkerer
                // først, så et crash midt i skrivningen ville efterlade en tom/halv
                // aliasfil i stedet for den forrige, brugbare version.
                var tmp = _filePath + ".tmp";
                await File.WriteAllTextAsync(tmp, json);
                File.Move(tmp, _filePath, overwrite: true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MacAliasService.SaveAsync failed: {ex.Message}");
            }
        }

        public async Task AddOrUpdateAsync(string macPrefix, string description)
        {
            if (string.IsNullOrWhiteSpace(macPrefix) || string.IsNullOrWhiteSpace(description))
                throw new ArgumentException("MAC prefix and description cannot be empty");

            var normalized = macPrefix.Replace(":", "").Replace("-", "").Replace(".", "").ToUpperInvariant();
            if (normalized.Length != 6 && normalized.Length != 12)
                throw new ArgumentException("MAC prefix must be 6 or 12 hex characters");

            _aliases[normalized] = description.Trim();
            await SaveAsync();
        }

        public async Task RemoveAsync(string macPrefix)
        {
            var normalized = macPrefix.Replace(":", "").Replace("-", "").Replace(".", "").ToUpperInvariant();
            if (_aliases.TryRemove(normalized, out _))
                await SaveAsync();
        }

        public string? Lookup(string mac)
        {
            if (string.IsNullOrEmpty(mac))
                return null;

            var normalized = mac.Replace(":", "").Replace("-", "").Replace(".", "").ToUpperInvariant();

            // Prøv fuld MAC først (12 tegn)
            if (normalized.Length >= 12 && _aliases.TryGetValue(normalized[..12], out var fullMac))
                return fullMac;

            // Prøv OUI-præfiks (6 tegn)
            if (normalized.Length >= 6 && _aliases.TryGetValue(normalized[..6], out var oui))
                return oui;

            return null;
        }

        // Snapshot — bevarer den case-insensitive comparer, så kaldere ser samme
        // opslagssemantik som servicen selv.
        public Dictionary<string, string> GetAll() => new(_aliases, StringComparer.OrdinalIgnoreCase);
    }
}
