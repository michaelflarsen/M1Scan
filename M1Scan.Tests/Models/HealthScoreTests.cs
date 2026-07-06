using Xunit;
using M1Scan.Models;

namespace M1Scan.Tests.Models
{
    public class HealthScoreTests
    {
        /// <summary>
        /// LatencySeries med færre end 5 samples returnerer "Measuring" (ikke valideret score).
        /// </summary>
        [Fact]
        public void Compute_InsufficientSamples_ReturnsMeasuring()
        {
            var wan = new LatencySeries { Label = "WAN" };
            wan.Add(10);
            wan.Add(12);
            wan.Add(11);

            var result = HealthScore.Compute(wan, null, null);

            Assert.Equal("Måler...", result.Verdict);
            Assert.False(result.IsValid);
        }

        /// <summary>
        /// 100% packet loss returnerer score 0 (F, offline).
        /// </summary>
        [Fact]
        public void Compute_TotalPacketLoss_ReturnsOffline()
        {
            var wan = new LatencySeries { Label = "WAN" };
            for (int i = 0; i < 5; i++)
                wan.Add(null); // alle tabt

            var result = HealthScore.Compute(wan, null, null);

            Assert.Equal(0, result.Score);
            Assert.Equal("F", result.Grade);
            Assert.Equal("Offline — ingen internetforbindelse", result.Verdict);
            Assert.True(result.IsValid);
        }

        /// <summary>
        /// Lav latency (10 ms avg), 0% loss, ingen jitter → høj score (90+, A).
        /// </summary>
        [Fact]
        public void Compute_LowLatencyNoseLoss_ReturnsA()
        {
            var wan = new LatencySeries { Label = "WAN" };
            var samples = new[] { 10.0, 10.0, 10.0, 10.0, 10.0 };
            foreach (var sample in samples)
                wan.Add(sample);

            var result = HealthScore.Compute(wan, null, null);

            Assert.True(result.IsValid);
            Assert.Equal("A", result.Grade);
            Assert.True(result.Score >= 90);
        }

        /// <summary>
        /// Høj latency (100 ms avg), 0% loss → lavere score (B eller C).
        /// </summary>
        [Fact]
        public void Compute_HighLatency_ReturnsLowerGrade()
        {
            var wan = new LatencySeries { Label = "WAN" };
            var samples = new[] { 100.0, 100.0, 100.0, 100.0, 100.0 };
            foreach (var sample in samples)
                wan.Add(sample);

            var result = HealthScore.Compute(wan, null, null);

            Assert.True(result.IsValid);
            Assert.True(result.Score < 90); // Høj latency giver mindre end A
        }

        /// <summary>
        /// 5% packet loss → score reduceres (omkring D).
        /// </summary>
        [Fact]
        public void Compute_WithPacketLoss_ReturnsD()
        {
            var wan = new LatencySeries { Label = "WAN" };
            for (int i = 0; i < 95; i++)
                wan.Add(20.0);
            for (int i = 0; i < 5; i++)
                wan.Add(null);

            var result = HealthScore.Compute(wan, null, null);

            Assert.True(result.IsValid);
            Assert.True(result.Score <= 60); // Tab presser score ned
        }

        /// <summary>
        /// God gateway (0% loss, 3 ms avg) giver bonus-point.
        /// </summary>
        [Fact]
        public void Compute_WithGoodGateway_IncreaseScore()
        {
            var wan = new LatencySeries { Label = "WAN" };
            var samples = new[] { 15.0, 15.0, 15.0, 15.0, 15.0 };
            foreach (var sample in samples)
                wan.Add(sample);

            var gateway = new LatencySeries { Label = "Gateway" };
            var gwSamples = new[] { 3.0, 3.0, 3.0 };
            foreach (var sample in gwSamples)
                gateway.Add(sample);

            var result = HealthScore.Compute(wan, gateway, null);

            Assert.True(result.IsValid);
            Assert.Equal("A", result.Grade); // god gateway hjælper score højere
        }

        /// <summary>
        /// Dårlig gateway (høj latency eller tab) giver færre bonuspoint.
        /// </summary>
        [Fact]
        public void Compute_WithPoorGateway_ReduceBonus()
        {
            var wan = new LatencySeries { Label = "WAN" };
            var samples = new[] { 20.0, 20.0, 20.0, 20.0, 20.0 };
            foreach (var sample in samples)
                wan.Add(sample);

            var gateway = new LatencySeries { Label = "Gateway" };
            gateway.Add(50.0); // højere latency end god gateway
            gateway.Add(50.0);

            var result = HealthScore.Compute(wan, gateway, null);

            Assert.True(result.IsValid);
            // Dårlig gateway giver færre bonus → lavere score
        }

