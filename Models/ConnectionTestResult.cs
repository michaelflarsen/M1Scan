using System;

namespace M1Scan.Models
{
    /// <summary>
    /// Resultat af en kort, samtidig ping-test mod en fast internet-reference
    /// (1.1.1.1) og et brugervalgt mål. Formålet er at kunne se, ved sammenligning
    /// af de to serier, om udsving skyldes brugerens egen forbindelse (begge
    /// serier rammes) eller noget efter internettet (kun målet rammes).
    /// </summary>
    public class ConnectionTestResult
    {
        public string TargetHostOrIp { get; init; } = string.Empty;
        public string TargetDescription { get; init; } = string.Empty;
        public DateTime StartedAt { get; init; }
        public int DurationSeconds { get; init; }

        /// <summary>Sekunder mellem hver ping. Står i rapporten som en påstand om
        /// hvordan der er målt, så den skal komme fra målingen — ikke fra en
        /// hardkodet tekst i visningen.</summary>
        public int IntervalSeconds { get; init; } = 1;

        // Serierne driver KUN graferne. De er rullende vinduer (LatencySeries.Capacity
        // = 150 samples), så en test på over 150 sekunder taber sine ældste målinger
        // fra dem. Alle tal rapporten påstår noget med, kommer derfor fra
        // *Stats nedenfor, som dækker hele testperioden.
        public LatencySeries ReferenceSeries { get; init; } = new() { Label = "Internet (1.1.1.1)" };
        public LatencySeries TargetSeries { get; init; } = new() { Label = "Mål" };

        public ConnectionTestStats TargetStats { get; init; } = new();
        public ConnectionTestStats ReferenceStats { get; init; } = new();

        public string Verdict { get; init; } = string.Empty;
        public string VerdictColorHex { get; init; } = "#8fa3bf";

        /// <summary>
        /// Kort statusetiket om MÅLET alene ("ONLINE — STABIL FORBINDELSE"), til den
        /// store header. Adskilt fra <see cref="Verdict"/>, som er den lange
        /// forklaring der også inddrager brugerens egen linje: rapporten bruges som
        /// dokumentation over for tredjepart, og modtageren skal kunne aflæse
        /// enhedens tilstand uden at læse hele analysen.
        /// </summary>
        public string TargetStatusLabel { get; init; } = string.Empty;
        public string TargetStatusColorHex { get; init; } = "#8fa3bf";

        /// <summary>
        /// True hvis internet-referencen selv var ustabil under testen. Så er
        /// målingen af målet mindre troværdig, og det skal fremgå af beviset —
        /// ellers dokumenterer rapporten noget den ikke kan stå inde for.
        /// </summary>
        public bool ReferenceUnreliable { get; init; }
    }

    /// <summary>
    /// Totaler for HELE en forbindelsestest. Findes ved siden af
    /// <see cref="LatencySeries"/>, fordi den er et rullende vindue på
    /// <see cref="LatencySeries.Capacity"/> samples: en test på fx 300 sekunder ville
    /// ellers tabe sine første 150 målinger, og en enhed der var nede i starten men
    /// oppe til sidst ville fremstå som fejlfri i et dokument der bruges som bevis.
    ///
    /// Alle tal og konklusioner i rapporten kommer herfra; serien bruges kun til grafen.
    /// </summary>
    public class ConnectionTestStats
    {
        private double _sum;
        private int _replies;
        private int _sent;
        private double _max;
        private readonly System.Collections.Generic.List<double> _latencies = new();

        /// <summary>Antal ping sendt i hele testperioden.</summary>
        public int Sent => _sent;

        /// <summary>Antal ping der fik svar i hele testperioden.</summary>
        public int Replies => _replies;

        public int Lost => _sent - _replies;

        public double AvgMs => _replies > 0 ? _sum / _replies : 0;
        public double MaxMs => _max;

        /// <summary>Gennemsnitlig absolut afvigelse fra gennemsnittet — samme
        /// jitter-definition som <see cref="LatencySeries"/> bruger.</summary>
        public double JitterMs
        {
            get
            {
                if (_replies < 2) return 0;
                double avg = AvgMs;
                double sum = 0;
                foreach (var v in _latencies) sum += Math.Abs(v - avg);
                return sum / _latencies.Count;
            }
        }

        public double LossPercent => _sent > 0 ? 100.0 * Lost / _sent : 0;

        public string AvgDisplay    => _replies > 0 ? $"{AvgMs:F0} ms" : "—";
        public string MaxDisplay    => _replies > 0 ? $"{MaxMs:F0} ms" : "—";
        public string JitterDisplay => _replies > 0 ? $"{JitterMs:F1} ms" : "—";
        public string LossDisplay   => _sent > 0 ? $"{LossPercent:F0}%" : "—";

        /// <summary>"58 / 60" — svar ud af sendte. Rapportens primære tal, fordi
        /// LossDisplay afrundes: ét tabt svar ud af 60 vises som "2%", og et helt
        /// fejlfrit forløb skal kunne skelnes utvetydigt fra et næsten fejlfrit.</summary>
        public string ReplyCountDisplay => _sent > 0 ? $"{_replies} / {_sent}" : "—";

        public void Add(double? latencyMs)
        {
            _sent++;
            if (!latencyMs.HasValue) return;

            _replies++;
            _sum += latencyMs.Value;
            _latencies.Add(latencyMs.Value);
            if (latencyMs.Value > _max) _max = latencyMs.Value;
        }

        /// <summary>
        /// Fælles tærskel for "denne linje har et problem". Ét sted, så statusetiket,
        /// verdict-tekst og pålidelighedsvurderingen af referencen ikke kan drifte
        /// fra hinanden. Bevidst grov: høj gennemsnitlig svartid alene er ikke et
        /// problem (et geografisk fjernt mål er naturligt langsomt) — det er tab og
        /// ustabilitet der tæller.
        /// </summary>
        public bool HasProblem => LossPercent >= 5 || JitterMs >= 30;
    }
}
