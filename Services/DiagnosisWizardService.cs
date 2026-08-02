using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using M1Scan.Models;

namespace M1Scan.Services
{
    /// <summary>
    /// "Diagnosticér nu" — orkestrerer et beslutningstræ over eksisterende services
    /// (INetworkService, IDiagnosticsService, ITracerouteService) og konkluderer på
    /// dansk hvor et netværksproblem sandsynligvis ligger. Ren orkestrering: al
    /// netværksprobing genbruges fra tjenester der allerede findes — se
    /// _Archive/Design_Plan 1.3.30.md punkt 2.2 for den oprindelige 9-trins-plan.
    /// MTU-test (DF-bit-sweep) er bevidst udeladt — kræver ny ICMP-probing-logik.
    /// </summary>
    public interface IDiagnosisWizardService
    {
        /// <summary>Kører hele beslutningstræet og yielder hvert trin efterhånden som
        /// det gennemføres, så UI'en kan vise live fremdrift. Når enumereringen er
        /// færdig (efter sidste yield), indeholder LastResult den fulde konklusion.
        /// </summary>
        /// <param name="wanPublicIp">Routerens offentlige WAN-IP (fra HomeViewModel's
        /// eksisterende ip-api.com-opslag), eller null hvis den ikke er tilgængelig
        /// endnu. Bruges til double-NAT-tjekket — CGNAT viser sig på WAN-siden, ikke
        /// på routerens lokale gateway-IP (som næsten aldrig ligger i CGNAT-området).
        /// Er den null, springes double-NAT-trinnet over i stedet for at konkludere
        /// forkert.</param>
        IAsyncEnumerable<DiagnosisStep> RunAsync(string? wanPublicIp, CancellationToken ct = default);

        /// <summary>Resultatet af den senest gennemførte kørsel. Null før RunAsync er
        /// enumereret færdig mindst én gang.</summary>
        DiagnosisResult? LastResult { get; }
    }

    public class DiagnosisWizardService : IDiagnosisWizardService
    {
        private const string WanProbeHost = "1.1.1.1";
        private const string TraceTarget  = "1.1.1.1";

        private static readonly (string Server, string Label)[] DnsServers =
            { ("1.1.1.1", "Cloudflare"), ("8.8.8.8", "Google") };

        private readonly INetworkService _networkService;
        private readonly IDiagnosticsService _diagnosticsService;
        private readonly ITracerouteService _tracerouteService;

        public DiagnosisResult? LastResult { get; private set; }

        public DiagnosisWizardService(INetworkService networkService, IDiagnosticsService diagnosticsService,
                                       ITracerouteService tracerouteService)
        {
            _networkService = networkService;
            _diagnosticsService = diagnosticsService;
            _tracerouteService = tracerouteService;
        }

