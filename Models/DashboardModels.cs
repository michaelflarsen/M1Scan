using System;
using System.Collections.Generic;
using System.Linq;
using M1Scan.Utils;

namespace M1Scan.Models
{
    /// <summary>
    /// Rullende vindue af latency-samples (null = tabt pakke).
    /// Kapacitet 150 samples ~ 5 min ved 2 s interval.
    /// </summary>
    public class LatencySeries : ObservableObject
    {
        public const int Capacity = 150;

        private readonly Queue<double?> _samples = new(Capacity);

        private double? _current;
        private double  _avg;
        private double  _max;
        private double  _jitterMs;
        private double  _lossPercent;
        private IReadOnlyList<double?> _values = Array.Empty<double?>();

        public string Label { get; init; } = string.Empty;

        public IReadOnlyList<double?> Values
        {
            get => _values;
            private set => SetProperty(ref _values, value);
        }

        public double? Current     { get => _current;     private set { if (SetProperty(ref _current, value)) OnPropertyChanged(nameof(CurrentDisplay)); } }
        public double  Avg         { get => _avg;         private set { if (SetProperty(ref _avg, value)) OnPropertyChanged(nameof(AvgDisplay)); } }
        public double  Max         { get => _max;         private set { if (SetProperty(ref _max, value)) OnPropertyChanged(nameof(MaxDisplay)); } }
        public double  JitterMs    { get => _jitterMs;    private set { if (SetProperty(ref _jitterMs, value)) OnPropertyChanged(nameof(JitterDisplay)); } }
        public double  LossPercent { get => _lossPercent; private set { if (SetProperty(ref _lossPercent, value)) OnPropertyChanged(nameof(LossDisplay)); } }

        public int SampleCount => _samples.Count;

        public string CurrentDisplay => Current.HasValue ? $"{Current.Value:F0} ms" : "—";
        public string AvgDisplay     => SampleCount > 0 ? $"{Avg:F0} ms"      : "—";
        public string MaxDisplay     => SampleCount > 0 ? $"{Max:F0} ms"      : "—";
        public string JitterDisplay  => SampleCount > 0 ? $"{JitterMs:F1} ms" : "—";
        public string LossDisplay    => SampleCount > 0 ? $"{LossPercent:F0}%" : "—";

        public void Add(double? latencyMs)
        {
            if (_samples.Count >= Capacity)
                _samples.Dequeue();
            _samples.Enqueue(latencyMs);

            var ok = _samples.Where(s => s.HasValue).Select(s => s!.Value).ToList();

            Current = latencyMs;
            Avg     = ok.Count > 0 ? ok.Average() : 0;
            Max     = ok.Count > 0 ? ok.Max()     : 0;
            JitterMs = ok.Count > 1 ? ok.Average(v => Math.Abs(v - Avg)) : 0;
            LossPercent = _samples.Count > 0
                ? 100.0 * _samples.Count(s => !s.HasValue) / _samples.Count
                : 0;

            Values = _samples.ToArray();
            OnPropertyChanged(nameof(SampleCount));

            // Display-props afhænger af SampleCount — skal altid re-evalueres,
            // også når den underliggende værdi er uændret (fx tab der forbliver 0)
            OnPropertyChanged(nameof(CurrentDisplay));
            OnPropertyChanged(nameof(AvgDisplay));
            OnPropertyChanged(nameof(MaxDisplay));
            OnPropertyChanged(nameof(JitterDisplay));
            OnPropertyChanged(nameof(LossDisplay));
        }
    }

    /// <summary>Persisteret kendt enhed (known_devices.json).</summary>
    public class KnownDevice
    {
        public string mac          { get; set; } = string.Empty;
        public string lastIp       { get; set; } = string.Empty;
        public string vendor       { get; set; } = string.Empty;
        public string name         { get; set; } = string.Empty;
        public DateTimeOffset firstSeen { get; set; }
        public DateTimeOffset lastSeen  { get; set; }
        public bool acknowledged   { get; set; }
    }

    public class KnownDevicesFile
    {
        public int version { get; set; } = 1;
        public List<KnownDevice> devices { get; set; } = new();
    }

    /// <summary>Resultat af hastighedstest (persisteres i dashboard.json).</summary>
    public class SpeedTestResult
    {
        public DateTimeOffset timestamp { get; set; }
        public double downloadMbps      { get; set; }
        public double uploadMbps        { get; set; }
        public long   bytesDown         { get; set; }
        public string server            { get; set; } = string.Empty;
    }

    public class DashboardData
    {
        public int version { get; set; } = 1;
        public SpeedTestResult? lastSpeedTest { get; set; }
    }

    public enum SpeedTestPhase { Download, Upload }

