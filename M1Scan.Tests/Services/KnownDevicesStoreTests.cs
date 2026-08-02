using System;
using M1Scan.Services;
using Xunit;

namespace M1Scan.Tests.Services
{
    /// <summary>
    /// wasNewlyCreated skal afgøre "ny enhed"-events pålideligt. En tidligere
    /// version sammenlignede firstSeen == lastSeen, men begge sættes fra to
    /// separate DateTimeOffset.Now-kald og er derfor stort set aldrig
    /// bit-identiske — det fik reelt "ny enhed"-historik til aldrig at blive
    /// skrevet. Disse tests låser den rigtige adfærd fast.
    /// </summary>
    public class KnownDevicesStoreTests
    {
        [Fact]
        public void Observe_FirstTimeMac_ReportsWasNewlyCreated()
        {
            var store = new KnownDevicesStore();
            var mac = RandomMac();

            store.Observe(mac, "192.168.1.10", "Vendor", seedAsKnown: false, out var wasNewlyCreated);

            Assert.True(wasNewlyCreated);
        }

        [Fact]
        public void Observe_SameMacTwice_OnlyFirstCallReportsWasNewlyCreated()
        {
            var store = new KnownDevicesStore();
            var mac = RandomMac();

            store.Observe(mac, "192.168.1.10", "Vendor", seedAsKnown: false, out var first);
            store.Observe(mac, "192.168.1.11", "Vendor", seedAsKnown: false, out var second);

            Assert.True(first);
            Assert.False(second);
        }

        [Fact]
        public void Observe_SeededDevice_StillReportsWasNewlyCreated()
        {
            // seedAsKnown styrer kun acknowledged (bruges til "NY"-badge i UI'et),
            // ikke om det er den første observation af enheden i denne proces —
            // de to koncepter er bevidst adskilt.
            var store = new KnownDevicesStore();
            var mac = RandomMac();

            store.Observe(mac, "192.168.1.10", "Vendor", seedAsKnown: true, out var wasNewlyCreated);

            Assert.True(wasNewlyCreated);
        }

        [Fact]
        public void Observe_WithoutOutParam_StillWorks()
        {
            var store = new KnownDevicesStore();
            var mac = RandomMac();

            var dev = store.Observe(mac, "192.168.1.10", "Vendor", seedAsKnown: false);

            Assert.Equal("192.168.1.10", dev.lastIp);
        }

        private static string RandomMac()
        {
            var b = new byte[6];
            Random.Shared.NextBytes(b);
            return string.Join(":", Array.ConvertAll(b, x => x.ToString("X2")));
        }
    }
}
