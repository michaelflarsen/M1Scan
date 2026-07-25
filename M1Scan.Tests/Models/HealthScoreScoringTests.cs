using M1Scan.Models;
using Xunit;

namespace M1Scan.Tests.Models
{
    /// <summary>
    /// Regressionstests for to fejl i scoringen:
    ///  1. En sonde der blev forsøgt men fejlede blev udeladt af nævneren, så
    ///     "ingen svar" gav samme normaliserede score som "perfekt svar".
    ///  2. Tabskurven havde et spring på 6 point mellem 0 % og 0,0001 % tab.
    /// </summary>
    public class HealthScoreScoringTests
    {
        private static LatencySeries Wan(double ms, int samples = 10)
        {
            var s = new LatencySeries { Label = "WAN" };
            for (int i = 0; i < samples; i++) s.Add(ms);
            return s;
        }

        // ── Manglende måling må ikke belønnes ────────────────────────────────

        [Fact]
        public void DnsAttemptedButNoReply_ScoresLowerThanFastDns()
        {
            var fast   = HealthScore.Compute(Wan(10), null, fastestDnsMs: 10, dnsAttempted: true);
            var noReply = HealthScore.Compute(Wan(10), null, fastestDnsMs: null, dnsAttempted: true);

            Assert.True(noReply.Score < fast.Score,
                $"DNS uden svar ({noReply.Score}) skal score lavere end hurtig DNS ({fast.Score}).");
        }

        [Fact]
        public void DnsAttemptedButNoReply_ScoresLowerThanSlowDns()
        {
            // Kernen i fejlen: et timeout gav tidligere en HØJERE score end et
            // langsomt men reelt svar, fordi den fejlede måling blev udeladt.
            var slow    = HealthScore.Compute(Wan(10), null, fastestDnsMs: 200, dnsAttempted: true);
            var noReply = HealthScore.Compute(Wan(10), null, fastestDnsMs: null, dnsAttempted: true);

            Assert.True(noReply.Score < slow.Score,
                $"DNS uden svar ({noReply.Score}) skal score lavere end langsom DNS ({slow.Score}).");
        }

        [Fact]
        public void DnsNotAttempted_DoesNotAffectScore()
        {
            // "Ikke målt endnu" skal være neutralt — ellers ville scoren dykke i de
            // første sekunder efter opstart, før diagnostikken er kørt.
            var notAttempted = HealthScore.Compute(Wan(10), null, fastestDnsMs: null, dnsAttempted: false);
            var perfectDns   = HealthScore.Compute(Wan(10), null, fastestDnsMs: 10, dnsAttempted: true);

            Assert.Equal(perfectDns.Score, notAttempted.Score);
        }

        [Fact]
        public void GatewayWithNoSamples_ScoresLowerThanHealthyGateway()
        {
            var healthy = new LatencySeries { Label = "GW" };
            for (int i = 0; i < 10; i++) healthy.Add(2);

            var silent = new LatencySeries { Label = "GW" }; // forsøgt, aldrig svar

            var withHealthy = HealthScore.Compute(Wan(10), healthy, null);
            var withSilent  = HealthScore.Compute(Wan(10), silent, null);

            Assert.True(withSilent.Score < withHealthy.Score,
                $"Tavs gateway ({withSilent.Score}) skal score lavere end sund gateway ({withHealthy.Score}).");
        }

        [Fact]
        public void NoGateway_IsNeutral()
        {
            var noGw      = HealthScore.Compute(Wan(10), null, null);
            var healthyGw = new LatencySeries { Label = "GW" };
            for (int i = 0; i < 10; i++) healthyGw.Add(2);

            Assert.Equal(HealthScore.Compute(Wan(10), healthyGw, null).Score, noGw.Score);
        }

        // ── Tabskurven skal være kontinuerlig ────────────────────────────────

        [Fact]
        public void SingleLostPacket_CostsAtMostThreePoints()
        {
            // 150 samples, én tabt = 0,67 % tab. Før kostede det 8 point af den
            // normaliserede score (spring fra 30 til 24 rå point) og kunne flytte
            // karakteren et helt trin på én enkelt pakke.
            var clean = new LatencySeries { Label = "WAN" };
            for (int i = 0; i < 150; i++) clean.Add(10);

            var oneLost = new LatencySeries { Label = "WAN" };
            for (int i = 0; i < 149; i++) oneLost.Add(10);
            oneLost.Add(null);

            var a = HealthScore.Compute(clean, null, null);
            var b = HealthScore.Compute(oneLost, null, null);

            var drop = a.Score - b.Score;
            Assert.InRange(drop, 0, 3);
        }

        [Fact]
        public void LossCurve_HasNoDiscontinuityJustAboveZero()
        {
            // Kernen i den oprindelige fejl: et uendeligt lille tab kostede lige så
            // meget som 1 % tab. Her sammenlignes 1 tabt ud af 1000 (0,1 %) med rent.
            var clean = new LatencySeries { Label = "WAN" };
            for (int i = 0; i < 150; i++) clean.Add(10);

            // 150 er seriens kapacitet, så det mindst mulige tab i et fuldt vindue
            // er 1/150. Ét point er den mindste synlige forskel.
            var tiny = new LatencySeries { Label = "WAN" };
            tiny.Add(null);
            for (int i = 0; i < 149; i++) tiny.Add(10);

            Assert.True(HealthScore.Compute(clean, null, null).Score
                      - HealthScore.Compute(tiny, null, null).Score <= 3);
        }

        [Fact]
        public void LossCurve_IsMonotonicallyDecreasing()
        {
            int previous = int.MaxValue;

            // 0 %, 1 %, 2 % ... 10 % tab — scoren må aldrig stige undervejs.
            foreach (var lossPercent in new[] { 0, 1, 2, 3, 5, 8, 10 })
            {
                var series = new LatencySeries { Label = "WAN" };
                int lost = lossPercent;                 // ud af 100 samples
                for (int i = 0; i < 100 - lost; i++) series.Add(10);
                for (int i = 0; i < lost; i++) series.Add(null);

                var score = HealthScore.Compute(series, null, null).Score;
                Assert.True(score <= previous,
                    $"Score steg ved {lossPercent} % tab: {score} > {previous}");
                previous = score;
            }
        }
    }
}
