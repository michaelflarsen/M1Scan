using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using M1Scan.Models;

namespace M1Scan.Services
{
    public interface IDiagnosticsService
    {
        Task<List<DnsTimingResult>> MeasureDnsServersAsync(IEnumerable<(string Server, string Label)> servers, CancellationToken ct = default);
        DhcpLeaseInfo? GetDhcpLease(string adapterName);
        Task<Ipv6Status> CheckIpv6Async(CancellationToken ct = default);
        Task<CaptivePortalStatus> CheckCaptivePortalAsync(CancellationToken ct = default);
        Task<SpeedTestResult> RunSpeedTestAsync(IProgress<SpeedTestProgress> progress, CancellationToken ct = default);
    }

    public class DiagnosticsService : IDiagnosticsService
    {
        private const string DnsProbeName   = "www.msftconnecttest.com";
        private const string NcsiUrl        = "http://www.msftconnecttest.com/connecttest.txt";
        private const string NcsiExpected   = "Microsoft Connect Test";
        private const string SpeedTestHost  = "speed.cloudflare.com";
        private const long   DownloadBytes  = 25_000_000;
        private const long   UploadBytes    = 5_000_000;

        private static readonly HttpClient _portalClient = new(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        private static readonly HttpClient _speedClient = new()
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        // ── DNS-svartider (rå UDP A-query — System.Net.Dns kan ikke målrette en server) ──

        public async Task<List<DnsTimingResult>> MeasureDnsServersAsync(
            IEnumerable<(string Server, string Label)> servers, CancellationToken ct = default)
        {
            // SEKVENTIELT, ikke parallelt. Målingerne deler samme uplink: kørte de
            // samtidigt, konkurrerede de om båndbredden og skævvred hinanden — vi målte
            // altså delvist vores egen trængsel i stedet for serverens svartid. Og
            // resultatet er ikke kun kosmetisk: den hurtigste værdi går videre til
            // HealthScore. Et par serier på nogle få hundrede ms hver er rigelig
            // hurtigt til et dashboard der opdateres på interval.
            var results = new List<DnsTimingResult>();

            foreach (var s in servers)
            {
                ct.ThrowIfCancellationRequested();

                // Warm-up query (ARP/route cold-start skævvrider første måling)
                await QueryDnsOnceAsync(s.Server, ct);

                // Tag den bedste af to målinger: en enkelt måling rammer let et
                // tilfældigt hik og får en fin server til at se langsom ud.
                var first  = await QueryDnsOnceAsync(s.Server, ct);
                var second = await QueryDnsOnceAsync(s.Server, ct);

                double? best = (first, second) switch
                {
                    (null, null) => null,
                    (null, var b) => b,
                    (var a, null) => a,
                    var (a, b) => Math.Min(a!.Value, b!.Value)
                };

                results.Add(new DnsTimingResult { Server = s.Server, Label = s.Label, ResponseMs = best });
            }

            return results;
        }

        private static async Task<double?> QueryDnsOnceAsync(string server, CancellationToken ct)
        {
            try
            {
                if (!IPAddress.TryParse(server, out var serverIp)) return null;

                var txId  = (ushort)Random.Shared.Next(ushort.MaxValue);
                var query = BuildDnsQuery(txId, DnsProbeName);

                using var udp = new UdpClient(serverIp.AddressFamily);
                udp.Connect(serverIp, 53);

                var sw = Stopwatch.StartNew();
                await udp.SendAsync(query, ct);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(2000);

                while (true)
                {
                    var result = await udp.ReceiveAsync(timeoutCts.Token);
                    if (result.Buffer.Length >= 2 &&
                        result.Buffer[0] == (byte)(txId >> 8) &&
                        result.Buffer[1] == (byte)(txId & 0xFF))
                    {
                        sw.Stop();
                        return sw.Elapsed.TotalMilliseconds;
                    }
                }
            }
            catch
            {
                return null; // tidsudløb eller netværksfejl
            }
        }

        private static byte[] BuildDnsQuery(ushort txId, string name)
        {
            var labels = name.Split('.');
            var packet = new List<byte>(12 + name.Length + 6)
            {
                (byte)(txId >> 8), (byte)(txId & 0xFF), // TXID
                0x01, 0x00,                             // Flags: RD=1
                0x00, 0x01,                             // QDCOUNT=1
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00      // AN/NS/AR=0
            };
            foreach (var label in labels)
            {
                packet.Add((byte)label.Length);
                packet.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
            }
            packet.Add(0x00);             // QNAME terminator
            packet.AddRange(new byte[] { 0x00, 0x01, 0x00, 0x01 }); // QTYPE=A, QCLASS=IN
            return packet.ToArray();
        }

        // ── DHCP-lease via registry (ipconfig-parsing er locale-afhængig, WMI kræver NuGet) ──

        public DhcpLeaseInfo? GetDhcpLease(string adapterName)
        {
            try
            {
                var intf = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => n.Name == adapterName);
                if (intf == null) return null;

                var props  = intf.GetIPProperties();
                var v4     = props.GetIPv4Properties();
                bool isDhcp = v4 != null && v4.IsDhcpEnabled;

                if (!isDhcp)
                    return new DhcpLeaseInfo { IsDhcp = false };

                string server = props.DhcpServerAddresses
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString() ?? "";

                DateTimeOffset? obtained = null, expires = null;
                using var key = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{intf.Id}");
                if (key != null)
                {
                    if (key.GetValue("LeaseObtainedTime") is int ot && ot > 0)
                        obtained = DateTimeOffset.FromUnixTimeSeconds(ot).ToLocalTime();
                    if (key.GetValue("LeaseTerminatesTime") is int tt && tt > 0)
                        expires = DateTimeOffset.FromUnixTimeSeconds(tt).ToLocalTime();
                    if (string.IsNullOrEmpty(server) && key.GetValue("DhcpServer") is string ds)
                        server = ds;
                }

                return new DhcpLeaseInfo { IsDhcp = true, Server = server, Obtained = obtained, Expires = expires };
            }
            catch
            {
                return null;
            }
        }

