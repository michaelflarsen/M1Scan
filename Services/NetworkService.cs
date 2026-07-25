using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using M1Scan.Models;
using M1Scan.Utils;

namespace M1Scan.Services
{
    /// <summary>
    /// Resultatet af et ICMP-sweep. Error adskiller "sweep'et kunne ikke køre" fra
    /// "ingen svarede" — to tilstande der før var identiske (en tom dictionary), så
    /// et blokeret raw-socket blev rapporteret til brugeren som en succesfuld
    /// scanning med nul fund.
    /// </summary>
    public readonly record struct SweepResult(
        Dictionary<string, (long rttMs, int ttl)> Hosts,
        string? Error)
    {
        public bool Failed => Error is not null;

        public static SweepResult Ok(Dictionary<string, (long rttMs, int ttl)> hosts) => new(hosts, null);

        public static SweepResult Fail(string error, Dictionary<string, (long rttMs, int ttl)>? partial = null) =>
            new(partial ?? new Dictionary<string, (long, int)>(), error);
    }

    public interface INetworkService
    {
        Task<List<NetworkAdapter>> GetNetworkAdaptersAsync();
        Task<HostInfo> PingHostAsync(string hostOrIp, string adapterName = "", CancellationToken ct = default);
        Task<HostInfo> PingHostAsync(string hostOrIp, string adapterName, string srcIp, CancellationToken ct = default);
        // Sweeps all IPs on one bound raw socket. Returns responsive IP -> (rttMs, ttl),
        // plus an Error when the sweep itself couldn't run (no admin, AV blocking raw
        // sockets, adapter removed) — callers MUST surface that instead of reporting
        // an empty result as a completed scan.
        Task<SweepResult> PingSweepBoundAsync(
            IEnumerable<string> ips, string srcIp, int timeoutMs, CancellationToken ct = default);
        Task<Dictionary<string, string>> GetArpTableAsync();
        Dictionary<string, string> GetArpTableNative();
        Task<bool> CheckPortAsync(string ip, int port, int timeoutMs = 1000, CancellationToken ct = default);
        Task<bool> CheckPortAsync(string ip, int port, string srcIp, int timeoutMs = 1000, CancellationToken ct = default);
        Task<string> GetNetBiosNameAsync(string ipAddress, CancellationToken ct = default);
        Task<string> GetNetBiosNameAsync(string ipAddress, string srcIp, CancellationToken ct = default);
        Task<string> GetMacAddressAsync(string ipAddress, CancellationToken ct = default);
        Task<string> ResolveHostNameAsync(string ip, int timeoutMs = 2000, CancellationToken ct = default);
        // Spørger enheden selv om dens navn via mDNS. Foretrækkes over reverse-DNS,
        // se ResolveMdnsNameAsync.
        Task<string> ResolveMdnsNameAsync(string ip, string srcIp, int timeoutMs = 1000, CancellationToken ct = default);
        Task FloodArpAsync(string subnet, int startIp, int endIp, CancellationToken ct = default);
        Task<string> SendArpRequestAsync(string ip, CancellationToken ct = default);
        Task<string> SendArpRequestAsync(string ip, string srcIp, CancellationToken ct = default);
    }

    public class NetworkService : INetworkService
    {
        // ConcurrentDictionary, IKKE Dictionary: cachen læses og skrives af op til 150
        // samtidige opslag under et sweep. Samtidige writes til en almindelig Dictionary
        // korrupterer bucket-kæden, hvilket typisk viser sig som et permanent 100 %
        // CPU-spin i FindEntry — ikke som en exception.
        private readonly ConcurrentDictionary<string, string> _dnsCache = new(StringComparer.OrdinalIgnoreCase);

        // Loft på cachen. En scanning af et /24 giver ~254 entries, så grænsen rammes
        // først efter mange scanninger; TryRemove i stedet for Clear() undgår at rive
        // hele cachen væk under igangværende opslag.
        private const int DnsCacheMaxEntries = 10_000;

        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        private static extern int SendARP(int destIp, int srcIp, byte[] macAddr, ref uint macAddrLen);

        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        private static extern uint GetIpNetTable2(ushort Family, out IntPtr Table);

        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        private static extern void FreeMibTable(IntPtr Memory);

        // MIB_IPNET_TABLE2: [NumEntries(4)][pad(4)][MIB_IPNET_ROW2 × NumEntries]
        // MIB_IPNET_ROW2 offsets (total 88 bytes):
        //   [0]  SOCKADDR_INET Address (28 bytes): [0-1]=si_family, [4-7]=IPv4 addr
        //   [28] ULONG InterfaceIndex
        //   [32] ULONGLONG InterfaceLuid
        //   [40] UCHAR PhysicalAddress[32]
        //   [72] ULONG PhysicalAddressLength
        //   [76] DWORD State  (6 = NlnsPermanent = self/broadcast, skip)
        //
        // Offsets og størrelse er hårdkodede fordi rækken læses felt-for-felt med
        // Marshal frem for en [StructLayout]-struct. 88 bytes gælder x64 og ARM64.
        // Ændrer layoutet sig (eller bygges der for en anden arkitektur), læser vi
        // vilkårlig hukommelse som IP'er og MAC'er — derfor en sanity-check nedenfor
        // i stedet for at stole blindt på tallet.
        private const int RowSize = 88;
        private const int TableHeaderSize = 8;