    public class SpeedTestProgress
    {
        public SpeedTestPhase Phase { get; init; }
        public long BytesDone       { get; init; }
        public long TotalBytes      { get; init; }
        public double CurrentMbps   { get; init; }
        public double Percent => TotalBytes > 0 ? 100.0 * BytesDone / TotalBytes : 0;
    }

    /// <summary>Svartid for én DNS-server (null = tidsudløb).</summary>
    public class DnsTimingResult
    {
        public string  Server     { get; init; } = string.Empty;
        public string  Label      { get; init; } = string.Empty;
        public double? ResponseMs { get; init; }

        public string Display => ResponseMs.HasValue ? $"{ResponseMs.Value:F0} ms" : "Tidsudløb";
        public string Dot => ResponseMs switch
        {
            null  => "Red",
            < 50  => "Green",
            < 150 => "Orange",
            _     => "Red"
        };
    }

    public class DhcpLeaseInfo
    {
        public bool   IsDhcp     { get; init; }
        public string Server     { get; init; } = string.Empty;
        public DateTimeOffset? Obtained { get; init; }
        public DateTimeOffset? Expires  { get; init; }
    }

    public enum Ipv6Status { Unknown, Connected, NotAvailable }
    public enum CaptivePortalStatus { Unknown, None, PortalDetected, NoResponse }

    /// <summary>Samlet netværks-sundhedsscore 0-100 med karakter og dansk verdict.</summary>
    public class HealthScore
    {
        public int    Score   { get; init; }
        public string Grade   { get; init; } = "—";
        public string Verdict { get; init; } = "Måler...";
        public string ColorHex { get; init; } = "#3A4A5A";
        public bool   IsValid { get; init; }

        public static readonly HealthScore Measuring = new();

        /// <param name="wan">WAN-serien (1.1.1.1)</param>
        /// <param name="gateway">Gateway-serien eller null</param>
        /// <param name="fastestDnsMs">Hurtigste målte DNS-svartid eller null hvis ikke målt endnu</param>
        public static HealthScore Compute(LatencySeries wan, LatencySeries? gateway, double? fastestDnsMs)
        {
            if (wan.SampleCount < 5)
                return Measuring;

            if (wan.LossPercent >= 100)
                return new HealthScore
                {
                    Score = 0, Grade = "F",
                    Verdict = "Offline — ingen internetforbindelse",
                    ColorHex = "#F44336", IsValid = true
                };

            double L = wan.Avg, J = wan.JitterMs, P = wan.LossPercent;

            double latencyPts = L <= 20  ? 25
                              : L <= 60  ? 25 - 10 * (L - 20) / 40
                              : L <= 150 ? 15 - 15 * (L - 60) / 90
                              : 0;

            double jitterPts = J <= 2  ? 20
                             : J <= 10 ? 20 - 10 * (J - 2) / 8
                             : J <= 30 ? 10 - 10 * (J - 10) / 20
                             : 0;

            double lossPts = P == 0 ? 30
                           : P <= 1 ? 24
                           : P <= 5 ? 24 - 24 * (P - 1) / 4
                           : 0;

            double maxPts = 75; // latency + jitter + loss

            double dnsPts = 0;
            if (fastestDnsMs.HasValue)
            {
                double D = fastestDnsMs.Value;
                dnsPts = D <= 20  ? 15
                       : D <= 100 ? 15 - 10 * (D - 20) / 80
                       : D <= 250 ? 5 - 5 * (D - 100) / 150
                       : 0;
                maxPts += 15;
            }

            double gwPts = 0;
            if (gateway is { SampleCount: > 0 })
            {
                gwPts = gateway.LossPercent == 0 && gateway.Avg <= 5  ? 10
                      : gateway.LossPercent == 0 && gateway.Avg <= 20 ? 7
                      : gateway.LossPercent <= 2 ? 4
                      : 0;
                maxPts += 10;
            }

            int score = (int)Math.Round((latencyPts + jitterPts + lossPts + dnsPts + gwPts) * 100 / maxPts);
            score = Math.Clamp(score, 0, 100);

            var (grade, verdict, color) = score switch
            {
                >= 90 => ("A", "Fremragende — klar til gaming og videomøder", "#4CAF50"),
                >= 75 => ("B", "God — fin til streaming og hjemmearbejde",    "#8BC34A"),
                >= 60 => ("C", "Acceptabel — mindre udsving kan mærkes",      "#FF9800"),
                >= 40 => ("D", "Dårlig — forvent hak i video og spil",        "#FF5722"),
                _     => ("F", "Kritisk — forbindelsen er ustabil",           "#F44336")
            };

            return new HealthScore { Score = score, Grade = grade, Verdict = verdict, ColorHex = color, IsValid = true };
        }
    }
}
