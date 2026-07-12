using System;
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
        private Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);

        public MacAliasService()
        {
            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "M1Scan");
            Directory.CreateDirectory(appDataPath);
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
                _aliases = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                    ?? new(StringComparer.OrdinalIgnoreCase);
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
                var json = JsonSerializer.Serialize(_aliases, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(_filePath, json);
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
            if (_aliases.Remove(normalized))
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

        public Dictionary<string, string> GetAll() => new(_aliases);
    }
}
