using Xunit;
using M1Scan.Models;

namespace M1Scan.Tests.Models
{
    /// <summary>
    /// ConnectionTestStats findes fordi LatencySeries er et rullende vindue på
    /// Capacity samples, mens en forbindelsestest kan vare op til 300 sekunder.
    /// Rapporten bruges som dokumentation over for tredjepart, så dens tal skal
    /// dække HELE testen — ikke kun de sidste 150 målinger.
    /// </summary>
    public class ConnectionTestStatsTests
    {
        /// <summary>
        /// Kernen i fejlen typen blev indført for at rette: en enhed der er nede i
        /// starten af en lang test og oppe til sidst må ikke fremstå som fejlfri.
        /// </summary>
        [Fact]
        public void Add_OutageEarlyInLongTest_IsStillCountedAfterCapacityExceeded()
        {
            var stats = new ConnectionTestStats();
            var series = new LatencySeries();

            // 250 sekunder nede, derefter 150 sekunder oppe — i alt 400 samples.
            for (int i = 0; i < 250; i++) { stats.Add(null); series.Add(null); }
            for (int i = 0; i < 150; i++) { stats.Add(20); series.Add(20); }

            // Serien har glemt nedetiden helt: dens vindue rummer præcis de sidste
            // 150 samples, som alle fik svar. Den ville rapportere et fejlfrit forløb.
            Assert.Equal(LatencySeries.Capacity, series.ReplyCount);
            Assert.Equal("150 / 150", series.ReplyCountDisplay);
            Assert.Equal(0, series.LossPercent);

            // Stats husker hele forløbet.
            Assert.Equal(400, stats.Sent);
            Assert.Equal(150, stats.Replies);
            Assert.Equal(250, stats.Lost);
            Assert.Equal("150 / 400", stats.ReplyCountDisplay);
            Assert.True(stats.HasProblem);
        }

        [Fact]
        public void Add_AllRepliesSucceed_ReportsNoLoss()
        {
            var stats = new ConnectionTestStats();
            for (int i = 0; i < 60; i++) stats.Add(40);

            Assert.Equal(60, stats.Sent);
            Assert.Equal(60, stats.Replies);
            Assert.Equal(0, stats.Lost);
            Assert.Equal("60 / 60", stats.ReplyCountDisplay);
            Assert.Equal(0, stats.LossPercent);
            Assert.False(stats.HasProblem);
        }

        [Fact]
        public void Add_ComputesAvgAndMaxOverRepliesOnly()
        {
            var stats = new ConnectionTestStats();
            stats.Add(10);
            stats.Add(null); // tab må ikke tælle med i gennemsnittet
            stats.Add(30);

            Assert.Equal(3, stats.Sent);
            Assert.Equal(2, stats.Replies);
            Assert.Equal(20, stats.AvgMs);
            Assert.Equal(30, stats.MaxMs);
        }

        [Fact]
        public void LossPercent_IsShareOfSentNotOfReplies()
        {
            var stats = new ConnectionTestStats();
            for (int i = 0; i < 3; i++) stats.Add(10);
            stats.Add(null);

            Assert.Equal(25, stats.LossPercent);
        }

        [Fact]
        public void HasProblem_TriggersOnLossThreshold()
        {
            var ok = new ConnectionTestStats();
            for (int i = 0; i < 96; i++) ok.Add(10);
            for (int i = 0; i < 4; i++) ok.Add(null);   // 4 % tab

            var bad = new ConnectionTestStats();
            for (int i = 0; i < 94; i++) bad.Add(10);
            for (int i = 0; i < 6; i++) bad.Add(null);  // 6 % tab

            Assert.False(ok.HasProblem);
            Assert.True(bad.HasProblem);
        }

        [Fact]
        public void HasProblem_TriggersOnJitterEvenWithoutLoss()
        {
            var stats = new ConnectionTestStats();
            // Skiftevis 10 og 200 ms: intet tab, men kraftigt udsving.
            for (int i = 0; i < 20; i++) stats.Add(i % 2 == 0 ? 10 : 200);

            Assert.Equal(0, stats.LossPercent);
            Assert.True(stats.JitterMs >= 30);
            Assert.True(stats.HasProblem);
        }

        /// <summary>
        /// Høj men jævn svartid er ikke et problem — et geografisk fjernt mål er
        /// naturligt langsomt, og et bevis må ikke stemple det som ustabilt.
        /// </summary>
        [Fact]
        public void HasProblem_HighButSteadyLatencyIsNotAProblem()
        {
            var stats = new ConnectionTestStats();
            for (int i = 0; i < 30; i++) stats.Add(250);

            Assert.Equal(250, stats.AvgMs);
            Assert.False(stats.HasProblem);
        }

        [Fact]
        public void TotalLoss_ReportsZeroRepliesWithoutDividingByZero()
        {
            var stats = new ConnectionTestStats();
            for (int i = 0; i < 10; i++) stats.Add(null);

            Assert.Equal(0, stats.Replies);
            Assert.Equal(100, stats.LossPercent);
            Assert.Equal(0, stats.AvgMs);
            Assert.Equal(0, stats.JitterMs);
            Assert.Equal("—", stats.AvgDisplay);
            Assert.Equal("0 / 10", stats.ReplyCountDisplay);
        }

        [Fact]
        public void NoSamples_ShowsPlaceholdersWithoutDividingByZero()
        {
            var stats = new ConnectionTestStats();

            Assert.Equal(0, stats.Sent);
            Assert.Equal(0, stats.LossPercent);
            Assert.Equal("—", stats.ReplyCountDisplay);
            Assert.Equal("—", stats.LossDisplay);
            Assert.False(stats.HasProblem);
        }

        [Fact]
        public void SingleReply_HasNoJitter()
        {
            var stats = new ConnectionTestStats();
            stats.Add(42);

            Assert.Equal(0, stats.JitterMs);
            Assert.Equal("0,0 ms", stats.JitterDisplay.Replace(".", ","));
        }
    }
}
