using System;
using System.Threading.Tasks;
using M1Scan.Models;
using M1Scan.Services;
using Xunit;

namespace M1Scan.Tests.Services
{
    /// <summary>
    /// Brugerens egne enhedsnavne. Navnet gemmes på MAC frem for IP, netop fordi
    /// DHCP flytter rundt på IP-adresser — testene her fastholder den egenskab.
    /// </summary>
    public class DeviceNameServiceTests
    {
        [Theory]
        // Samme MAC skrevet på de formater brugerfladen og de forskellige
        // opslagskilder leverer, skal ramme samme nøgle.
        [InlineData("AA:BB:CC:DD:EE:FF")]
        [InlineData("aa:bb:cc:dd:ee:ff")]
        [InlineData("AA-BB-CC-DD-EE-FF")]
        [InlineData("aabbccddeeff")]
        [InlineData("AABB.CCDD.EEFF")]
        public void Normalize_MapsAllCommonMacFormatsToSameKey(string mac)
        {
            Assert.Equal("AABBCCDDEEFF", DeviceNameService.Normalize(mac));
        }

        [Fact]
        public void Normalize_HandlesNullAndEmpty()
        {
            Assert.Equal(string.Empty, DeviceNameService.Normalize(null!));
            Assert.Equal(string.Empty, DeviceNameService.Normalize(""));
        }

        [Fact]
        public async Task LookupIsCaseAndSeparatorInsensitive()
        {
            var svc = new DeviceNameService();
            var mac = RandomMac();

            await svc.SetAsync(mac, "TechnoBunker");
            try
            {
                Assert.Equal("TechnoBunker", svc.Lookup(mac));
                Assert.Equal("TechnoBunker", svc.Lookup(mac.ToLowerInvariant()));
                Assert.Equal("TechnoBunker", svc.Lookup(mac.Replace(":", "-")));
                Assert.Equal("TechnoBunker", svc.Lookup(mac.Replace(":", "")));
            }
            finally { await svc.SetAsync(mac, null); }
        }

        [Fact]
        public async Task EmptyNameRemovesTheOverride()
        {
            var svc = new DeviceNameService();
            var mac = RandomMac();

            await svc.SetAsync(mac, "Midlertidigt");
            Assert.NotNull(svc.Lookup(mac));

            // Tomt navn = "brug det automatiske opslag igen".
            await svc.SetAsync(mac, "   ");
            Assert.Null(svc.Lookup(mac));
        }

        [Fact]
        public async Task NameIsTrimmed()
        {
            var svc = new DeviceNameService();
            var mac = RandomMac();

            await svc.SetAsync(mac, "  TechnoBunker  ");
            try { Assert.Equal("TechnoBunker", svc.Lookup(mac)); }
            finally { await svc.SetAsync(mac, null); }
        }

        [Fact]
        public async Task PartialMacIsRejected()
        {
            // Kun en fuld MAC (12 hex-tegn) identificerer én enhed. Et OUI-præfiks
            // ville ramme alle enheder fra samme fabrikant.
            var svc = new DeviceNameService();

            await svc.SetAsync("AABBCC", "Skal ikke gemmes");

            Assert.Null(svc.Lookup("AABBCC"));
        }

        [Fact]
        public async Task NonHexTwelveCharacterStringIsRejected()
        {
            // 12 tegn men ikke hex — et rent længdetjek ville acceptere dette,
            // men det er ikke en gyldig MAC.
            var svc = new DeviceNameService();

            await svc.SetAsync("GGGGGGGGGGGG", "Skal ikke gemmes");

            Assert.Null(svc.Lookup("GGGGGGGGGGGG"));
        }

        [Fact]
        public void LookupOfUnknownMacReturnsNull()
        {
            Assert.Null(new DeviceNameService().Lookup(RandomMac()));
        }

        // Tilfældig MAC så testene ikke træder på hinandens data i den delte
        // %APPDATA%-fil, og ikke ødelægger rigtige navne på udviklerens maskine.
        private static string RandomMac()
        {
            var b = new byte[6];
            Random.Shared.NextBytes(b);
            return string.Join(":", Array.ConvertAll(b, x => x.ToString("X2")));
        }
    }

    /// <summary>Navnerækkefølgen i Scan-tabellen.</summary>
    public class DisplayNamePrecedenceTests
    {
        [Fact]
        public void CustomNameBeatsEveryAutomaticSource()
        {
            var host = new HostInfo
            {
                IpAddress   = "192.168.5.4",
                HostName    = "seer.dk",          // routerens PTR-record
                NetBiosName = "TECHNOBUNKER"
            };
            Assert.Equal("seer.dk", host.DisplayName);

            host.CustomName = "TechnoBunker";
            Assert.Equal("TechnoBunker", host.DisplayName);
            Assert.True(host.HasCustomName);
        }

        [Fact]
        public void ClearingCustomNameFallsBackToAutomaticLookup()
        {
            var host = new HostInfo
            {
                IpAddress = "192.168.5.4",
                HostName  = "seer.dk",
                CustomName = "TechnoBunker"
            };
            Assert.Equal("TechnoBunker", host.DisplayName);

            host.CustomName = string.Empty;
            Assert.Equal("seer.dk", host.DisplayName);
            Assert.False(host.HasCustomName);
        }

        [Fact]
        public void WhitespaceOnlyCustomNameIsIgnored()
        {
            var host = new HostInfo { IpAddress = "192.168.5.4", HostName = "seer.dk", CustomName = "   " };
            Assert.Equal("seer.dk", host.DisplayName);
            Assert.False(host.HasCustomName);
        }

        [Fact]
        public void MergeCarriesCustomNameToTheBoundRow()
        {
            // Under et scan flettes en frisk observation ind i den række der allerede
            // er bundet til griddet; navnet må ikke gå tabt undervejs.
            var bound = new HostInfo { IpAddress = "192.168.5.4" };
            bound.MergeFrom(new HostInfo { IpAddress = "192.168.5.4", CustomName = "TechnoBunker" });

            Assert.Equal("TechnoBunker", bound.DisplayName);
        }

        [Fact]
        public void MergeDoesNotClearAnExistingCustomName()
        {
            var bound = new HostInfo { IpAddress = "192.168.5.4", CustomName = "TechnoBunker" };
            bound.MergeFrom(new HostInfo { IpAddress = "192.168.5.4", HostName = "seer.dk" });

            Assert.Equal("TechnoBunker", bound.DisplayName);
        }
    }
}