        // Øvre grænse for hvor mange rækker vi accepterer at tabellen påstår at have.
        // NumEntries læses fra unmanaged hukommelse; en urimelig værdi (korrupt eller
        // uventet layout) ville ellers sende løkken langt uden for tabellen.
        private const int MaxArpRows = 100_000;

        // Try to get MAC from Windows netsh arp table as fallback
        private static string GetMacFromNetshFallback(string ipAddress)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("netsh", $"arp show table")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                using var process = System.Diagnostics.Process.Start(psi);
                if (process == null) return string.Empty;
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(2000);

                var lines = output.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    if (line.Contains(ipAddress, StringComparison.OrdinalIgnoreCase))
                    {
                        // Look for MAC address pattern (xx-xx-xx-xx-xx-xx)
                        var macMatches = System.Text.RegularExpressions.Regex.Matches(line, @"[0-9a-f]{2}-[0-9a-f]{2}-[0-9a-f]{2}-[0-9a-f]{2}-[0-9a-f]{2}-[0-9a-f]{2}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (macMatches.Count > 0)
                            return macMatches[0].Value.Replace('-', ':');
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GetMacFromNetshFallback failed: {ex.Message}");
            }
            return string.Empty;
        }

        // Reads the Windows ARP cache via native GetIpNetTable2 — instant, no subprocess.
        private static Dictionary<string, string> ReadArpCacheNative()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            uint status = GetIpNetTable2(2 /* AF_INET */, out IntPtr ptr);

            // Returnerer API'et en fejl MEN alligevel en tabel, skal den stadig frigives.
            // Den gamle kombinerede betingelse returnerede før try/finally og lækkede
            // dermed tabellen i netop det tilfælde.
            if (status != 0)
            {
                if (ptr != IntPtr.Zero) FreeMibTable(ptr);
                return result;
            }
            if (ptr == IntPtr.Zero) return result;

            try
            {
                int count = Marshal.ReadInt32(ptr, 0);

                // Urimeligt antal = vi læser ikke det layout vi tror. Stop hellere med
                // et tomt resultat (kalderen falder tilbage til SendARP/netsh) end at
                // parse tilfældig hukommelse som netværksdata.
                if (count < 0 || count > MaxArpRows)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"ReadArpCacheNative: urimeligt NumEntries ({count}) — springer over.");
                    return result;
                }

