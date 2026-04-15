using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using MMPing.Models;
using MMPing.Utils;

namespace MMPing.Services
{
    public interface INetworkService
    {
        Task<List<NetworkAdapter>> GetNetworkAdaptersAsync();
        Task<HostInfo> PingHostAsync(string hostOrIp, string adapterName = "");
        Task<List<HostInfo>> ScanNetworkAsync(string subnet, int startIp, int endIp, string adapterName = "");
        Task<string> GetArpInfoAsync(string ipAddress);
        Task<Dictionary<string, string>> GetArpTableAsync();
        Task<bool> CheckPortAsync(string ip, int port, int timeoutMs = 1000);
        Task<string> GetNetBiosNameAsync(string ipAddress);
    }

    public class NetworkService : INetworkService
    {
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

        public async Task<HostInfo> PingHostAsync(string hostOrIp, string adapterName = "")
        {
            var hostInfo = new HostInfo 
            { 
                HostName = hostOrIp,
                AdapterName = adapterName
            };

            try
            {
                using (var ping = new Ping())
                {
                    var reply = await ping.SendPingAsync(hostOrIp, 2000);
                    
                    if (reply.Status == IPStatus.Success)
                    {
                        hostInfo.IsReachable = true;
                        hostInfo.ResponseTime = (int)reply.RoundtripTime;
                        hostInfo.Status = "Online";
                        hostInfo.IpAddress = reply.Address.ToString();

                        // OS-gæt fra TTL
                        int ttl = reply.Options?.Ttl ?? 0;
                        hostInfo.OsGuess = ttl switch
                        {
                            > 0 and <= 64  => "Linux / Mac",
                            > 64 and <= 128 => "Windows",
                            > 128           => "Netværksenhed",
                            _               => string.Empty
                        };

                        // Try to get hostname if IP was provided
                        if (IPAddress.TryParse(hostOrIp, out _))
                        {
                            try
                            {
                                var hostEntry = await Dns.GetHostEntryAsync(hostOrIp);
                                hostInfo.HostName = hostEntry.HostName;
                            }
                            catch { }
                        }
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

            hostInfo.LastSeen = DateTime.Now;
            return hostInfo;
        }

        public async Task<List<HostInfo>> ScanNetworkAsync(string subnet, int startIp, int endIp, string adapterName = "")
        {
            var tasks = new List<Task<HostInfo>>();

            for (int i = startIp; i <= endIp; i++)
            {
                var ip = $"{subnet}.{i}";
                tasks.Add(PingHostAsync(ip, adapterName));
            }

            var hostInfos = await Task.WhenAll(tasks);
            var results = hostInfos.Where(h => h.IsReachable).ToList();

            // Hent MAC-adresser fra ARP-cache og sæt vendor
            var arpTable = await GetArpTableAsync();
            foreach (var host in results)
            {
                if (arpTable.TryGetValue(host.IpAddress, out var mac))
                {
                    host.MacAddress = mac;
                    host.Vendor = OuiLookup.Lookup(mac);
                }
            }

            // Tilføj enheder fra ARP-tabellen der ikke svarede på ping
            var scannedIps = new HashSet<string>(hostInfos.Select(h => h.IpAddress));
            foreach (var arpEntry in arpTable)
            {
                if (!scannedIps.Contains(arpEntry.Key))
                {
                    // Tjek om IP'en er i det scannede subnet
                    if (IPAddress.TryParse(arpEntry.Key, out var ip) &&
                        arpEntry.Key.StartsWith(subnet + "."))
                    {
                        var arpHost = new HostInfo
                        {
                            IpAddress = arpEntry.Key,
                            HostName = arpEntry.Key, // Forsøg at resolve hostname
                            MacAddress = arpEntry.Value,
                            Vendor = OuiLookup.Lookup(arpEntry.Value),
                            IsReachable = false,
                            Status = "ARP-only",
                            LastSeen = DateTime.Now
                        };

                        // Prøv at få hostname
                        try
                        {
                            var hostEntry = await Dns.GetHostEntryAsync(arpEntry.Key);
                            arpHost.HostName = hostEntry.HostName;
                        }
                        catch { }

                        results.Add(arpHost);
                    }
                }
            }

            // NetBIOS-navne parallelt (kort timeout — fejler stille for ikke-Windows)
            var netbiosTasks = results.Select(async host =>
            {
                host.NetBiosName = await GetNetBiosNameAsync(host.IpAddress);
            });
            await Task.WhenAll(netbiosTasks);

            return results;
        }

        public async Task<string> GetNetBiosNameAsync(string ipAddress)
        {
            return await Task.Run(() =>
            {
                try
                {
                    // NetBIOS Name Status Request (UDP port 137)
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

                    // Parse: antal navne er på offset 56, hvert navn er 18 bytes
                    if (response.Length > 57)
                    {
                        int nameCount = response[56];
                        for (int i = 0; i < nameCount && 57 + i * 18 + 15 < response.Length; i++)
                        {
                            int offset = 57 + i * 18;
                            byte suffix = response[offset + 15];
                            byte flags = response[offset + 16];
                            // Workstation name (suffix 0x00, ikke group)
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
            });
        }

        public async Task<Dictionary<string, string>> GetArpTableAsync()
        {
            return await Task.Run(() =>
            {
                var table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "arp",
                            Arguments = "-a",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            CreateNoWindow = true
                        }
                    };
                    process.Start();
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();

                    foreach (var line in output.Split('\n'))
                    {
                        var parts = line.Trim().Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2 && IPAddress.TryParse(parts[0], out _))
                        {
                            var mac = parts[1].Replace('-', ':').ToUpper();
                            if (mac.Length == 17)
                                table[parts[0]] = mac;
                        }
                    }
                }
                catch { }
                return table;
            });
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

        public async Task<bool> CheckPortAsync(string ip, int port, int timeoutMs = 1000)
        {
            try
            {
                using var client = new TcpClient();
                var connectTask = client.ConnectAsync(ip, port);
                var completed = await Task.WhenAny(connectTask, Task.Delay(timeoutMs));
                return completed == connectTask && client.Connected;
            }
            catch { return false; }
        }
    }
}