        public async IAsyncEnumerable<DiagnosisStep> RunAsync(string? wanPublicIp, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var steps = new List<DiagnosisStep>();

            // ── 1. Adapter/link OK? ─────────────────────────────────────────────
            var adapterStep = new DiagnosisStep { Name = "Netværksadapter", Status = DiagnosisStepStatus.Running };
            steps.Add(adapterStep);
            yield return adapterStep;

            NetworkAdapter? bestAdapter = null;
            try
            {
                var adapters = await _networkService.GetNetworkAdaptersAsync();
                bestAdapter = adapters
                    .Where(a => a.IsConnected && !string.IsNullOrEmpty(a.Gateway) && !a.Gateway!.Contains(':'))
                    .FirstOrDefault();

                if (bestAdapter != null)
                {
                    adapterStep.Status = DiagnosisStepStatus.Ok;
                    adapterStep.Detail = $"{bestAdapter} er forbundet";
                }
                else
                {
                    adapterStep.Status = DiagnosisStepStatus.Failed;
                    adapterStep.Detail = "Ingen forbundet netværksadapter med gateway fundet";
                }
            }
            catch (Exception ex)
            {
                adapterStep.Status = DiagnosisStepStatus.Failed;
                adapterStep.Detail = $"Kunne ikke læse netværksadaptere: {ex.Message}";
            }
            yield return adapterStep;

            if (bestAdapter == null)
            {
                LastResult = BuildConclusion(steps, isCgnat: false);
                yield break;
            }

            // ── 2. Gateway svarer? ───────────────────────────────────────────────
            var gatewayStep = new DiagnosisStep { Name = "Gateway", Status = DiagnosisStepStatus.Running };
            steps.Add(gatewayStep);
            yield return gatewayStep;

            var gatewayPing = await PingWithLossAsync(bestAdapter.Gateway!, 4, ct);
            if (gatewayPing.avgMs.HasValue)
            {
                gatewayStep.Status = gatewayPing.lossPercent > 0 ? DiagnosisStepStatus.Warning : DiagnosisStepStatus.Ok;
                gatewayStep.Detail = $"{gatewayPing.avgMs:F0} ms, {gatewayPing.lossPercent:F0}% tab";
            }
            else
            {
                gatewayStep.Status = DiagnosisStepStatus.Failed;
                gatewayStep.Detail = "Gateway svarer ikke";
            }
            yield return gatewayStep;

            // ── 3. DNS virker? ───────────────────────────────────────────────────
            var dnsStep = new DiagnosisStep { Name = "DNS", Status = DiagnosisStepStatus.Running };
            steps.Add(dnsStep);
            yield return dnsStep;

            List<DnsTimingResult> dnsResults;
            try { dnsResults = await _diagnosticsService.MeasureDnsServersAsync(DnsServers, ct); }
            catch { dnsResults = new List<DnsTimingResult>(); } // tom liste tolkes nedenfor som "ingen DNS-server svarede"

            var fastestDns = dnsResults.Where(d => d.ResponseMs.HasValue).OrderBy(d => d.ResponseMs).FirstOrDefault();
            if (fastestDns != null)
            {
                dnsStep.Status = DiagnosisStepStatus.Ok;
                dnsStep.Detail = $"{fastestDns.Label} svarede på {fastestDns.ResponseMs:F0} ms";
            }
            else
            {
                dnsStep.Status = DiagnosisStepStatus.Failed;
                dnsStep.Detail = "Ingen DNS-server svarede";
            }
            yield return dnsStep;

            // ── 4. WAN svarer? ───────────────────────────────────────────────────
            var wanStep = new DiagnosisStep { Name = "Internet (WAN)", Status = DiagnosisStepStatus.Running };
            steps.Add(wanStep);
            yield return wanStep;

            var wanPing = await PingWithLossAsync(WanProbeHost, 4, ct);
            if (wanPing.avgMs.HasValue)
            {
                wanStep.Status = wanPing.lossPercent > 0 ? DiagnosisStepStatus.Warning : DiagnosisStepStatus.Ok;
                wanStep.Detail = $"{wanPing.avgMs:F0} ms, {wanPing.lossPercent:F0}% tab";
            }
            else
            {
                wanStep.Status = DiagnosisStepStatus.Failed;
                wanStep.Detail = "Intet svar fra internettet";
            }
            yield return wanStep;

            // ── 5. Double-NAT? (tilnærmet: WAN-IP i CGNAT-området) ───────────────
            // CGNAT viser sig på routerens OFFENTLIGE WAN-IP, ikke dens lokale
            // gateway-IP (som vi selv pinger ovenfor og næsten aldrig ligger i
            // 100.64.0.0/10). Uden en kendt WAN-IP kan trinnet ikke afgøres og
            // springes over — at gætte forkert her ville underrapportere reelle
            // double-NAT-tilfælde uden brugeren nogensinde ville se det.
            var natStep = new DiagnosisStep { Name = "Double-NAT", Status = DiagnosisStepStatus.Running };
            steps.Add(natStep);
            yield return natStep;

            bool isCgnat = false;
            if (string.IsNullOrEmpty(wanPublicIp))
            {
                natStep.Status = DiagnosisStepStatus.Skipped;
                natStep.Detail = "Kunne ikke afgøres (offentlig WAN-IP ikke tilgængelig)";
            }
            else
            {
                isCgnat = IsInCgnatRange(wanPublicIp);
                natStep.Status = isCgnat ? DiagnosisStepStatus.Warning : DiagnosisStepStatus.Ok;
                natStep.Detail = isCgnat
                    ? "Din router er bag endnu et NAT-lag (typisk hos ISP'en) — kan begrænse port-forwarding"
                    : "Ingen double-NAT opdaget";
            }
            yield return natStep;

            // ── 6. Captive portal? ───────────────────────────────────────────────
            var portalStep = new DiagnosisStep { Name = "Captive portal", Status = DiagnosisStepStatus.Running };
            steps.Add(portalStep);
            yield return portalStep;

            CaptivePortalStatus portalStatus;
            try { portalStatus = await _diagnosticsService.CheckCaptivePortalAsync(ct); }
            catch { portalStatus = CaptivePortalStatus.NoResponse; } // uafgjort, ikke en fejlkonklusion

            portalStep.Status = portalStatus == CaptivePortalStatus.PortalDetected
                ? DiagnosisStepStatus.Warning : DiagnosisStepStatus.Ok;
            portalStep.Detail = portalStatus switch
            {
                CaptivePortalStatus.PortalDetected => "Login-side opdaget",
                CaptivePortalStatus.None           => "Ingen login-side",
                _                                  => "Kunne ikke afgøres"
            };
            yield return portalStep;

            // ── 7. Traceroute — hvor stiger latency/loss? (kun hvis WAN svarer) ───
            List<TraceHopInfo> hops = new();
            var traceStep = new DiagnosisStep { Name = "Traceroute", Status = DiagnosisStepStatus.Running };
            steps.Add(traceStep);
            yield return traceStep;

            if (!wanPing.avgMs.HasValue)
            {
                traceStep.Status = DiagnosisStepStatus.Skipped;
                traceStep.Detail = "Sprunget over — intet svar fra internettet";
            }
            else
            {
                try
                {
                    await foreach (var hop in _tracerouteService.TraceRouteAsync(TraceTarget, maxHops: 20, timeoutMs: 600, ct: ct))
                        hops.Add(hop);

                    traceStep.Status = DiagnosisStepStatus.Ok;
                    traceStep.Detail = $"{hops.Count} hop til {TraceTarget}";
                }
                catch (Exception ex)
                {
                    traceStep.Status = DiagnosisStepStatus.Warning;
                    traceStep.Detail = $"Traceroute kunne ikke gennemføres: {ex.Message}";
                }
            }
            yield return traceStep;

            LastResult = BuildConclusion(steps, isCgnat, gatewayPing, wanPing, hops);
        }

