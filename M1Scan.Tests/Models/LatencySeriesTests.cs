using Xunit;
using M1Scan.Models;

namespace M1Scan.Tests.Models
{
    /// <summary>
    /// Dækker svar-tælleren (ReplyCount), som forbindelsesbeviset bruger som sit
    /// primære tal. Den findes netop fordi LossPercent afrundes i visningen: et
    /// enkelt tabt svar ud af 60 vises som "0 %", og et bevis må ikke skjule det.
    /// </summary>
    public class LatencySeriesTests
    {
        [Fact]
        public void ReplyCount_AllRepliesSucceed_EqualsSampleCount()
        {
            var s = new LatencySeries();
            s.Add(10);
            s.Add(12);
            s.Add(11);

            Assert.Equal(3, s.SampleCount);
            Assert.Equal(3, s.ReplyCount);
            Assert.Equal("3 / 3", s.ReplyCountDisplay);
        }

        [Fact]
        public void ReplyCount_CountsOnlyNonNullSamples()
        {
            var s = new LatencySeries();
            s.Add(10);
            s.Add(null); // tabt pakke
            s.Add(11);
            s.Add(null);

            Assert.Equal(4, s.SampleCount);
            Assert.Equal(2, s.ReplyCount);
            Assert.Equal("2 / 4", s.ReplyCountDisplay);
        }

        /// <summary>
        /// Netop det tilfælde tælleren findes for: ét tabt svar giver en tabsprocent
        /// der afrundes til et tal man ikke kan regne tilbage til antal pakker
        /// ("1%"), mens svar-tælleren viser præcis hvad der skete. Forskellen på
        /// 149/150 og 150/150 er hele pointen i et bevis.
        /// </summary>
        [Fact]
        public void ReplyCount_SingleLossAmongMany_ShowsExactCountNotJustPercent()
        {
            var s = new LatencySeries();
            for (int i = 0; i < 199; i++) s.Add(10);
            s.Add(null);

            // Serien har en Capacity på 150, så kun de sidste 150 samples tælles.
            Assert.Equal(LatencySeries.Capacity, s.SampleCount);
            Assert.Equal(LatencySeries.Capacity - 1, s.ReplyCount);
            Assert.Equal("149 / 150", s.ReplyCountDisplay);
        }

        /// <summary>
        /// En fejlfri serie skal kunne skelnes utvetydigt fra en med tab: begge kan
        /// vise "0%" hvis tabet er lille nok, men tælleren er altid eksakt.
        /// </summary>
        [Fact]
        public void ReplyCountDisplay_DistinguishesPerfectRunFromNearPerfectRun()
        {
            var perfect = new LatencySeries();
            var nearly = new LatencySeries();
            for (int i = 0; i < 60; i++) { perfect.Add(10); nearly.Add(10); }
            nearly.Add(null);

            Assert.Equal("60 / 60", perfect.ReplyCountDisplay);
            Assert.Equal("60 / 61", nearly.ReplyCountDisplay);
            Assert.NotEqual(perfect.ReplyCountDisplay, nearly.ReplyCountDisplay);
        }

        [Fact]
        public void ReplyCount_TotalLoss_IsZero()
        {
            var s = new LatencySeries();
            s.Add(null);
            s.Add(null);

            Assert.Equal(2, s.SampleCount);
            Assert.Equal(0, s.ReplyCount);
            Assert.Equal("0 / 2", s.ReplyCountDisplay);
        }

        [Fact]
        public void ReplyCount_NoSamples_ShowsPlaceholder()
        {
            var s = new LatencySeries();

            Assert.Equal(0, s.ReplyCount);
            Assert.Equal("—", s.ReplyCountDisplay);
        }

        [Fact]
        public void Reset_ClearsReplyCount()
        {
            var s = new LatencySeries();
            s.Add(10);
            s.Add(12);

            s.Reset();

            Assert.Equal(0, s.ReplyCount);
            Assert.Equal("—", s.ReplyCountDisplay);
        }

        /// <summary>
        /// Ældre samples falder ud af vinduet ved Capacity — tælleren må følge med,
        /// ikke akkumulere over hele appens levetid.
        /// </summary>
        [Fact]
        public void ReplyCount_RespectsCapacityWindow()
        {
            var s = new LatencySeries();
            for (int i = 0; i < LatencySeries.Capacity; i++) s.Add(null);
            for (int i = 0; i < LatencySeries.Capacity; i++) s.Add(10);

            Assert.Equal(LatencySeries.Capacity, s.SampleCount);
            Assert.Equal(LatencySeries.Capacity, s.ReplyCount); // de tabte er skubbet ud
        }
    }
}