        // ── IPv6: global unicast til stede OG ping mod Cloudflare IPv6 ──

        public async Task<Ipv6Status> CheckIpv6Async(CancellationToken ct = default)
        {
            try
            {
                bool hasGlobalV6 = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Any(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6
                              && !a.Address.IsIPv6LinkLocal
                              && !a.Address.IsIPv6SiteLocal
                              && !IPAddress.IsLoopback(a.Address));

                if (!hasGlobalV6) return Ipv6Status.NotAvailable;

                using var ping = new Ping();
                // ct blev tidligere ignoreret her: en annulleret diagnostik hang stadig
                // op til 2 s på dette ping. SendPingAsync-overloaden med token afbryder.
                var reply = await ping.SendPingAsync(
                    IPAddress.Parse("2606:4700:4700::1111"), TimeSpan.FromMilliseconds(2000), cancellationToken: ct);
                return reply.Status == IPStatus.Success ? Ipv6Status.Connected : Ipv6Status.NotAvailable;
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                return Ipv6Status.NotAvailable;
            }
        }

        // ── Captive portal: samme probe som Windows NCSI ──

        public async Task<CaptivePortalStatus> CheckCaptivePortalAsync(CancellationToken ct = default)
        {
            try
            {
                using var resp = await _portalClient.GetAsync(NcsiUrl, ct);
                if ((int)resp.StatusCode is >= 300 and < 400)
                    return CaptivePortalStatus.PortalDetected;
                if (!resp.IsSuccessStatusCode)
                    return CaptivePortalStatus.NoResponse;
                var body = await resp.Content.ReadAsStringAsync(ct);
                return body.Trim() == NcsiExpected
                    ? CaptivePortalStatus.None
                    : CaptivePortalStatus.PortalDetected;
            }
            catch
            {
                return CaptivePortalStatus.NoResponse;
            }
        }

        // ── Speedtest mod Cloudflare ──

        public async Task<SpeedTestResult> RunSpeedTestAsync(IProgress<SpeedTestProgress> progress, CancellationToken ct = default)
        {
            double downloadMbps = await RunDownloadAsync(progress, ct);
            double uploadMbps   = await RunUploadAsync(progress, ct);

            return new SpeedTestResult
            {
                timestamp    = DateTimeOffset.Now,
                downloadMbps = downloadMbps,
                uploadMbps   = uploadMbps,
                bytesDown    = DownloadBytes,
                server       = SpeedTestHost
            };
        }

        private static async Task<double> RunDownloadAsync(IProgress<SpeedTestProgress> progress, CancellationToken ct)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
            var token = timeoutCts.Token;

            using var resp = await _speedClient.GetAsync(
                $"https://{SpeedTestHost}/__down?bytes={DownloadBytes}",
                HttpCompletionOption.ResponseHeadersRead, token);
            resp.EnsureSuccessStatusCode();

            await using var stream = await resp.Content.ReadAsStreamAsync(token);
            var buffer = new byte[65536];
            long total = 0;
            var sw = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;

            int read;
            while ((read = await stream.ReadAsync(buffer, token)) > 0)
            {
                total += read;
                if (sw.Elapsed - lastReport > TimeSpan.FromMilliseconds(200))
                {
                    lastReport = sw.Elapsed;
                    progress.Report(new SpeedTestProgress
                    {
                        Phase       = SpeedTestPhase.Download,
                        BytesDone   = total,
                        TotalBytes  = DownloadBytes,
                        CurrentMbps = total * 8 / 1e6 / sw.Elapsed.TotalSeconds
                    });
                }
            }
            sw.Stop();
            return total * 8 / 1e6 / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
        }

        private static async Task<double> RunUploadAsync(IProgress<SpeedTestProgress> progress, CancellationToken ct)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
            var token = timeoutCts.Token;

            progress.Report(new SpeedTestProgress
            {
                Phase      = SpeedTestPhase.Upload,
                BytesDone  = 0,
                TotalBytes = UploadBytes
            });

            var data = new byte[UploadBytes];
            Random.Shared.NextBytes(data);

            // v1: hele POST'en times — header-overhead er ubetydeligt ved 5 MB (→ "estimat")
            var sw = Stopwatch.StartNew();
            using var content = new ByteArrayContent(data);
            using var resp = await _speedClient.PostAsync($"https://{SpeedTestHost}/__up", content, token);
            sw.Stop();
            resp.EnsureSuccessStatusCode();

            progress.Report(new SpeedTestProgress
            {
                Phase      = SpeedTestPhase.Upload,
                BytesDone  = UploadBytes,
                TotalBytes = UploadBytes
            });

            return UploadBytes * 8 / 1e6 / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
        }
    }
}
