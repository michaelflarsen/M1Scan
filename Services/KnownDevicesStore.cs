using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using M1Scan.Models;

namespace M1Scan.Services
{
    /// <summary>
    /// Persisteret baseline af kendte enheder (MAC → KnownDevice) i
    /// %APPDATA%\M1Scan\known_devices.json. Bruges af dashboardet til
    /// at markere NYE enheder på netværket.
    /// </summary>
    public class KnownDevicesStore
    {
        private static readonly string PersistPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "M1Scan", "known_devices.json");

        private readonly Dictionary<string, KnownDevice> _devices = new();

        public bool FileExisted { get; private set; }

        public KnownDevicesStore()
        {
            Load();
        }

        public KnownDevice? Get(string mac) =>
            _devices.TryGetValue(Normalize(mac), out var d) ? d : null;

        /// <summary>
        /// Registrerer en observeret enhed. Returnerer enheden — IsNew bestemmes
        /// af acknowledged-flaget. seedAsKnown bruges ved første kørsel, så
        /// eksisterende enheder ikke alle markeres som NYE.
        /// </summary>
        public KnownDevice Observe(string mac, string ip, string vendor, bool seedAsKnown)
        {
            var key = Normalize(mac);
            if (_devices.TryGetValue(key, out var existing))
            {
                existing.lastIp   = ip;
                existing.lastSeen = DateTimeOffset.Now;
                if (!string.IsNullOrEmpty(vendor)) existing.vendor = vendor;
                return existing;
            }

            var dev = new KnownDevice
            {
                mac          = key,
                lastIp       = ip,
                vendor       = vendor,
                firstSeen    = DateTimeOffset.Now,
                lastSeen     = DateTimeOffset.Now,
                acknowledged = seedAsKnown
            };
            _devices[key] = dev;
            return dev;
        }

        public void Acknowledge(string mac)
        {
            if (_devices.TryGetValue(Normalize(mac), out var d))
                d.acknowledged = true;
        }

        public void AcknowledgeAll()
        {
            foreach (var d in _devices.Values)
                d.acknowledged = true;
        }

        /// <summary>Broadcast/multicast-MACs skal ikke baselines (falske NY-badges).</summary>
        public static bool IsRealDeviceMac(string mac)
        {
            var m = Normalize(mac);
            if (m.Length < 8) return false;
            if (m == "FF-FF-FF-FF-FF-FF") return false;
            if (m.StartsWith("01-00-5E")) return false; // IPv4 multicast
            if (m.StartsWith("33-33"))    return false; // IPv6 multicast
            if (m == "00-00-00-00-00-00") return false;
            return true;
        }

        public static string Normalize(string mac) =>
            mac.Trim().Replace(':', '-').ToUpperInvariant();

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PersistPath)!);
                var file = new KnownDevicesFile { devices = _devices.Values.OrderBy(d => d.firstSeen).ToList() };
                var json = JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(PersistPath, json);
                // Baseline er nu gemt — efterfølgende observationer i samme session
                // skal IKKE seedes som kendte
                FileExisted = true;
            }
            catch { /* ignore write errors */ }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(PersistPath)) { FileExisted = false; return; }
                FileExisted = true;
                var json = File.ReadAllText(PersistPath);
                var file = JsonSerializer.Deserialize<KnownDevicesFile>(json);
                if (file?.devices == null) return;
                foreach (var d in file.devices)
                    _devices[Normalize(d.mac)] = d;
            }
            catch { FileExisted = false; /* ignore corrupt file */ }
        }
    }
}
