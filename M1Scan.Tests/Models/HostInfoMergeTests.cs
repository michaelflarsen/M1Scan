using M1Scan.Models;
using Xunit;

namespace M1Scan.Tests.Models
{
    /// <summary>
    /// MergeFrom afløser tre hånd-flettede kopier af samme logik, som var indbyrdes
    /// uenige. Disse tests fastholder de regler den samlede implementering skal følge.
    /// </summary>
    public class HostInfoMergeTests
    {
        private static HostInfo Existing() => new()
        {
            IpAddress   = "192.168.1.10",
            HostName    = "nas.local",
            MacAddress  = "AA:BB:CC:DD:EE:FF",
            Vendor      = "Synology",
            Status      = "Online",
            IsReachable = true,
            ResponseTime = 5,
            OsGuess     = "Linux / Mac"
        };

        // ── Tom data må ikke overskrive kendt data ───────────────────────────

        [Fact]
        public void EmptyFields_DoNotOverwriteKnownValues()
        {
            var host = Existing();
            host.MergeFrom(new HostInfo { IpAddress = "192.168.1.10" });

            Assert.Equal("nas.local", host.HostName);
            Assert.Equal("AA:BB:CC:DD:EE:FF", host.MacAddress);
            Assert.Equal("Synology", host.Vendor);
            Assert.Equal("Linux / Mac", host.OsGuess);
            Assert.Equal(5, host.ResponseTime);
        }

        [Fact]
        public void HostNameEqualToIp_IsTreatedAsPlaceholder_NotAName()
        {
            // Sweep-stien sætter HostName = IP som placeholder. Den må aldrig
            // overskrive et rigtigt navn der allerede er slået op.
            var host = Existing();
            host.MergeFrom(new HostInfo { IpAddress = "192.168.1.10", HostName = "192.168.1.10" });

            Assert.Equal("nas.local", host.HostName);
        }

        [Fact]
        public void NonEmptyFields_DoOverwrite()
        {
            var host = Existing();
            host.MergeFrom(new HostInfo
            {
                IpAddress  = "192.168.1.10",
                HostName   = "nas2.local",
                MacAddress = "11:22:33:44:55:66",
                Vendor     = "QNAP",
                ResponseTime = 9
            });

            Assert.Equal("nas2.local", host.HostName);
            Assert.Equal("11:22:33:44:55:66", host.MacAddress);
            Assert.Equal("QNAP", host.Vendor);
            Assert.Equal(9, host.ResponseTime);
        }

        // ── IsReachable må ikke sænkes af et sent sweep-svar ────────────────

        [Fact]
        public void NonAuthoritativeMerge_CannotMarkReachableHostOffline()
        {
            // Berigelsessvar ankommer ud af rækkefølge og måler ikke selv
            // tilgængelighed. Et sådant svar må ikke slukke en host der svarede.
            var host = Existing();
            host.MergeFrom(new HostInfo { IpAddress = "192.168.1.10", IsReachable = false });

            Assert.True(host.IsReachable);
        }

        [Fact]
        public void AuthoritativeMerge_CanMarkHostOffline()
        {
            var host = Existing();
            host.MergeFrom(
                new HostInfo { IpAddress = "192.168.1.10", IsReachable = false, Status = "Timeout" },
                authoritative: true);

            Assert.False(host.IsReachable);
            Assert.Equal("Timeout", host.Status);
        }

        [Fact]
        public void NonAuthoritativeMerge_CanStillPromoteToReachable()
        {
            var host = new HostInfo { IpAddress = "192.168.1.10", IsReachable = false };
            host.MergeFrom(new HostInfo { IpAddress = "192.168.1.10", IsReachable = true });

            Assert.True(host.IsReachable);
        }

        // ── Porte ───────────────────────────────────────────────────────────

        [Fact]
        public void NonAuthoritativeMerge_AccumulatesOpenPorts()
        {
            // To faser tjekker samme host; den anden må ikke nulstille den førstes fund.
            var host = new HostInfo { IpAddress = "192.168.1.10", IsPort80Open = true };
            host.MergeFrom(new HostInfo { IpAddress = "192.168.1.10", IsPort443Open = true });

            Assert.True(host.IsPort80Open);
            Assert.True(host.IsPort443Open);
        }

        [Fact]
        public void AuthoritativeMerge_ReplacesPortState()
        {
            // Et gen-ping tjekker alle fire porte, så en port der nu er lukket
            // skal også vises som lukket.
            var host = new HostInfo { IpAddress = "192.168.1.10", IsPort80Open = true, IsPort443Open = true };
            host.MergeFrom(
                new HostInfo { IpAddress = "192.168.1.10", IsPort443Open = true },
                authoritative: true);

            Assert.False(host.IsPort80Open);
            Assert.True(host.IsPort443Open);
        }

        // ── Selvfletning ────────────────────────────────────────────────────

        [Fact]
        public void MergingWithItself_IsANoOp()
        {
            // Under et sweep er den indkomne host ofte den SAMME instans som den
            // bundne række; fletningen må ikke nulstille noget i det tilfælde.
            var host = Existing();
            host.MergeFrom(host);

            Assert.Equal("nas.local", host.HostName);
            Assert.True(host.IsReachable);
            Assert.Equal("Synology", host.Vendor);
        }

        // ── DisplayName-fallback ────────────────────────────────────────────

        [Fact]
        public void DisplayName_PrefersDnsThenNetBiosThenIp()
        {
            var host = new HostInfo { IpAddress = "192.168.1.10" };
            Assert.Equal("192.168.1.10", host.DisplayName);

            host.NetBiosName = "WORKSTATION";
            Assert.Equal("WORKSTATION", host.DisplayName);

            host.HostName = "ws.local";
            Assert.Equal("ws.local", host.DisplayName);
        }

        [Fact]
        public void DisplayName_IgnoresHostNameThatIsJustTheIp()
        {
            var host = new HostInfo { IpAddress = "192.168.1.10", HostName = "192.168.1.10" };
            host.NetBiosName = "WORKSTATION";

            Assert.Equal("WORKSTATION", host.DisplayName);
        }

        [Fact]
        public void IsAlias_TrueOnlyWhenVendorDiffersFromOui()
        {
            var host = new HostInfo { Vendor = "Google, Inc.", OriginalVendor = "Google, Inc." };
            Assert.False(host.IsAlias);

            host.Vendor = "Michael POCO F3";
            Assert.True(host.IsAlias);
        }
    }
}
