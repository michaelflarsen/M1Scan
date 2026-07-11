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
        Task<Dictionary<string, string>> GetArpTableAsync();
        Dictionary<string, string> GetArpTableNative();
        Task<bool> CheckPortAsync(string ip, int port, int timeoutMs = 1000, CancellationToken ct = default);
        Task<string> GetNetBiosNameAsync(string ipAddress, CancellationToken ct = default);
        Task<string> GetMacAddressAsync(string ipAddress, CancellationToken ct = default);
        Task FloodArpAsync(string subnet, int startIp, int endIp, CancellationToken ct = default);
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

        // Try to get MAC from Windows netsh as fallback when native lookup fails
        private static string GetMacFromNetshFallback(string ipAddress)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("netsh", $"arp show interface")
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
                        var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var part in parts)
                        {
                            if (part.Length == 17 && part.Count(c => c == '-') == 5)
                                return part.Replace('-', ':');
                        }
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

                    result[ip] = string.Join(":", macBytes.Select(b => b.ToString("X2")));
                }
            }
            finally { FreeMibTable(ptr); }
            return result;
        }

        public Dictionary<string, string> GetArpTableNative() => ReadArpCacheNative();

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

        public async Task<HostInfo> PingHostAsync(string hostOrIp, string adapterName = "",
                                                   CancellationToken ct = default)
        {
            var hostInfo = new HostInfo
            {
                HostName = hostOrIp,
                AdapterName = adapterName
            };

            try
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
                var tcpResults = await Task.WhenAll(tcpPorts.Select(p => CheckPortAsync(hostOrIp, p, 300, ct)));
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

        public async Task<string> GetNetBiosNameAsync(string ipAddress,
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

                    using var udp = new UdpClient();
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

        public async Task<bool> CheckPortAsync(string ip, int port,
                                                int timeoutMs = 1000,
                                                CancellationToken ct = default)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            using var client = new TcpClient();
            try
            {
                await client.ConnectAsync(ip, port, cts.Token);
                return client.Connected;
            }
            catch { return false; }
        }
    }
}