                for (int i = 0; i < count; i++)
                {
                    IntPtr row = ptr + TableHeaderSize + i * RowSize;
                    ushort family = (ushort)Marshal.ReadInt16(row, 0);
                    if (family != 2) continue;

                    byte[] addrBytes = {
                        Marshal.ReadByte(row, 4), Marshal.ReadByte(row, 5),
                        Marshal.ReadByte(row, 6), Marshal.ReadByte(row, 7)
                    };
                    string ip = new IPAddress(addrBytes).ToString();

                    uint macLen = (uint)Marshal.ReadInt32(row, 72);
                    if (macLen == 0 || macLen > 6) continue;

                    uint state = (uint)Marshal.ReadInt32(row, 76);
                    if (state == 6) continue; // NlnsPermanent = self/broadcast

                    var macBytes = new byte[macLen];
                    for (int b = 0; b < (int)macLen; b++)
                        macBytes[b] = Marshal.ReadByte(row, 40 + b);

                    // Skip null-MAC (00:00:00:00:00:00) — an unresolved/incomplete entry.
                    if (macBytes.All(b => b == 0)) continue;

                    result[ip] = string.Join(":", macBytes.Select(b => b.ToString("X2")));
                }
            }
            finally { FreeMibTable(ptr); }
            return result;
        }

        public Dictionary<string, string> GetArpTableNative() => ReadArpCacheNative();

        // Sends a blocking ARP request to a single IP and returns the resolved MAC.
        // SendARP blocks until the device replies or the OS times out, and writes the
        // MAC directly into the buffer — this is more reliable than reading the ARP
        // cache afterward, which may not yet be populated for slow-responding devices.
        // Retries once, because slow IoT devices (Shelly, etc.) often miss the first ARP.
        //
        // srcIp forces the request out of a specific local interface. This is essential
        // when a VPN (Tailscale, etc.) hijacks the route to a LAN IP: without it the OS
        // sends the ARP through the tunnel where it can't resolve, and the device shows
        // no MAC even though it's on the same physical segment.
        public async Task<string> SendArpRequestAsync(string ip, CancellationToken ct = default)
            => await SendArpRequestAsync(ip, string.Empty, ct);

        public async Task<string> SendArpRequestAsync(string ip, string srcIp,
                                                      CancellationToken ct = default)
        {
            int dest, src = 0;
            try
            {
                var bytes = IPAddress.Parse(ip).GetAddressBytes();
                dest = BitConverter.ToInt32(bytes, 0);
                if (!string.IsNullOrEmpty(srcIp) && IPAddress.TryParse(srcIp, out var srcAddr))
                    src = BitConverter.ToInt32(srcAddr.GetAddressBytes(), 0);
            }
            catch { return string.Empty; }

            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (ct.IsCancellationRequested) return string.Empty;

                var mac = await Task.Run(() =>
                {
                    try
                    {
                        byte[] macBuf = new byte[6]; uint len = 6;
                        if (SendARP(dest, src, macBuf, ref len) == 0 && len > 0)
                            return string.Join(":", macBuf.Take((int)len).Select(b => b.ToString("X2")));
                    }
                    catch { }
                    return string.Empty;
                }, ct);

                if (!string.IsNullOrEmpty(mac)) return mac;
                if (attempt == 0) await Task.Delay(120, ct).ConfigureAwait(false);
            }
            return string.Empty;
        }

        // Antal dedikerede tråde til ARP-floodet. SendARP er et BLOKERENDE P/Invoke —
        // tidligere blev der lavet én Task.Run pr. IP (op til 254) med en semafor på
        // 64, hvilket parkerede op til 64 trådpulje-tråde i kernekald. Samtidig vil
        // sweep, DNS-opslag og porttjek hver have 16-150 samtidige operationer, og
        // trådpuljen indsætter kun ~1 ny tråd pr. sekund ud over sit minimum. Resultatet
        // var trådsult: scanningen ventede på tråde, ikke på netværket.
        //
        // Otte dedikerede LongRunning-tråde tager arbejdet uden at røre puljen.
        private const int ArpFloodWorkers = 8;

        // Sends ARP requests to all IPs in range to seed the OS ARP cache.
        // Runs concurrently with the ping sweep; capped at 1500ms total.
        public async Task FloodArpAsync(string subnet, int startIp, int endIp,
                                         CancellationToken ct = default)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(1500);
            var token = timeoutCts.Token;

            var queue = new ConcurrentQueue<int>(Enumerable.Range(startIp, endIp - startIp + 1));

            var workers = Enumerable.Range(0, ArpFloodWorkers).Select(_ =>
                Task.Factory.StartNew(() =>
                {
                    while (!token.IsCancellationRequested && queue.TryDequeue(out int i))
                    {
                        try
                        {
                            var bytes = IPAddress.Parse($"{subnet}.{i}").GetAddressBytes();
                            int dest = BitConverter.ToInt32(bytes, 0);
                            byte[] mac = new byte[6]; uint len = 6;
                            SendARP(dest, 0, mac, ref len);
                        }
                        catch { /* enkelt IP der ikke kan ARP'es stopper ikke floodet */ }
                    }
                },
                CancellationToken.None,           // workeren tjekker selv token'et og afslutter pænt
                TaskCreationOptions.LongRunning,  // egen tråd, ikke en pulje-tråd
                TaskScheduler.Default)).ToArray();

            // Workerne afslutter selv ved timeout, så der er intet at "afbryde" —
            // await'en her venter blot på at de otte tråde er løbet tør eller udløbet.
            try { await Task.WhenAll(workers).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }

        public async Task<string> GetMacAddressAsync(string ipAddress,
                                                      CancellationToken ct = default)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(1500);
            try
            {
                return await Task.Run(() =>
                {
                    try
                    {
                        var bytes = IPAddress.Parse(ipAddress).GetAddressBytes();
                        int destIp = BitConverter.ToInt32(bytes, 0);
                        byte[] macAddr = new byte[6];
                        uint macAddrLen = (uint)macAddr.Length;
                        if (SendARP(destIp, 0, macAddr, ref macAddrLen) == 0 && macAddrLen > 0)
                            return string.Join(":", macAddr.Take((int)macAddrLen).Select(b => b.ToString("X2")));

                        // Fallback: try native ARP cache first
                        var nativeArp = ReadArpCacheNative();
                        if (nativeArp.TryGetValue(ipAddress, out var mac) && !string.IsNullOrEmpty(mac))
                            return mac;

                        // Last resort: netsh fallback for stubborn devices like Shelly
                        return GetMacFromNetshFallback(ipAddress);
                    }
                    catch { }
                    return string.Empty;
                }, cts.Token);
            }
            catch (OperationCanceledException) { return string.Empty; }
        }

        public async Task<List<NetworkAdapter>> GetNetworkAdaptersAsync()
        {
            return await Task.Run(() =>
            {
                var adapters = new List<NetworkAdapter>();
                var networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();

                foreach (var intf in networkInterfaces)
                {
                    if (intf.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;

                    var adapter = new NetworkAdapter
                    {
                        Name = intf.Name,
                        Description = intf.Description,
                        MacAddress = intf.GetPhysicalAddress().ToString(),
                        IsDhcpEnabled = TryGetDhcpEnabled(intf),
                        Status = intf.OperationalStatus == OperationalStatus.Up ? "Tilsluttet" : "Inaktiv",
                        IsConnected = intf.OperationalStatus == OperationalStatus.Up,
                        SpeedBitsPerSec = TryGetSpeed(intf)
                    };

                    var ipprops = intf.GetIPProperties();
                    var unicastInfos = ipprops.UnicastAddresses
                        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                        .ToList();

                    adapter.IpAddresses = unicastInfos.Select(a => a.Address.ToString()).ToArray();

                    var firstUnicast = unicastInfos.FirstOrDefault();
                    if (firstUnicast != null)
                    {
                        try { adapter.SubnetMask = firstUnicast.IPv4Mask?.ToString(); }
                        catch { /* some tunnel/VPN adapters throw on IPv4Mask */ }
                    }
                    adapter.DnsServers = ipprops.DnsAddresses
                        .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                        .Select(a => a.ToString())
                        .ToArray();

                    if (ipprops.GatewayAddresses.Count > 0)
                        adapter.Gateway = ipprops.GatewayAddresses[0].Address.ToString();

                    adapters.Add(adapter);
                }

                return adapters;
            });
        }

        private static long TryGetSpeed(NetworkInterface intf)
        {
            try { return intf.Speed; }
            catch { return -1; }
        }

        private static bool TryGetDhcpEnabled(NetworkInterface intf)
        {
            try { return intf.GetIPProperties().GetIPv4Properties().IsDhcpEnabled; }
            catch { return false; }
        }

        // Public wrapper for reverse-DNS/mDNS hostname resolution (used by the fast sweep path).
        public Task<string> ResolveHostNameAsync(string ip, int timeoutMs = 2000,
                                                 CancellationToken ct = default)
            => GetHostNameWithTimeoutAsync(ip, timeoutMs, ct);

        // ── Reverse mDNS ─────────────────────────────────────────────────────
        //
        // Spørger ENHEDEN SELV hvad den hedder, i stedet for at spørge netværket.
        //
        // Hvorfor det er nødvendigt: Dns.GetHostEntryAsync sender både en almindelig
        // PTR-forespørgsel til DNS-serveren OG et mDNS/LLMNR-opslag, og returnerer det
        // svar der kommer først. Det er et kapløb, ikke et valg — på et testnetværk gav
        // 192.168.5.4 skiftevis "seer.dk" (routerens PTR-record, som peger på en tjeneste
        // der kører på maskinen) og "TechnoBunker.local" (maskinens eget annoncerede navn),
        // alt efter hvor travlt DNS-serveren havde. Samme host, to navne, afhængigt af
        // belastning.
        //
        // Enhedens eget navn er den mere pålidelige kilde: en PTR-record sættes af en
        // administrator én gang og bliver forældet, mens mDNS-navnet kommer fra maskinen
        // i det øjeblik vi spørger. Derfor slås mDNS op eksplicit og foretrækkes.
        //
        // Protokol: DNS-PTR-forespørgsel på <omvendte oktetter>.in-addr.arpa sendt til
        // multicast 224.0.0.251:5353 med QU-bit sat, så svaret kommer unicast tilbage til
        // vores egen port (ellers skulle vi dele port 5353 med Windows' egen mDNS-service).
        public async Task<string> ResolveMdnsNameAsync(string ip, string srcIp,
                                                        int timeoutMs = 1000,
                                                        CancellationToken ct = default)
        {
            if (!IPAddress.TryParse(ip, out var target) ||
                target.AddressFamily != AddressFamily.InterNetwork)
                return string.Empty;

            return await Task.Run(() =>
            {
                if (ct.IsCancellationRequested) return string.Empty;
                try
                {
                    var query = BuildReversePtrQuery(target);

                    UdpClient udp;
                    if (!string.IsNullOrEmpty(srcIp) && IPAddress.TryParse(srcIp, out var srcAddr))
                    {
                        try { udp = new UdpClient(new IPEndPoint(srcAddr, 0)); }
                        catch { udp = new UdpClient(); }
                    }
                    else udp = new UdpClient();

                    using var _udp = udp;
                    udp.Client.ReceiveTimeout = timeoutMs;
                    udp.Send(query, query.Length, new IPEndPoint(MdnsGroup, MdnsPort));

                    // Læs indtil vi ser et brugbart svar eller løber tør for tid.
                    // Andre enheder kan sende multicast-trafik til vores port imens.
                    var deadline = Environment.TickCount64 + timeoutMs;
                    while (Environment.TickCount64 < deadline && !ct.IsCancellationRequested)
                    {
                        var remote = new IPEndPoint(IPAddress.Any, 0);
                        byte[] response;
                        try { response = udp.Receive(ref remote); }
                        catch (SocketException) { break; } // timeout

                        // Kun svar fra selve målet tæller — ellers kunne en anden enhed
                        // på segmentet navngive en host den ikke ejer.
                        if (!remote.Address.Equals(target)) continue;

                        var name = ParsePtrAnswer(response);
                        if (!string.IsNullOrEmpty(name)) return name;
                    }
                }
                catch { /* mDNS er en bonus-kilde; fejl falder tilbage til reverse-DNS */ }
                return string.Empty;
            }, ct);
        }

        private static readonly IPAddress MdnsGroup = IPAddress.Parse("224.0.0.251");
        private const int MdnsPort = 5353;

        /// <summary>Bygger en DNS-PTR-forespørgsel for "&lt;d.c.b.a&gt;.in-addr.arpa".</summary>
        private static byte[] BuildReversePtrQuery(IPAddress target)
        {
            var o = target.GetAddressBytes();
            var qname = $"{o[3]}.{o[2]}.{o[1]}.{o[0]}.in-addr.arpa";

            var packet = new List<byte>(64)
            {
                0x00, 0x00,             // ID = 0 (mDNS bruger ikke transaktions-ID)
                0x00, 0x00,             // Flags: standard query
                0x00, 0x01,             // QDCOUNT = 1
                0x00, 0x00,             // ANCOUNT
                0x00, 0x00,             // NSCOUNT
                0x00, 0x00              // ARCOUNT
            };

            foreach (var label in qname.Split('.'))
            {
                packet.Add((byte)label.Length);
                packet.AddRange(Encoding.ASCII.GetBytes(label));
            }
            packet.Add(0x00);           // rod-label afslutter QNAME

            packet.Add(0x00); packet.Add(0x0C);   // QTYPE = PTR (12)
            packet.Add(0x80); packet.Add(0x01);   // QCLASS = IN, QU-bit → bed om unicast svar

            return packet.ToArray();
        }

        /// <summary>
        /// Læser navnet ud af første PTR-svar i en DNS-besked. Returnerer tom streng
        /// hvis beskeden ikke indeholder et brugbart svar.
        /// </summary>
        internal static string ParsePtrAnswer(byte[] msg)
        {
            const int HeaderSize = 12;
            if (msg.Length < HeaderSize) return string.Empty;

            int qdCount = (msg[4] << 8) | msg[5];
            int anCount = (msg[6] << 8) | msg[7];
            if (anCount == 0) return string.Empty;

            int pos = HeaderSize;

            // Spring spørgsmålene over: QNAME + QTYPE(2) + QCLASS(2)
            for (int i = 0; i < qdCount; i++)
            {
                if (!SkipName(msg, ref pos)) return string.Empty;
                pos += 4;
                if (pos > msg.Length) return string.Empty;
            }

            for (int i = 0; i < anCount; i++)
            {
                if (!SkipName(msg, ref pos)) return string.Empty;
                if (pos + 10 > msg.Length) return string.Empty;

                int type = (msg[pos] << 8) | msg[pos + 1];
                int rdLength = (msg[pos + 8] << 8) | msg[pos + 9];
                pos += 10;

                if (pos + rdLength > msg.Length) return string.Empty;

                if (type == 12) // PTR
                {
                    int namePos = pos;
                    var name = ReadName(msg, ref namePos, 0);
                    if (!string.IsNullOrEmpty(name)) return name;
                }

                pos += rdLength;
            }

            return string.Empty;
        }

        /// <summary>Springer et (evt. komprimeret) DNS-navn over.</summary>
        private static bool SkipName(byte[] msg, ref int pos)
        {
            while (true)
            {
                if (pos >= msg.Length) return false;
                int len = msg[pos];

                if (len == 0) { pos++; return true; }

                // 0xC0-præfiks = komprimeringspointer; navnet slutter her (2 bytes).
                if ((len & 0xC0) == 0xC0)
                {
                    pos += 2;
                    return pos <= msg.Length;
                }

                pos += 1 + len;
            }
        }

        /// <summary>
        /// Læser et DNS-navn, inkl. komprimeringspointere. depth begrænser hvor mange
        /// pointere vi følger — en besked kan pege på sig selv og give en uendelig løkke.
        /// </summary>
        private static string ReadName(byte[] msg, ref int pos, int depth)
        {
            if (depth > 10) return string.Empty;

            var labels = new List<string>();
            while (true)
            {
                if (pos >= msg.Length) return string.Empty;
                int len = msg[pos];

                if (len == 0) { pos++; break; }

                if ((len & 0xC0) == 0xC0)
                {
                    if (pos + 1 >= msg.Length) return string.Empty;
                    int pointer = ((len & 0x3F) << 8) | msg[pos + 1];
                    pos += 2;

                    int inner = pointer;
                    var rest = ReadName(msg, ref inner, depth + 1);
                    if (!string.IsNullOrEmpty(rest)) labels.Add(rest);
                    break;
                }

                if (pos + 1 + len > msg.Length) return string.Empty;
                labels.Add(Encoding.UTF8.GetString(msg, pos + 1, len));
                pos += 1 + len;
            }

            return string.Join('.', labels);
        }

        private async Task<string> GetHostNameWithTimeoutAsync(string ip,
            int timeoutMs = 2000, CancellationToken ct = default)
        {
            if (_dnsCache.TryGetValue(ip, out var cached))
                return cached;

            // Hold cachen bounded uden at smide igangværende opslag væk: fjern
            // enkelte ældre nøgler i stedet for at Clear()'e hele tabellen.
            if (_dnsCache.Count > DnsCacheMaxEntries)
            {
                foreach (var key in _dnsCache.Keys.Take(_dnsCache.Count - DnsCacheMaxEntries + 1))
                    _dnsCache.TryRemove(key, out _);
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            try
            {
                var lookup = Dns.GetHostEntryAsync(ip, AddressFamily.Unspecified, cts.Token);

                // Token'et afbryder ikke selve resolver-kaldet — det opgiver blot at
                // vente på det. Den forladte opgave fejler så bagefter (typisk
                // "værten kendes ikke") uden at nogen ser fejlen, og .NET rejser
                // UnobservedTaskException fra finalizer-tråden. Med 254 opslag pr. scan
                // fyldte det crash-loggen med forventede DNS-fejl.
                // Denne continuation markerer fejlen som observeret.
                _ = lookup.ContinueWith(
                    static t => { _ = t.Exception; },
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

                var entry = await lookup;
                var hostname = entry.HostName;
                _dnsCache[ip] = hostname;
                return hostname;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Kalderen annullerede — cache IKKE et negativt resultat, ellers
                // husker vi "intet navn" for en host vi aldrig fik spurgt om.
                throw;
            }
            catch
            {
                // Cache IKKE fejl. Et mislykket opslag betyder ikke at værten er
                // navnløs — med 800 ms timeout og 150 samtidige opslag er et
                // forbigående timeout almindeligt, og et negativt cache-hit ville
                // fastlåse "intet navn" for resten af processens levetid, også ved
                // "Tilføj scan" og efterfølgende scanninger.
                return ip;
            }
        }

        // Sweeps an entire IP range with ICMP echo over ONE shared raw socket bound to srcIp.
        // A single socket sends all requests (each tagged with a sequence number that encodes
        // the destination) and one receive loop matches replies back — far cheaper than one
        // socket per host, and avoids cross-talk where one socket steals another's reply.
        // Returns a dictionary of responsive IP -> (rttMs, ttl). Requires admin (app has it).
        // Kun ét sweep ad gangen: en rå ICMP-socket bundet til en adresse modtager ALLE
        // ICMP-svar til den adresse, også svar der hører til et andet, samtidigt sweep.
        // Et fremmed svar bliver kasseret (ikke lagt tilbage), så den rette ejer taber
        // sit svar. Kommentaren her stod tidligere som om én delt socket løste det —
        // det gør den ikke; serialisering gør.
        private static readonly SemaphoreSlim _sweepGate = new(1, 1);

        // Sweep-id: skal være unikt pr. sweep. Tidligere brugtes
        // Environment.CurrentManagedThreadId, men thread-pool-id'er genbruges, så to
        // sweeps kunne ende med samme id og krydsmatche hinandens svar.
        private static int _sweepIdCounter = Random.Shared.Next(ushort.MaxValue);

        private static SweepResult IcmpSweepBound(
            IEnumerable<string> ips, string srcIp, int timeoutMs, CancellationToken ct)
        {
            var result = new Dictionary<string, (long rttMs, int ttl)>();
            if (!IPAddress.TryParse(srcIp, out var srcAddr))
                return SweepResult.Fail($"Ugyldig kilde-IP: '{srcIp}'");

            // Map sequence number -> destination IP + send timestamp, so replies can be matched.
            var pending = new Dictionary<ushort, (string ip, long sentTicks)>();
            var ipList = ips.Where(x => IPAddress.TryParse(x, out _)).ToList();
            if (ipList.Count == 0) return SweepResult.Ok(result);

            // seq er 16 bit — flere end 65535 mål ville wrappe og overskrive pending-
            // entries, så svar blev matchet til den forkerte host.
            if (ipList.Count > ushort.MaxValue)
                return SweepResult.Fail($"For mange mål i ét sweep ({ipList.Count}); maks. {ushort.MaxValue}.");

            _sweepGate.Wait(ct);
            Socket? sock = null;
            try
            {
                sock = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Icmp);
                sock.Bind(new IPEndPoint(srcAddr, 0));

                ushort id = (ushort)(Interlocked.Increment(ref _sweepIdCounter) & 0xFFFF);
                var sw = System.Diagnostics.Stopwatch.StartNew();

                // ---- Send phase: fire an echo request for every IP ----
                ushort seq = 0;
                int sendFailures = 0;
                Exception? lastSendError = null;
                foreach (var ip in ipList)
                {
                    if (ct.IsCancellationRequested) break;
                    var destAddr = IPAddress.Parse(ip);
                    byte[] packet = new byte[8];
                    packet[0] = 8; // echo request
                    packet[4] = (byte)(id >> 8); packet[5] = (byte)(id & 0xFF);
                    packet[6] = (byte)(seq >> 8); packet[7] = (byte)(seq & 0xFF);
                    ushort checksum = IcmpChecksum(packet);
                    packet[2] = (byte)(checksum >> 8); packet[3] = (byte)(checksum & 0xFF);

                    pending[seq] = (ip, sw.ElapsedTicks);
                    try { sock.SendTo(packet, new IPEndPoint(destAddr, 0)); }
                    catch (Exception ex)
                    {
                        sendFailures++;
                        lastSendError = ex;
                        System.Diagnostics.Debug.WriteLine($"ICMP send to {ip} failed: {ex.Message}");
                    }
                    seq++;
                }

                // Kunne INTET sendes, er det en reel fejl — ikke et tomt netværk.
                if (sendFailures == ipList.Count)
                    return SweepResult.Fail(
                        $"Ingen ICMP-pakker kunne sendes ({lastSendError?.Message}). " +
                        "Tjek firewall/antivirus og at adapteren er aktiv.");

                // ---- Receive phase: collect replies until timeout or all matched ----
                // Deadline måles fra HER, ikke fra før send-fasen. Med en fælles
                // budget-klokke fik de sidste IP'er i listen kun nogle få ms tilbage,
                // så høje adresser systematisk faldt igennem fase 1.
                var recvSw = System.Diagnostics.Stopwatch.StartNew();
                var buf = new byte[1024];
                while (pending.Count > result.Count && !ct.IsCancellationRequested)
                {
                    long remainingMs = timeoutMs - recvSw.ElapsedMilliseconds;
                    if (remainingMs <= 0) break;
                    sock.ReceiveTimeout = (int)Math.Min(remainingMs, timeoutMs);

                    int received;
                    EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    try { received = sock.ReceiveFrom(buf, ref remote); }
                    catch (SocketException) { break; } // timeout — no more replies

                    if (received < 20) continue;
                    int ihl = (buf[0] & 0x0F) * 4;
                    int ttl = buf[8];
                    if (received < ihl + 8 || buf[ihl] != 0 /* echo reply */) continue;

                    ushort rId = (ushort)((buf[ihl + 4] << 8) | buf[ihl + 5]);
                    ushort rSeq = (ushort)((buf[ihl + 6] << 8) | buf[ihl + 7]);
                    if (rId != id || !pending.TryGetValue(rSeq, out var info)) continue;

                    var rttMs = (sw.ElapsedTicks - info.sentTicks) * 1000 / System.Diagnostics.Stopwatch.Frequency;
                    result[info.ip] = (rttMs, ttl);
                }
                return SweepResult.Ok(result);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AccessDenied)
            {
                return SweepResult.Fail(
                    "Raw-socket blev nægtet. Kør M1Scan som administrator, og tjek om " +
                    "antivirus/firewall blokerer rå ICMP.", result);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return SweepResult.Fail($"Ping-sweep fejlede: {ex.Message}", result);
            }
            finally
            {
                sock?.Dispose();
                _sweepGate.Release();
            }
        }

        private static ushort IcmpChecksum(byte[] data)
        {
            uint sum = 0;
            for (int i = 0; i + 1 < data.Length; i += 2)
                sum += (uint)((data[i] << 8) + data[i + 1]);
            if (data.Length % 2 == 1)
                sum += (uint)(data[^1] << 8);
            while ((sum >> 16) != 0) sum = (sum & 0xFFFF) + (sum >> 16);
            return (ushort)~sum;
        }

        public Task<HostInfo> PingHostAsync(string hostOrIp, string adapterName = "",
                                            CancellationToken ct = default)
            => PingHostAsync(hostOrIp, adapterName, string.Empty, ct);

        public Task<SweepResult> PingSweepBoundAsync(
            IEnumerable<string> ips, string srcIp, int timeoutMs, CancellationToken ct = default)
            => Task.Run(() => IcmpSweepBound(ips, srcIp, timeoutMs, ct), ct);

        public async Task<HostInfo> PingHostAsync(string hostOrIp, string adapterName,
                                                  string srcIp, CancellationToken ct = default)
        {
            var hostInfo = new HostInfo
            {
                HostName = hostOrIp,
                AdapterName = adapterName
            };

            try
            {
                // When a source IP is given, use a bound raw-socket ping so ICMP leaves the
                // chosen adapter (not a VPN route). Otherwise use the simpler managed Ping.
                if (!string.IsNullOrEmpty(srcIp))
                {
                    var sweep = await Task.Run(
                        () => IcmpSweepBound(new[] { hostOrIp }, srcIp, 600, ct), ct);

                    // Kunne sweep'et slet ikke køre, er "ingen svar" ikke en konklusion —
                    // sig hvorfor, så TCP-fallbacket nedenfor ikke maskerer årsagen.
                    if (sweep.Failed)
                        hostInfo.Status = sweep.Error!;

                    bool ok = sweep.Hosts.TryGetValue(hostOrIp, out var r);
                    long rtt = ok ? r.rttMs : 0;
                    int ttl = ok ? r.ttl : 0;
                    if (ok)
                    {
                        hostInfo.IsReachable = true;
                        hostInfo.ResponseTime = (int)rtt;
                        hostInfo.Status = "Online";
                        hostInfo.IpAddress = hostOrIp;
                        hostInfo.OsGuess = ttl switch
                        {
                            > 0 and <= 64   => "Linux / Mac",
                            > 64 and <= 128 => "Windows",
                            > 128           => "Netværksenhed",
                            _               => string.Empty
                        };
                        if (IPAddress.TryParse(hostOrIp, out _))
                            hostInfo.HostName = await GetHostNameWithTimeoutAsync(hostOrIp, 2000, ct);
                    }
                    else
                    {
                        hostInfo.IsReachable = false;
                        hostInfo.Status = "Timeout";
                    }
                }
                else
                {
                    using var ping = new Ping();
                    // Single attempt 600ms — generous for LAN; TCP fallback covers filtered hosts
                    var reply = await ping.SendPingAsync(hostOrIp, 600);

                    if (reply.Status == IPStatus.Success)
                    {
                        hostInfo.IsReachable = true;
                        hostInfo.ResponseTime = (int)reply.RoundtripTime;
                        hostInfo.Status = "Online";
                        hostInfo.IpAddress = reply.Address.ToString();

                        int ttl = reply.Options?.Ttl ?? 0;
                        hostInfo.OsGuess = ttl switch
                        {
                            > 0 and <= 64  => "Linux / Mac",
                            > 64 and <= 128 => "Windows",
                            > 128           => "Netværksenhed",
                            _               => string.Empty
                        };

                        if (IPAddress.TryParse(hostOrIp, out _))
                            hostInfo.HostName = await GetHostNameWithTimeoutAsync(hostOrIp, 2000, ct);
                    }
                    else
                    {
                        hostInfo.IsReachable = false;
                        hostInfo.Status = reply.Status.ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                hostInfo.IsReachable = false;
                hostInfo.Status = $"Error: {ex.Message}";
            }

            // TCP fallback — opdager hosts der blokerer ICMP (routere, firewalls, IoT)
            if (!hostInfo.IsReachable)
            {
                if (ct.IsCancellationRequested) { hostInfo.LastSeen = DateTime.Now; return hostInfo; }
                var tcpPorts = new[] { 80, 443, 22, 445 };
                var tcpResults = await Task.WhenAll(tcpPorts.Select(p => CheckPortAsync(hostOrIp, p, srcIp, 300, ct)));
                var firstOpen = tcpPorts.Zip(tcpResults).FirstOrDefault(x => x.Second);
                if (firstOpen.Second)
                {
                    hostInfo.IsReachable = true;
                    hostInfo.IpAddress = hostOrIp;
                    hostInfo.Status = $"Online (TCP:{firstOpen.First})";
                    if (IPAddress.TryParse(hostOrIp, out _))
                        hostInfo.HostName = await GetHostNameWithTimeoutAsync(hostOrIp, 2000, ct);
                }
            }

            hostInfo.LastSeen = DateTime.Now;
            return hostInfo;
        }

        public Task<string> GetNetBiosNameAsync(string ipAddress, CancellationToken ct = default)
            => GetNetBiosNameAsync(ipAddress, string.Empty, ct);

        // Binds the UDP socket to srcIp so the NetBIOS query leaves the chosen adapter.
        public async Task<string> GetNetBiosNameAsync(string ipAddress, string srcIp,
                                                       CancellationToken ct = default)
        {
            return await Task.Run(() =>
            {
                if (ct.IsCancellationRequested) return string.Empty;
                try
                {
                    byte[] request = {
                        0x00, 0x00, 0x00, 0x10, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                        0x20, 0x43, 0x4b, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41,
                        0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41,
                        0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x00,
                        0x00, 0x21, 0x00, 0x01
                    };

                    UdpClient udp;
                    if (!string.IsNullOrEmpty(srcIp) && IPAddress.TryParse(srcIp, out var srcAddr))
                    {
                        try { udp = new UdpClient(new IPEndPoint(srcAddr, 0)); }
                        catch { udp = new UdpClient(); }
                    }
                    else
                    {
                        udp = new UdpClient();
                    }
                    using var _udp = udp;
                    udp.Client.ReceiveTimeout = 200;
                    udp.Send(request, request.Length, ipAddress, 137);

                    var ep = new IPEndPoint(IPAddress.Any, 0);
                    var response = udp.Receive(ref ep);

                    if (response.Length > 57)
                    {
                        int nameCount = response[56];
                        for (int i = 0; i < nameCount && 57 + i * 18 + 15 < response.Length; i++)
                        {
                            int offset = 57 + i * 18;
                            byte suffix = response[offset + 15];
                            byte flags = response[offset + 16];
                            if (suffix == 0x00 && (flags & 0x80) == 0)
                            {
                                var name = Encoding.ASCII.GetString(response, offset, 15).TrimEnd();
                                if (!string.IsNullOrWhiteSpace(name))
                                    return name;
                            }
                        }
                    }
                }
                catch { }
                return string.Empty;
            }, ct);
        }

        public async Task<Dictionary<string, string>> GetArpTableAsync()
        {
            return await Task.Run(ReadArpCacheNative);
        }

        public Task<bool> CheckPortAsync(string ip, int port, int timeoutMs = 1000,
                                         CancellationToken ct = default)
            => CheckPortAsync(ip, port, string.Empty, timeoutMs, ct);

        // Binds the outgoing socket to srcIp so the connection leaves the chosen adapter,
        // preventing a VPN/tunnel from hijacking the route to a LAN destination.
        public async Task<bool> CheckPortAsync(string ip, int port, string srcIp,
                                                int timeoutMs = 1000,
                                                CancellationToken ct = default)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            TcpClient client;
            if (!string.IsNullOrEmpty(srcIp) && IPAddress.TryParse(srcIp, out var srcAddr))
            {
                try { client = new TcpClient(new IPEndPoint(srcAddr, 0)); }
                catch { client = new TcpClient(); } // src IP not bindable (e.g. adapter gone) — fall back
            }
            else
            {
                client = new TcpClient();
            }

            try
            {
                await client.ConnectAsync(ip, port, cts.Token);
                return client.Connected;
            }
            catch { return false; }
            finally { client.Dispose(); }
        }
    }
}
