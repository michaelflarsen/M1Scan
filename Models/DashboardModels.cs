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
        private readonly object _lockObj = new(); // Thread-safe access during continuous probing

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

        // Spejlet af _samples.Count, opdateret under låsen. En direkte _samples.Count
        // herfra var et ubeskyttet læs af en Queue der muteres på en anden tråd.
        private volatile int _sampleCount;

        // Antal samples der faktisk fik svar. Holdes som et råt heltal ved siden af
        // LossPercent, fordi procenten afrundes i visningen: ét tabt svar ud af 60
        // vises som "0 %". Til en rapport der bruges som dokumentation skal forskellen
        // på 60/60 og 59/60 kunne ses.
        private volatile int _replyCount;

        public int SampleCount => _sampleCount;

        /// <summary>Antal ping der fik svar (samples uden tab) i det aktuelle vindue.</summary>
        public int ReplyCount => _replyCount;

        /// <summary>
        /// "58 / 60" — svar ud af afsendte i det aktuelle vindue, "—" hvis intet er
        /// målt endnu. Bemærk at tallene dækker de seneste <see cref="Capacity"/>
        /// samples, ikke hele forløbet: til en rapport over en hel testperiode skal
        /// <c>ConnectionTestStats</c> bruges i stedet.
        /// </summary>
        public string ReplyCountDisplay => SampleCount > 0 ? $"{ReplyCount} / {SampleCount}" : "—";

        public string CurrentDisplay => Current.HasValue ? $"{Current.Value:F0} ms" : "—";
        public string AvgDisplay     => SampleCount > 0 ? $"{Avg:F0} ms"      : "—";
        public string MaxDisplay     => SampleCount > 0 ? $"{Max:F0} ms"      : "—";
        public string JitterDisplay  => SampleCount > 0 ? $"{JitterMs:F1} ms" : "—";
        public string LossDisplay    => SampleCount > 0 ? $"{LossPercent:F0}%" : "—";

        public void Reset()
        {
            lock (_lockObj)
            {
                _samples.Clear();
                _sampleCount = 0;
                _replyCount = 0;
            }

            Current     = null;
            Avg         = 0;
            Max         = 0;
            JitterMs    = 0;
            LossPercent = 0;
            Values      = Array.Empty<double?>();
            OnPropertyChanged(nameof(SampleCount));
            OnPropertyChanged(nameof(ReplyCount));
            OnPropertyChanged(nameof(ReplyCountDisplay));
            OnPropertyChanged(nameof(CurrentDisplay));
            OnPropertyChanged(nameof(AvgDisplay));
            OnPropertyChanged(nameof(MaxDisplay));
            OnPropertyChanged(nameof(JitterDisplay));
            OnPropertyChanged(nameof(LossDisplay));
        }

        public void Add(double? latencyMs)
        {
            // Alt beregnes inde i låsen, men INTET publiceres derfra: property-setterne
            // rejser PropertyChanged, og WPF's binding-maskineri (plus enhver anden
            // subscriber) ville så køre vilkårlig kode mens vi holder _lockObj. En
            // handler der rører serien igen — eller tager en anden lås — deadlocker.
            // Derfor: beregn under lås, tildel og notificér bagefter.
            double? current;
            double avg, max, jitter, loss;
            double?[] snapshot;
            int count;

            lock (_lockObj)
            {
                if (_samples.Count >= Capacity)
                    _samples.Dequeue();
                _samples.Enqueue(latencyMs);

                // Single enumeration: collect valid samples and compute all stats at once
                var validSamples = new List<double>();
                int lossCount = 0;

                foreach (var sample in _samples)
                {
                    if (sample.HasValue)
                        validSamples.Add(sample.Value);
                    else
                        lossCount++;
                }

                current = latencyMs;

                if (validSamples.Count > 0)
                {
                    avg = validSamples.Average();
                    max = validSamples.Max();
                    jitter = validSamples.Count > 1
                        ? validSamples.Average(v => Math.Abs(v - avg))
                        : 0;
                }
                else
                {
                    avg = 0;
                    max = 0;
                    jitter = 0;
                }

                loss = _samples.Count > 0
                    ? 100.0 * lossCount / _samples.Count
                    : 0;

                snapshot = _samples.ToArray();
                count = _samples.Count;
                _sampleCount = count;
                _replyCount = validSamples.Count;
            }

            Current     = current;
            Avg         = avg;
            Max         = max;
            JitterMs    = jitter;
            LossPercent = loss;
            Values      = snapshot;

            OnPropertyChanged(nameof(SampleCount));
            OnPropertyChanged(nameof(ReplyCount));
            OnPropertyChanged(nameof(ReplyCountDisplay));

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

    public class UiSettings
    {
        public int  version            { get; set; } = 1;
        public bool graphsVisible      { get; set; } = false;
        public bool diagnosticsVisible { get; set; } = false;
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

    /// <summary>Én hop i en traceroute (IP, hostname, latency over tid).</summary>
    public class TraceHopInfo : ObservableObject
    {
        private string? _hostName;
        private string? _country;
        private string? _asn;

        public int HopNumber { get; set; }
        public string? IpAddress { get; set; }

        public string? HostName
        {
            get => _hostName;
            set => SetProperty(ref _hostName, value);
        }

        public string? Country
        {
            get => _country;
            set => SetProperty(ref _country, value);
        }

        public string? Asn
        {
            get => _asn;
            set => SetProperty(ref _asn, value);
        }

        public LatencySeries LatencySeries { get; set; } = new LatencySeries { Label = "" };
        public bool IsReachable { get; set; }
        public bool IsTimeout { get; set; }
    }

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
        /// <param name="gateway">Gateway-serien, eller null hvis der ikke er nogen gateway at måle</param>
        /// <param name="fastestDnsMs">Hurtigste målte DNS-svartid, eller null hvis ingen DNS-server svarede</param>
        /// <param name="dnsAttempted">
        /// Om DNS-målingen faktisk er kørt. Adskiller "ikke målt endnu" (skal ikke
        /// påvirke scoren) fra "målt, men ingen svarede" (skal koste point).
        /// </param>
        public static HealthScore Compute(LatencySeries wan, LatencySeries? gateway,
                                          double? fastestDnsMs, bool dnsAttempted = false)
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

            // Kontinuerlig tabskurve. Før var 0 % = 30 point og alt over 0 % = 24, altså
            // et spring på 6 point (8 % af scoren). Med Capacity = 150 samples er én
            // tabt pakke 0,67 % — det udløste et øjeblikkeligt karakterskift som
            // hoppede tilbage to samples senere.
            //
            // Nu falder scoren jævnt, og den første procent har en fladere hældning:
            // enkelte tabte pakker i et 150-sample-vindue er måleusikkerhed, ikke et
            // netværksproblem, mens vedvarende tab over ~1 % straffes hårdt (0 point
            // ved 5 %). Kurven er kontinuerlig i begge knæk (1 % → 27, 5 % → 0).
            double lossPts = P <= 1 ? 30 - 3 * P
                           : P <= 5 ? 27 - 27 * (P - 1) / 4
                           : 0;

            double maxPts = 75; // latency + jitter + loss

            // Vigtigt: en sonde der ER forsøgt men fejlede skal tælle 0 point OG tælle
            // med i maxPts. Før blev nævneren kun udvidet når sonden havde data, så
            // "DNS svarede aldrig" og "DNS svarede på 5 ms" gav samme normaliserede
            // score — en manglende måling blev altså belønnet som en perfekt måling.
            double dnsPts = 0;
            // En målt værdi beviser i sig selv at målingen er kørt, så flaget er kun
            // nødvendigt for at udtrykke "målt, men ingen svarede". Uden dette ||
            // ville en kalder der oplyser en svartid men glemmer flaget få DNS
            // udeladt af scoren helt.
            if (dnsAttempted || fastestDnsMs.HasValue)
            {
                if (fastestDnsMs.HasValue)
                {
                    double D = fastestDnsMs.Value;
                    dnsPts = D <= 20  ? 15
                           : D <= 100 ? 15 - 10 * (D - 20) / 80
                           : D <= 250 ? 5 - 5 * (D - 100) / 150
                           : 0;
                }
                maxPts += 15;
            }

            double gwPts = 0;
            if (gateway is not null)
            {
                // Gateway med 0 samples = forsøgt men uden svar. Det er et dårligt
                // tegn, ikke en neutral tilstand, så den koster point.
                if (gateway.SampleCount > 0)
                {
                    gwPts = gateway.LossPercent == 0 && gateway.Avg <= 5  ? 10
                          : gateway.LossPercent == 0 && gateway.Avg <= 20 ? 7
                          : gateway.LossPercent <= 2 ? 4
                          : 0;
                }
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