        /// <summary>Ren funktion: udleder konklusion + anbefaling + kopiérbar rapport
        /// fra allerede-gennemførte trin. Ingen netværksadgang — testbar isoleret.</summary>
        internal static DiagnosisResult BuildConclusion(
            IReadOnlyList<DiagnosisStep> steps,
            bool isCgnat,
            (double? avgMs, double lossPercent)? gatewayPing = null,
            (double? avgMs, double lossPercent)? wanPing = null,
            IReadOnlyList<TraceHopInfo>? hops = null)
        {
            string conclusion, recommendation;

            var adapterStep = steps.FirstOrDefault(s => s.Name == "Netværksadapter");
            var gatewayStep = steps.FirstOrDefault(s => s.Name == "Gateway");
            var dnsStep     = steps.FirstOrDefault(s => s.Name == "DNS");
            var wanStep     = steps.FirstOrDefault(s => s.Name == "Internet (WAN)");
            var portalStep  = steps.FirstOrDefault(s => s.Name == "Captive portal");

            if (adapterStep == null || adapterStep.Status == DiagnosisStepStatus.Failed)
            {
                conclusion = "Din netværksadapter er ikke forbundet.";
                recommendation = "Tjek at netværkskablet sidder korrekt, eller at Wi-Fi er slået til og forbundet.";
            }
            else if (gatewayStep?.Status == DiagnosisStepStatus.Failed)
            {
                conclusion = "Din router/gateway svarer ikke.";
                recommendation = "Tjek kabler til routeren, og prøv at genstarte den.";
            }
            else if (portalStep?.Status == DiagnosisStepStatus.Warning)
            {
                conclusion = "Du er bag en login-side (fx hotel- eller gæste-WiFi).";
                recommendation = "Åbn en browser og log ind på netværket, før du prøver igen.";
            }
            else if (dnsStep?.Status == DiagnosisStepStatus.Failed && wanStep?.Status != DiagnosisStepStatus.Failed)
            {
                conclusion = "DNS-serveren svarer ikke, men selve internetforbindelsen virker.";
                recommendation = "Skift DNS-server under IP-skift (fx til 1.1.1.1 eller 8.8.8.8).";
            }
            else if (wanStep?.Status == DiagnosisStepStatus.Failed)
            {
                conclusion = "Din router har forbindelse, men internettet er nede.";
                recommendation = "Kontakt din internetudbyder — problemet ligger uden for dit eget netværk.";
            }
            else
            {
                // Find det hop hvor tab/latency stiger markant ift. det foregående hop.
                var badHop = FindProblemHop(hops);
                if (badHop != null)
                {
                    var where = !string.IsNullOrEmpty(badHop.HostName) ? badHop.HostName
                              : !string.IsNullOrEmpty(badHop.IpAddress) ? badHop.IpAddress
                              : $"hop {badHop.HopNumber}";
                    conclusion = badHop.LatencySeries.LossPercent > 0
                        ? $"Problemet er hos {where} (hop {badHop.HopNumber}) — taber {badHop.LatencySeries.LossPercent:F0}% pakker. Dit eget netværk er OK."
                        : $"Latensen stiger markant ved {where} (hop {badHop.HopNumber}, {badHop.LatencySeries.Avg:F0} ms). Dit eget netværk er OK.";
                    recommendation = "Kontakt din internetudbyder og oplys ovenstående hop-nummer og måling.";
                }
                else if (isCgnat)
                {
                    conclusion = "Netværket virker, men din router er bag double-NAT (typisk hos ISP'en).";
                    recommendation = "Dette er normalt ikke et problem, men kan begrænse port-forwarding og enkelte spil/tjenester.";
                }
                else
                {
                    var lossNote = wanPing?.lossPercent > 0 ? $", {wanPing.Value.lossPercent:F0}% tab" : "";
                    conclusion = $"Dit netværk ser sundt ud — {wanPing?.avgMs:F0} ms til internettet{lossNote}.";
                    recommendation = "Ingen handling nødvendig.";
                }
            }

            return new DiagnosisResult
            {
                Steps = steps,
                Conclusion = conclusion,
                Recommendation = recommendation,
                CopyableReport = BuildReport(steps, conclusion, recommendation)
            };
        }

