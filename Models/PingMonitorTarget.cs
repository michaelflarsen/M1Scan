using System;
using M1Scan.Utils;

namespace M1Scan.Models
{
    public enum PingMonitorStatus { Waiting, Online, Offline, Timeout }

    /// <summary>
    /// Ét overvåget mål på Ping monitor-siden. Id er en stabil GUID, ikke IP'en selv,
    /// så en rettelse af HostOrIp ikke afskærer målet fra sin egen historik i
    /// HistoryService's ping_samples-tabel.
    /// </summary>
    public class PingMonitorTarget : ObservableObject
    {
        private string _hostOrIp = string.Empty;
        private string _description = string.Empty;
        private PingMonitorStatus _status = PingMonitorStatus.Waiting;
        private DateTime _lastChecked = DateTime.MinValue;
        private double _uptimePercent;
        private bool _isUptimeLoaded;

        public string Id { get; init; } = Guid.NewGuid().ToString("N");

        public string HostOrIp
        {
            get => _hostOrIp;
            set => SetProperty(ref _hostOrIp, value);
        }

        public string Description
        {
            get => _description;
            set => SetProperty(ref _description, value);
        }

        public PingMonitorStatus Status
        {
            get => _status;
            set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(StatusText)); }
        }

        public DateTime LastChecked
        {
            get => _lastChecked;
            set { if (SetProperty(ref _lastChecked, value)) OnPropertyChanged(nameof(LastCheckedFormatted)); }
        }

        /// <summary>Uptime % over det viste vindue (fra HistoryService's ping_samples), ikke kun siden appen startede.</summary>
        public double UptimePercent
        {
            get => _uptimePercent;
            set { if (SetProperty(ref _uptimePercent, value)) OnPropertyChanged(nameof(UptimeDisplay)); }
        }

        public bool IsUptimeLoaded
        {
            get => _isUptimeLoaded;
            set => SetProperty(ref _isUptimeLoaded, value);
        }

        /// <summary>Live rullende vindue til sparkline + min/avg/max/jitter/loss — nulstilles ved genstart, ligesom Dashboard's WanSeries.</summary>
        public LatencySeries Series { get; } = new() { Label = "" };

        public string LastCheckedFormatted =>
            _lastChecked == DateTime.MinValue ? "—" : _lastChecked.ToString("HH:mm:ss");

        public string UptimeDisplay => IsUptimeLoaded ? $"{UptimePercent:F1}%" : "—";

        public string StatusText => _status switch
        {
            PingMonitorStatus.Online  => "Online",
            PingMonitorStatus.Offline => "Offline",
            PingMonitorStatus.Timeout => "Timeout",
            _                         => "Venter..."
        };
    }
}
