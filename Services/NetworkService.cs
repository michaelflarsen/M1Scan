using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Diagnostics;
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
        Task<List<HostInfo>> ScanNetworkAsync(string subnet, int startIp, int endIp, string adapterName = "", CancellationToken ct = default);
        Task<string> GetArpInfoAsync(string ipAddress);
        Task<Dictionary<string, string>> GetArpTableAsync();
        Dictionary<string, string> GetArpTableNative();
        Task<bool> CheckPortAsync(string ip, int port, int timeoutMs = 1000, CancellationToken ct = default);
        Task<string> GetNetBiosNameAsync(string ipAddress, CancellationToken ct = default);
        Task<string> GetMacAddressAsync(string ipAddress, CancellationToken ct = default);
        Task FloodArpAsync(string subnet, int startIp, int endIp, CancellationToken ct = default);
    }

    public class NetworkService : INetworkService
    {
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
                        if (SendARP(destIp, 0, macAddr, ref macAddrLen) == 0)
                            return string.Join(":", macAddr.Take((int)macAddrLen).Select(b => b.ToString("X2")));
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
                        IsConnected = intf.OperationalStatus == OperationalStatus.Up
                    };

                    var ipprops = intf.GetIPProperties();
                    var ipAddresses = ipprops.UnicastAddresses
                        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                        .Select(a => a.Address.ToString())
                        .ToArray();

                    adapter.IpAddresses = ipAddresses;
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

        private static bool TryGetDhcpEnabled(NetworkInterface intf)
        {
            try { return intf.GetIPProperties().GetIPv4Properties().IsDhcpEnabled; }
            catch { return false; }
        }

        private static async Task<string> GetHostNameWithTimeoutAsync(string ip,
            int timeoutMs = 2000, CancellationToken ct = default)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            try
            {
                var entry = await Dns.GetHostEntryAsync(ip, AddressFamily.Unspecified, cts.Token);
                return entry.HostName;
            }
            catch { return ip; }
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

        public async Task<List<HostInfo>> ScanNetworkAsync(string subnet, int startIp, int endIp,
                                                            string adapterName = "",
                                                            CancellationToken ct = default)
        {
            // Start ARP flood concurrently with ping sweep
            var floodTask = FloodArpAsync(subnet, startIp, endIp, ct);

            var tasks = new List<Task<HostInfo>>();
            for (int i = startIp; i <= endIp; i++)
            {
                var ip = $"{subnet}.{i}";
                tasks.Add(PingHostAsync(ip, adapterName, ct));
            }

            var hostInfos = await Task.WhenAll(tasks);
            await floodTask;

            var results = hostInfos.Where(h => h.IsReachable).ToList();

            // Instant native ARP table read — no subprocess
            var arpTable = ReadArpCacheNative();
            foreach (var host in results)
            {
                if (arpTable.TryGetValue(host.IpAddress, out var mac))
                {
                    host.MacAddress = mac;
                    host.Vendor = OuiLookup.Lookup(mac);
                }
            }

            var scannedIps = new HashSet<string>(hostInfos.Select(h => h.IpAddress));
            foreach (var arpEntry in arpTable)
            {
                if (!scannedIps.Contains(arpEntry.Key) && arpEntry.Key.StartsWith(subnet + "."))
                {
                    var arpHost = new HostInfo
                    {
                        IpAddress = arpEntry.Key,
                        HostName = arpEntry.Key,
                        MacAddress = arpEntry.Value,
                        Vendor = OuiLookup.Lookup(arpEntry.Value),
                        IsReachable = false,
                        Status = "ARP-only",
                        LastSeen = DateTime.Now
                    };
                    arpHost.HostName = await GetHostNameWithTimeoutAsync(arpEntry.Key, 2000, ct);
                    results.Add(arpHost);
                }
            }

            var netbiosTasks = results.Select(async host =>
            {
                host.NetBiosName = await GetNetBiosNameAsync(host.IpAddress, ct);
            });
            await Task.WhenAll(netbiosTasks);

            return results;
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
                    udp.Client.ReceiveTimeout = 500;
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

        public async Task<string> GetArpInfoAsync(string ipAddress)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "arp",
                            Arguments = $"-a {ipAddress}",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            CreateNoWindow = true
                        }
                    };
                    process.Start();
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    return output;
                }
                catch (Exception ex)
                {
                    return $"Error: {ex.Message}";
                }
            });
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
