using System;
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
    public interface INetworkService
    {
        Task<List<NetworkAdapter>> GetNetworkAdaptersAsync();
        Task<HostInfo> PingHostAsync(string hostOrIp, string adapterName = "", CancellationToken ct = default);
        Task<HostInfo> PingHostAsync(string hostOrIp, string adapterName, string srcIp, CancellationToken ct = default);
        Task<Dictionary<string, string>> GetArpTableAsync();
        Dictionary<string, string> GetArpTableNative();
        Task<bool> CheckPortAsync(string ip, int port, int timeoutMs = 1000, CancellationToken ct = default);
        Task<bool> CheckPortAsync(string ip, int port, string srcIp, int timeoutMs = 1000, CancellationToken ct = default);
        Task<string> GetNetBiosNameAsync(string ipAddress, CancellationToken ct = default);
        Task<string> GetNetBiosNameAsync(string ipAddress, string srcIp, CancellationToken ct = default);
        Task<string> GetMacAddressAsync(string ipAddress, CancellationToken ct = default);
        Task FloodArpAsync(string subnet, int startIp, int endIp, CancellationToken ct = default);
        Task<string> SendArpRequestAsync(string ip, CancellationToken ct = default);
        Task<string> SendArpRequestAsync(string ip, string srcIp, CancellationToken ct = default);
    }

    public class NetworkService : INetworkService
    {
        private readonly Dictionary<string, string> _dnsCache = new(StringComparer.OrdinalIgnoreCase);

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
        private const int RowSize = 88;
        private const int TableHeaderSize = 8;

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
            catch { }
            return string.Empty;
        }

        // Reads the Windows ARP cache via native GetIpNetTable2 — instant, no subprocess.
        private static Dictionary<string, string> ReadArpCacheNative()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (GetIpNetTable2(2 /* AF_INET */, out IntPtr ptr) != 0 || ptr == IntPtr.Zero)
                return result;
            try
            {
                int count = Marshal.ReadInt32(ptr, 0);
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

        // Sends ARP requests to all IPs in range to seed the OS ARP cache.
        // Runs concurrently with the ping sweep; capped at 1500ms total.
        public async Task FloodArpAsync(string subnet, int startIp, int endIp,
                                         CancellationToken ct = default)
        {
            using var sem = new SemaphoreSlim(64);
            var tasks = Enumerable.Range(startIp, endIp - startIp + 1).Select(i =>
                Task.Run(async () =>
                {
                    if (ct.IsCancellationRequested) return;
                    await sem.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        var bytes = IPAddress.Parse($"{subnet}.{i}").GetAddressBytes();
                        int dest = BitConverter.ToInt32(bytes, 0);
                        byte[] mac = new byte[6]; uint len = 6;
                        SendARP(dest, 0, mac, ref len);
                    }
                    catch { }
                    finally { sem.Release(); }
                }, ct)).ToList();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(1500);
            try { await Task.WhenAll(tasks).WaitAsync(timeoutCts.Token); }
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

        private async Task<string> GetHostNameWithTimeoutAsync(string ip,
            int timeoutMs = 2000, CancellationToken ct = default)
        {
            if (_dnsCache.TryGetValue(ip, out var cached))
                return cached;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            try
            {
                var entry = await Dns.GetHostEntryAsync(ip, AddressFamily.Unspecified, cts.Token);
                var hostname = entry.HostName;
                _dnsCache[ip] = hostname;
                return hostname;
            }
            catch
            {
                _dnsCache[ip] = ip;
                return ip;
            }
        }

        // Raw-socket ICMP echo bound to a specific source IP. Returns (success, rtt, ttl).
        // System.Net.NetworkInformation.Ping cannot bind a source interface, so when we
        // need to force ICMP out of a chosen adapter (e.g. avoid a VPN route) we build the
        // ICMP packet manually on a raw socket bound to srcIp. Requires admin (app has it).
        private static (bool ok, long rttMs, int ttl) IcmpPingBound(string ip, string srcIp, int timeoutMs)
        {
            if (!IPAddress.TryParse(ip, out var destAddr) ||
                !IPAddress.TryParse(srcIp, out var srcAddr))
                return (false, 0, 0);

            Socket? sock = null;
            try
            {
                sock = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Icmp);
                sock.Bind(new IPEndPoint(srcAddr, 0));
                sock.ReceiveTimeout = timeoutMs;
                sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveTimeout, timeoutMs);

                // ICMP echo request: type=8, code=0, id, seq, then checksum over the whole packet.
                ushort id = (ushort)Environment.CurrentManagedThreadId;
                byte[] packet = new byte[8];
                packet[0] = 8; // echo request
                packet[1] = 0;
                packet[4] = (byte)(id >> 8); packet[5] = (byte)(id & 0xFF);
                packet[6] = 0; packet[7] = 1; // sequence
                ushort checksum = IcmpChecksum(packet);
                packet[2] = (byte)(checksum >> 8); packet[3] = (byte)(checksum & 0xFF);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                sock.SendTo(packet, new IPEndPoint(destAddr, 0));

                var buf = new byte[1024];
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                int received = sock.ReceiveFrom(buf, ref remote);
                sw.Stop();

                // Reply is a full IP packet: IHL*4 = IP header length, then ICMP.
                if (received >= 20)
                {
                    int ihl = (buf[0] & 0x0F) * 4;
                    int ttl = buf[8];
                    if (received >= ihl + 8 && buf[ihl] == 0 /* echo reply */)
                        return (true, sw.ElapsedMilliseconds, ttl);
                }
                return (false, 0, 0);
            }
            catch { return (false, 0, 0); }
            finally { sock?.Dispose(); }
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
                    var (ok, rtt, ttl) = await Task.Run(() => IcmpPingBound(hostOrIp, srcIp, 600), ct);
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