        /// <summary>Finder det første hop hvor tab er markant (≥5%) eller latency
        /// stiger markant (≥3x foregående gyldige hop) — en simpel, forklarlig
        /// heuristik frem for statistisk modellering af "normalt" spring.</summary>
        private static TraceHopInfo? FindProblemHop(IReadOnlyList<TraceHopInfo>? hops)
        {
            if (hops == null || hops.Count == 0) return null;

            double previousAvg = 0;
            foreach (var hop in hops.OrderBy(h => h.HopNumber))
            {
                if (hop.LatencySeries.LossPercent >= 5)
                    return hop;

                if (previousAvg > 5 && hop.LatencySeries.Avg > previousAvg * 3)
                    return hop;

                if (hop.LatencySeries.Avg > 0)
                    previousAvg = hop.LatencySeries.Avg;
            }
            return null;
        }

        private static string BuildReport(IReadOnlyList<DiagnosisStep> steps, string conclusion, string recommendation)
        {
            var sb = new StringBuilder();
            sb.AppendLine("M1Scan — Netværksdiagnose");
            sb.AppendLine(DateTimeOffset.Now.ToString("dd/MM/yyyy HH:mm"));
            sb.AppendLine();
            foreach (var step in steps)
                sb.AppendLine($"{step.StatusIcon} {step.Name}: {step.Detail}");
            sb.AppendLine();
            sb.AppendLine($"Konklusion: {conclusion}");
            sb.AppendLine($"Anbefaling: {recommendation}");
            return sb.ToString();
        }

        internal static bool IsInCgnatRange(string? ip)
        {
            if (string.IsNullOrEmpty(ip) || !IPAddress.TryParse(ip, out var addr)) return false;
            if (addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;

            var bytes = addr.GetAddressBytes();
            // 100.64.0.0/10 (RFC 6598)
            return bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127;
        }

        private static async Task<(double? avgMs, double lossPercent)> PingWithLossAsync(string host, int count, CancellationToken ct)
        {
            int success = 0;
            double totalMs = 0;

            for (int i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var ping = new Ping();
                    var reply = await ping.SendPingAsync(host, 1000);
                    if (reply.Status == IPStatus.Success)
                    {
                        success++;
                        totalMs += reply.RoundtripTime;
                    }
                }
                catch { /* tæller som tab */ }
            }

            double lossPercent = 100.0 * (count - success) / count;
            double? avgMs = success > 0 ? totalMs / success : null;
            return (avgMs, lossPercent);
        }
    }
}
