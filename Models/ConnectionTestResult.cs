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

        public LatencySeries ReferenceSeries { get; init; } = new() { Label = "Internet (1.1.1.1)" };
        public LatencySeries TargetSeries { get; init; } = new() { Label = "Mål" };

        public string Verdict { get; init; } = string.Empty;
        public string VerdictColorHex { get; init; } = "#8fa3bf";
    }
}
