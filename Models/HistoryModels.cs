using System;

namespace M1Scan.Models
{
    /// <summary>Én latency/jitter/loss/score-sample fra HistoryService's baggrundssampler.</summary>
    public class HistorySample
    {
        public DateTimeOffset Timestamp   { get; init; }
        public double?        WanAvgMs    { get; init; }
        public double?        WanJitterMs { get; init; }
        public double?        WanLossPct  { get; init; }
        public double?        GwAvgMs     { get; init; }
        public double?        GwJitterMs  { get; init; }
        public double?        GwLossPct   { get; init; }
        public int?           HealthScore { get; init; }
        public string?        HealthGrade { get; init; }
    }

    /// <summary>Sammendrag af ét scan (Scan-siden), gemt til historikkens tidslinje.</summary>
    public class ScanSummary
    {
        public DateTimeOffset Timestamp       { get; init; }
        public int            HostCount       { get; init; }
        public int            ReachableCount  { get; init; }
        public bool           WasComplete     { get; init; }
    }

    /// <summary>Én enheds-hændelse ("ny enhed set", "enhed forsvundet") til historikkens log.</summary>
    public class DeviceEvent
    {
        public DateTimeOffset Timestamp { get; init; }
        public string         Mac       { get; init; } = string.Empty;
        public string         EventType { get; init; } = string.Empty;
        public string?        Name      { get; init; }
    }

    /// <summary>Kendte event-typer for <see cref="DeviceEvent"/>.EventType — plain strings i DB'en, konstanter her for at undgå tastefejl ved kaldesteder.</summary>
    public static class DeviceEventType
    {
        public const string NewDevice = "new_device";
        public const string DeviceGone = "device_gone";
    }

    /// <summary>Én latency-sample for ét hop under en løbende traceroute-probe.</summary>
    public class TraceSample
    {
        public DateTimeOffset Timestamp  { get; init; }
        public int            HopNumber  { get; init; }
        public string?        IpAddress  { get; init; }
        public double?        LatencyMs  { get; init; }
    }
}
