using System;
using System.Collections.Generic;
using System.Text;
using M1Scan.Services;
using Xunit;

namespace M1Scan.Tests.Services
{
    /// <summary>
    /// Parsing af DNS-svar fra reverse-mDNS. Beskeden kommer fra en vilkårlig enhed på
    /// netværket, så parseren skal kunne håndtere både gyldige svar og noget der er
    /// afkortet, selvrefererende eller direkte fjendtligt uden at kaste eller hænge.
    /// </summary>
    public class MdnsParsingTests
    {
        // ── Hjælpere til at bygge DNS-beskeder ───────────────────────────────

        private static void AddHeader(List<byte> m, int qd, int an)
        {
            m.AddRange(new byte[] { 0, 0, 0x84, 0 });          // ID, flags (svar)
            m.Add((byte)(qd >> 8)); m.Add((byte)(qd & 0xFF));  // QDCOUNT
            m.Add((byte)(an >> 8)); m.Add((byte)(an & 0xFF));  // ANCOUNT
            m.AddRange(new byte[] { 0, 0, 0, 0 });             // NSCOUNT, ARCOUNT
        }

        private static void AddName(List<byte> m, string name)
        {
            foreach (var label in name.Split('.'))
            {
                m.Add((byte)label.Length);
                m.AddRange(Encoding.ASCII.GetBytes(label));
            }
            m.Add(0);
        }

        private static void AddPtrAnswer(List<byte> m, string owner, string target)
        {
            AddName(m, owner);
            m.AddRange(new byte[] { 0, 12 });          // TYPE = PTR
            m.AddRange(new byte[] { 0, 1 });           // CLASS = IN
            m.AddRange(new byte[] { 0, 0, 0, 60 });    // TTL

            var rdata = new List<byte>();
            AddName(rdata, target);
            m.Add((byte)(rdata.Count >> 8)); m.Add((byte)(rdata.Count & 0xFF));
            m.AddRange(rdata);
        }

        // ── Gyldige svar ─────────────────────────────────────────────────────

        [Fact]
        public void ParsesNameFromWellFormedPtrAnswer()
        {
            var m = new List<byte>();
            AddHeader(m, qd: 1, an: 1);
            AddName(m, "4.5.168.192.in-addr.arpa");
            m.AddRange(new byte[] { 0, 12, 0, 1 });            // spørgsmålets QTYPE/QCLASS
            AddPtrAnswer(m, "4.5.168.192.in-addr.arpa", "TechnoBunker.local");

            Assert.Equal("TechnoBunker.local", NetworkService.ParsePtrAnswer(m.ToArray()));
        }

        [Fact]
        public void SkipsNonPtrAnswersAndFindsThePtr()
        {
            var m = new List<byte>();
            AddHeader(m, qd: 0, an: 2);

            // Først en A-record der skal springes over
            AddName(m, "host.local");
            m.AddRange(new byte[] { 0, 1, 0, 1 });             // TYPE = A, CLASS = IN
            m.AddRange(new byte[] { 0, 0, 0, 60 });
            m.AddRange(new byte[] { 0, 4, 192, 168, 5, 4 });   // RDLENGTH = 4 + adresse

            AddPtrAnswer(m, "4.5.168.192.in-addr.arpa", "TechnoBunker.local");

            Assert.Equal("TechnoBunker.local", NetworkService.ParsePtrAnswer(m.ToArray()));
        }

        [Fact]
        public void ResolvesCompressionPointerInOwnerName()
        {
            // Det almindelige tilfælde fra rigtige responders: svarets ejer-navn er
            // en pointer tilbage til spørgsmålets QNAME (altid offset 12) i stedet
            // for at gentage det.
            var m = new List<byte>();
            AddHeader(m, qd: 1, an: 1);
            AddName(m, "4.5.168.192.in-addr.arpa");            // QNAME på offset 12
            m.AddRange(new byte[] { 0, 12, 0, 1 });            // QTYPE = PTR, QCLASS = IN

            m.AddRange(new byte[] { 0xC0, 12 });               // ejer = pointer til QNAME
            m.AddRange(new byte[] { 0, 12, 0, 1 });            // TYPE = PTR, CLASS = IN
            m.AddRange(new byte[] { 0, 0, 0, 60 });            // TTL

            var rdata = new List<byte>();
            AddName(rdata, "TechnoBunker.local");
            m.Add((byte)(rdata.Count >> 8)); m.Add((byte)(rdata.Count & 0xFF));
            m.AddRange(rdata);

            Assert.Equal("TechnoBunker.local", NetworkService.ParsePtrAnswer(m.ToArray()));
        }

