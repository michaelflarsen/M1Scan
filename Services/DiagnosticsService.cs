using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Security;
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

        // Én TCP-forbindelse rammer sjældent den reelle båndbredde: TCP slow-start
        // og et enkelt forbindelsesvindue begrænser gennemløbet, særligt på hurtige
        // eller høj-latency-linjer. Rigtige speedtests (Ookla, Cloudflare selv) bruger
        // flere parallelle streams — det gør vi nu også.
        private const int    ParallelStreams   = 6;
        private static readonly TimeSpan WarmupDuration     = TimeSpan.FromSeconds(2);  // slow-start ekskluderes fra målingen
        private static readonly TimeSpan MeasureDuration    = TimeSpan.FromSeconds(8);  // ren målt periode efter warm-up

        private static readonly HttpClient _portalClient = new(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        private static readonly HttpClient _speedClient = new(new SocketsHttpHandler
        {
            MaxConnectionsPerServer = ParallelStreams + 2,
            AllowAutoRedirect = false // undgår dobbelt-optælling af bytes hvis __up nogensinde 3xx-redirectes
        })
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
            var (downloadMbps, bytesDown) = await RunPhaseAsync(SpeedTestPhase.Download, progress, ct);
            var (uploadMbps, _)           = await RunPhaseAsync(SpeedTestPhase.Upload, progress, ct);

            return new SpeedTestResult
            {
                timestamp    = DateTimeOffset.Now,
                downloadMbps = downloadMbps,
                uploadMbps   = uploadMbps,
                bytesDown    = bytesDown,
                server       = SpeedTestHost
            };
        }

        // Kører N parallelle streams i et fast tidsvindue: de første WarmupDuration
        // sekunder ekskluderes fra beregningen (TCP slow-start giver kunstigt lave
        // øjeblikstal der ellers trækker gennemsnittet skævt), og resultatet er
        // gennemsnittet af MeasureDuration sekunders faktisk gennemløb.
        private static async Task<(double Mbps, long Bytes)> RunPhaseAsync(SpeedTestPhase phase, IProgress<SpeedTestProgress> progress, CancellationToken ct)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var totalBudget = WarmupDuration + MeasureDuration + TimeSpan.FromSeconds(10); // slack til opsætning/afvikling
            timeoutCts.CancelAfter(totalBudget);
            var token = timeoutCts.Token;

            long totalBytes = 0;
            long warmupBytesSnapshot = -1;
            var sw = Stopwatch.StartNew();

            void OnBytes(long delta)
            {
                Interlocked.Add(ref totalBytes, delta);
            }

            using var reportTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
            var reportLoop = Task.Run(async () =>
            {
                try
                {
                    while (await reportTimer.WaitForNextTickAsync(token))
                    {
                        long done = Interlocked.Read(ref totalBytes);
                        if (warmupBytesSnapshot < 0 && sw.Elapsed >= WarmupDuration)
                            warmupBytesSnapshot = done;

                        double instMbps = warmupBytesSnapshot >= 0
                            ? (done - warmupBytesSnapshot) * 8 / 1e6 / Math.Max((sw.Elapsed - WarmupDuration).TotalSeconds, 0.001)
                            : 0;

                        double percent = Math.Min(100, 100.0 * sw.Elapsed.TotalSeconds / (WarmupDuration + MeasureDuration).TotalSeconds);
                        progress.Report(new SpeedTestProgress
                        {
                            Phase       = phase,
                            BytesDone   = (long)percent,
                            TotalBytes  = 100,
                            CurrentMbps = instMbps
                        });
                    }
                }
                catch (OperationCanceledException) { /* normal ved afslutning */ }
            }, token);

            using var streamsCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var streamTasks = Enumerable.Range(0, ParallelStreams)
                .Select(_ => phase == SpeedTestPhase.Download
                    ? RunDownloadStreamAsync(OnBytes, streamsCts.Token)
                    : RunUploadStreamAsync(OnBytes, streamsCts.Token))
                .ToArray();

            try
            {
                // Målingen er tidsstyret, ikke bytestyret: vent warm-up + measure, stop så streams.
                await Task.Delay(WarmupDuration + MeasureDuration, token);
            }
            catch (OperationCanceledException) { /* eksternt annulleret — falder igennem til oprydning */ }
            finally
            {
                streamsCts.Cancel();
                reportTimer.Dispose();
                try { await Task.WhenAll(streamTasks); } catch (OperationCanceledException) { }
                try { await reportLoop; } catch (OperationCanceledException) { }
            }

            ct.ThrowIfCancellationRequested();
            sw.Stop();

            long rawBytes = Interlocked.Read(ref totalBytes);
            if (rawBytes == 0)
                throw new IOException($"Hastighedstest ({phase}): ingen data modtaget fra {SpeedTestHost} — se crash.log for detaljer.");

            long measuredBytes = Math.Max(rawBytes - Math.Max(warmupBytesSnapshot, 0), 0);
            double measuredSeconds = Math.Max((sw.Elapsed - WarmupDuration).TotalSeconds, 0.001);
            return (measuredBytes * 8 / 1e6 / measuredSeconds, measuredBytes);
        }

        // Cloudflares __down-endpoint afviser (HTTP 403) bytes-parametre over ~100 MB,
        // og et enkelt request-loft rækker ikke på hurtige linjer: ved fx 900 Mbit/s pr.
        // stream (900/6 = 150 Mbit/s) er 80 MB brugt op på ~4 sekunder — godt inden
        // måleperioden er omme — hvorefter streamen dør, og aggregatet falder brat midt
        // i målingen. Derfor genstartes streamen når den løber tør, med en kort pause
        // (undgår at gentagne hurtige requests rammer Cloudflares rate-limiter/429).
        private const long RequestChunkBytes = 80_000_000;
        private static readonly TimeSpan RestartDelay = TimeSpan.FromMilliseconds(150);

        private static async Task RunDownloadStreamAsync(Action<long> onBytes, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var resp = await _speedClient.GetAsync(
                        $"https://{SpeedTestHost}/__down?bytes={RequestChunkBytes}",
                        HttpCompletionOption.ResponseHeadersRead, ct);
                    resp.EnsureSuccessStatusCode();

                    await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                    var buffer = new byte[65536];
                    int read;
                    while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                        onBytes(read);
                }
                catch (OperationCanceledException) { return; /* forventet: streamsCts stopper streamen ved vinduets afslutning */ }
                catch (Exception ex)
                {
                    Utils.CrashLog.Write("SpeedTest.Download", ex);
                    return; // reel fejl (fx 429) — stop denne stream, de øvrige fortsætter
                }

                try { await Task.Delay(RestartDelay, ct); } catch (OperationCanceledException) { return; }
            }
        }

        private static async Task RunUploadStreamAsync(Action<long> onBytes, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var content = new ProgressStreamContent(RequestChunkBytes, onBytes);
                    using var resp = await _speedClient.PostAsync($"https://{SpeedTestHost}/__up", content, ct);
                    resp.EnsureSuccessStatusCode();
                }
                catch (OperationCanceledException) { return; /* forventet: streamsCts stopper streamen ved vinduets afslutning */ }
                catch (Exception ex)
                {
                    Utils.CrashLog.Write("SpeedTest.Upload", ex);
                    return;
                }

                try { await Task.Delay(RestartDelay, ct); } catch (OperationCanceledException) { return; }
            }
        }

        /// <summary>Genererer og sender tilfældige bytes løbende, så upload rapporterer reel fremdrift i stedet for ét stort blob.</summary>
        private sealed class ProgressStreamContent : HttpContent
        {
            private readonly long _totalBytes;
            private readonly Action<long> _onBytes;

            public ProgressStreamContent(long totalBytes, Action<long> onBytes)
            {
                _totalBytes = totalBytes;
                _onBytes = onBytes;
                Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                Headers.ContentLength = totalBytes;
            }

            // Kun base-klassens kontrakt kræver denne overload — moderne HttpClient kalder
            // altid CT-varianten nedenfor, så CancellationToken.None her rammes reelt aldrig.
            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
                => await SerializeToStreamAsync(stream, context, CancellationToken.None);

            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
            {
                const int chunkSize = 65536;
                var buffer = new byte[chunkSize];
                long remaining = _totalBytes;
                while (remaining > 0)
                {
                    int n = (int)Math.Min(chunkSize, remaining);
                    Random.Shared.NextBytes(n == chunkSize ? buffer : buffer.AsSpan(0, n));
                    await stream.WriteAsync(buffer.AsMemory(0, n), ct);
                    remaining -= n;
                    _onBytes(n);
                }
            }

            protected override bool TryComputeLength(out long length)
            {
                length = _totalBytes;
                return true;
            }
        }
    }
}
