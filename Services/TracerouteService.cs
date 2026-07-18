using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using M1Scan.Models;

namespace M1Scan.Services
{
    public interface ITracerouteService
    {
        IAsyncEnumerable<TraceHopInfo> TraceRouteAsync(string hostOrIp, int maxHops = 32, int timeoutMs = 600, CancellationToken ct = default);
        IAsyncEnumerable<TraceHopInfo> ContinuousProbeAsync(List<TraceHopInfo> hops, int timeoutMs = 600, int delayBetweenHopsMs = 2000, CancellationToken ct = default);
        Task<string?> ResolveHostNameAsync(string ip, int timeoutMs = 1500, CancellationToken ct = default);
    }

    public class TracerouteService : ITracerouteService
    {
        private const int PingsPerHop = 3; // Match Windows tracert: 3 forsøg per hop

        public TracerouteService()
        {
        }

        /// <summary>
        /// Traceroute via ICMP TTL escalation. Yields hops progressively as they arrive.
        /// 3 pings per hop, DNS reverse lookup, latency stats.
        /// </summary>
        public async IAsyncEnumerable<TraceHopInfo> TraceRouteAsync(
            string hostOrIp,
            int maxHops = 32,
            int timeoutMs = 600,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            IPAddress? targetIp = null;

            try
            {
                if (IPAddress.TryParse(hostOrIp, out var parsed))
                    targetIp = parsed;
                else
                {
                    var addresses = await Dns.GetHostAddressesAsync(hostOrIp, AddressFamily.InterNetwork, ct);
                    targetIp = addresses.FirstOrDefault();
                }

                if (targetIp == null)
                    yield break;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                yield break;
            }

            using var ping = new Ping();
            bool destinationReached = false;

            for (int ttl = 1; ttl <= maxHops && !ct.IsCancellationRequested && !destinationReached; ttl++)
            {
                var hopInfo = new TraceHopInfo
                {
                    HopNumber = ttl,
                    LatencySeries = new LatencySeries { Label = $"Hop {ttl}" }
                };

                // 3 pings per hop (som Windows tracert)
                for (int attempt = 0; attempt < PingsPerHop && !ct.IsCancellationRequested; attempt++)
                {
                    try
                    {
                        var options = new PingOptions(ttl, false);
                        var reply = await ping.SendPingAsync(targetIp, TimeSpan.FromMilliseconds(timeoutMs), new byte[32], options, ct);

                        if (reply.Status == IPStatus.Success)
                        {
                            hopInfo.IpAddress = reply.Address.ToString();
                            hopInfo.IsReachable = true;
                            hopInfo.LatencySeries.Add(reply.RoundtripTime);
                            destinationReached = true;
                        }
                        else if (reply.Status == IPStatus.TtlExpired)
                        {
                            hopInfo.IpAddress = reply.Address.ToString();
                            hopInfo.IsReachable = true;
                            hopInfo.LatencySeries.Add(reply.RoundtripTime);
                        }
                        else if (reply.Status == IPStatus.TimedOut)
                        {
                            hopInfo.IsTimeout = true;
                            hopInfo.LatencySeries.Add(null);
                        }
                        else
                        {
                            hopInfo.LatencySeries.Add(null);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        hopInfo.IsTimeout = true;
                        hopInfo.LatencySeries.Add(null);
                    }

                    // Små delay mellem forsøg
                    if (attempt < PingsPerHop - 1)
                        await Task.Delay(30, ct);
                }

                // Skip DNS reverse lookup here — it will run in parallel after the trace completes.
                // Yield hop med det samme — UI opdaterer progressivt!
                yield return hopInfo;

                // Delay mellem hops
                if (ttl < maxHops)
                    await Task.Delay(100, ct);
            }
        }

        /// <summary>
        /// Continuous probe: re-probes all hops in a loop, updating rolling averages.
        /// Yields each hop with updated latency stats as probe data arrives.
        ///
        /// IMPORTANT: This method mutates the hop objects in-place (modifies LatencySeries.Add).
        /// The caller must NOT clear or replace the original hops list during probing — the probe
        /// will continue using the stale references and results won't reach the UI.
        ///
        /// Thread-safety: Probe runs on background thread. Caller must marshal UI updates to UI thread.
        /// </summary>
        public async IAsyncEnumerable<TraceHopInfo> ContinuousProbeAsync(
            List<TraceHopInfo> hops,
            int timeoutMs = 600,
            int delayBetweenHopsMs = 2000,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (hops.Count == 0)
                yield break;

            using var ping = new Ping();

            while (!ct.IsCancellationRequested)
            {
                foreach (var hop in hops)
                {
                    if (ct.IsCancellationRequested)
                        break;

                    if (string.IsNullOrEmpty(hop.IpAddress))
                        continue;

                    try
                    {
                        var ttl = hop.HopNumber;
                        var options = new PingOptions(ttl, false);
                        var reply = await ping.SendPingAsync(
                            IPAddress.Parse(hop.IpAddress),
                            TimeSpan.FromMilliseconds(timeoutMs),
                            new byte[32],
                            options,
                            ct);

                        if (reply.Status == IPStatus.Success || reply.Status == IPStatus.TtlExpired)
                        {
                            hop.LatencySeries.Add(reply.RoundtripTime);
                        }
                        else
                        {
                            hop.LatencySeries.Add(null);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        hop.LatencySeries.Add(null);
                    }

                    // Yield after each probe — UI updates live
                    yield return hop;

                    // Delay between hops
                    if (!ct.IsCancellationRequested)
                        await Task.Delay(delayBetweenHopsMs, ct);
                }
            }
        }

        /// <summary>
        /// Resolve hostname for a single IP address with a timeout.
        /// Returns null if resolution fails or times out.
        /// </summary>
        public async Task<string?> ResolveHostNameAsync(string ip, int timeoutMs = 1500, CancellationToken ct = default)
        {
            try
            {
                var dnsTask = Dns.GetHostEntryAsync(ip);
                var delayTask = Task.Delay(timeoutMs, ct);
                var completed = await Task.WhenAny(dnsTask, delayTask);

                if (completed == dnsTask)
                {
                    var hostEntry = await dnsTask;
                    return hostEntry.HostName;
                }

                // Timeout — return null
                return null;
            }
            catch
            {
                return null;
            }
        }
    }
}