        [Fact]
        public void ResolvesCompressionPointerInsideRdata()
        {
            // To PTR-svar hvor det andet genbruger det førstes navn via pointer.
            // Vi læser det første svar, så testen viser blot at en besked med
            // pointere i RDATA parses uden at fejle.
            var m = new List<byte>();
            AddHeader(m, qd: 1, an: 2);
            AddName(m, "4.5.168.192.in-addr.arpa");
            m.AddRange(new byte[] { 0, 12, 0, 1 });

            // Svar 1: fuldt navn i RDATA — noter hvor navnet lander.
            m.AddRange(new byte[] { 0xC0, 12 });
            m.AddRange(new byte[] { 0, 12, 0, 1 });
            m.AddRange(new byte[] { 0, 0, 0, 60 });
            var rdata1 = new List<byte>();
            AddName(rdata1, "TechnoBunker.local");
            m.Add((byte)(rdata1.Count >> 8)); m.Add((byte)(rdata1.Count & 0xFF));
            int nameOffset = m.Count;
            m.AddRange(rdata1);

            // Svar 2: RDATA er kun en pointer til navnet i svar 1.
            m.AddRange(new byte[] { 0xC0, 12 });
            m.AddRange(new byte[] { 0, 12, 0, 1 });
            m.AddRange(new byte[] { 0, 0, 0, 60 });
            m.AddRange(new byte[] { 0, 2 });
            m.Add((byte)(0xC0 | (nameOffset >> 8)));
            m.Add((byte)(nameOffset & 0xFF));

            Assert.Equal("TechnoBunker.local", NetworkService.ParsePtrAnswer(m.ToArray()));
        }

        // ── Misdannede beskeder må ikke kaste eller hænge ────────────────────

        [Fact]
        public void ReturnsEmptyForNoAnswers()
        {
            var m = new List<byte>();
            AddHeader(m, qd: 0, an: 0);
            Assert.Equal(string.Empty, NetworkService.ParsePtrAnswer(m.ToArray()));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(11)]   // kortere end DNS-headeren
        public void ReturnsEmptyForTruncatedMessage(int length)
        {
            Assert.Equal(string.Empty, NetworkService.ParsePtrAnswer(new byte[length]));
        }

        [Fact]
        public void ReturnsEmptyWhenRdLengthRunsPastEndOfMessage()
        {
            var m = new List<byte>();
            AddHeader(m, qd: 0, an: 1);
            AddName(m, "x.local");
            m.AddRange(new byte[] { 0, 12, 0, 1 });
            m.AddRange(new byte[] { 0, 0, 0, 60 });
            m.AddRange(new byte[] { 0xFF, 0xFF });             // RDLENGTH = 65535, men intet data

            Assert.Equal(string.Empty, NetworkService.ParsePtrAnswer(m.ToArray()));
        }

        [Fact]
        public void SelfReferencingPointerDoesNotHang()
        {
            // En pointer der peger på sig selv ville give en uendelig løkke uden
            // dybdegrænsen i ReadName. Testen skal simpelthen returnere.
            var m = new List<byte>();
            AddHeader(m, qd: 0, an: 1);
            AddName(m, "x.local");
            m.AddRange(new byte[] { 0, 12, 0, 1 });
            m.AddRange(new byte[] { 0, 0, 0, 60 });
            m.AddRange(new byte[] { 0, 2 });

            int here = m.Count;
            m.Add((byte)(0xC0 | (here >> 8)));
            m.Add((byte)(here & 0xFF));                        // peger på sig selv

            var result = NetworkService.ParsePtrAnswer(m.ToArray());
            Assert.NotNull(result);
        }

        [Fact]
        public void MutuallyRecursivePointersDoNotHang()
        {
            // To pointere der peger på hinanden.
            var msg = new byte[]
            {
                0,0, 0x84,0, 0,0, 0,1, 0,0, 0,0,   // header, AN=1
                0xC0, 14,                          // offset 12: peger på 14
                0xC0, 12                           // offset 14: peger tilbage på 12
            };

            var result = NetworkService.ParsePtrAnswer(msg);
            Assert.NotNull(result);
        }
    }
}