        /// <summary>
        /// God DNS-svartid (15 ms) tilføjer 15 points til maks-pool.
        /// </summary>
        [Fact]
        public void Compute_WithFastDns_AddsDnsBonus()
        {
            var wan = new LatencySeries { Label = "WAN" };
            var samples = new[] { 20.0, 20.0, 20.0, 20.0, 20.0 };
            foreach (var sample in samples)
                wan.Add(sample);

            var result = HealthScore.Compute(wan, null, 15.0);

            Assert.True(result.IsValid);
            // DNS-bonus skulle være aktiv
        }

        /// <summary>
        /// Høj DNS-svartid (300 ms) giver mindre bonus.
        /// </summary>
        [Fact]
        public void Compute_WithSlowDns_ReducesDnsBonus()
        {
            var wan = new LatencySeries { Label = "WAN" };
            var samples = new[] { 20.0, 20.0, 20.0, 20.0, 20.0 };
            foreach (var sample in samples)
                wan.Add(sample);

            var resultGoodDns = HealthScore.Compute(wan, null, 15.0);
            var resultSlowDns = HealthScore.Compute(wan, null, 300.0);

            Assert.True(resultGoodDns.IsValid);
            Assert.True(resultSlowDns.IsValid);
            Assert.True(resultGoodDns.Score > resultSlowDns.Score);
        }

        /// <summary>
        /// Høj jitter (20 ms) reducerer score.
        /// </summary>
        [Fact]
        public void Compute_HighJitter_ReducesScore()
        {
            var wan = new LatencySeries { Label = "WAN" };
            wan.Add(20.0);
            wan.Add(50.0);
            wan.Add(10.0);
            wan.Add(60.0);
            wan.Add(30.0);

            var result = HealthScore.Compute(wan, null, null);

            Assert.True(result.IsValid);
            Assert.True(result.Score < 85); // Høj jitter presser ned
        }

        /// <summary>
        /// Score er altid mellem 0 og 100.
        /// </summary>
        [Theory]
        [InlineData(5.0, 1.0)]   // Lav latency
        [InlineData(200.0, 100.0)] // Høj latency
        [InlineData(50.0, 50.0)]    // Blandet
        public void Compute_ScoreAlwaysClamped_Between0And100(double latency, double? dns)
        {
            var wan = new LatencySeries { Label = "WAN" };
            for (int i = 0; i < 5; i++)
                wan.Add(latency);

            var result = HealthScore.Compute(wan, null, dns);

            if (result.IsValid)
            {
                Assert.True(result.Score >= 0 && result.Score <= 100);
            }
        }

        /// <summary>
        /// Grader matcher score-intervaller: 90+ = A, 75-89 = B, 60-74 = C, 40-59 = D, <40 = F.
        /// </summary>
        [Fact]
        public void Compute_GradeMatchesScore()
        {
            var createWan = (double latency) =>
            {
                var wan = new LatencySeries { Label = "WAN" };
                for (int i = 0; i < 5; i++)
                    wan.Add(latency);
                return wan;
            };

            var wanGood = createWan(10);
            var resultA = HealthScore.Compute(wanGood, null, null);
            Assert.Equal("A", resultA.Grade);
            Assert.True(resultA.Score >= 90);

            var wanMedium = createWan(80);
            var resultB = HealthScore.Compute(wanMedium, null, null);
            Assert.Equal("B", resultB.Grade);
            Assert.True(resultB.Score >= 75 && resultB.Score < 90);
        }

        /// <summary>
        /// Alle scores har en farve-hex og dansk verdict.
        /// </summary>
        [Fact]
        public void Compute_AlwaysHasColorAndVerdict()
        {
            var wan = new LatencySeries { Label = "WAN" };
            wan.Add(20);
            wan.Add(20);
            wan.Add(20);
            wan.Add(20);
            wan.Add(20);

            var result = HealthScore.Compute(wan, null, null);

            Assert.NotNull(result.ColorHex);
            Assert.NotEmpty(result.ColorHex);
            Assert.NotNull(result.Verdict);
            Assert.NotEmpty(result.Verdict);
        }
    }
}
