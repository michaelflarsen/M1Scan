using System.Collections.Generic;
using M1Scan.Models;
using M1Scan.Services;
using Xunit;

namespace M1Scan.Tests.Services
{
    /// <summary>
    /// DiagnosisWizardService orkestrerer rigtige netværkskald (ping, DNS,
    /// traceroute), som ikke er egnet til CI uden netværksadgang. Disse tests
    /// dækker i stedet BuildConclusion — den rene konklusionslogik der tager
    /// færdige trin-resultater ind og udleder en DiagnosisResult, uden selv at
    /// røre netværket. Samme mønster som DeviceFingerprintTests konstruerer
    /// HostInfo direkte.
    /// </summary>
    public class DiagnosisWizardServiceTests
    {
        private static DiagnosisStep Step(string name, DiagnosisStepStatus status, string detail = "") =>
            new() { Name = name, Status = status, Detail = detail };

        [Fact]
        public void NoAdapter_ConcludesAdapterNotConnected()
        {
            var steps = new List<DiagnosisStep>
            {
                Step("Netværksadapter", DiagnosisStepStatus.Failed)
            };

            var result = DiagnosisWizardService.BuildConclusion(steps, isCgnat: false);

            Assert.Contains("adapter", result.Conclusion, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void GatewayFailed_ConcludesGatewayNotResponding()
        {
            var steps = new List<DiagnosisStep>
            {
                Step("Netværksadapter", DiagnosisStepStatus.Ok),
                Step("Gateway", DiagnosisStepStatus.Failed)
            };

            var result = DiagnosisWizardService.BuildConclusion(steps, isCgnat: false);

            Assert.Contains("router", result.Conclusion, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void CaptivePortalDetected_ConcludesLoginPageRequired()
        {
            var steps = new List<DiagnosisStep>
            {
                Step("Netværksadapter", DiagnosisStepStatus.Ok),
                Step("Gateway", DiagnosisStepStatus.Ok),
                Step("Captive portal", DiagnosisStepStatus.Warning)
            };

            var result = DiagnosisWizardService.BuildConclusion(steps, isCgnat: false);

            Assert.Contains("login-side", result.Conclusion, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void DnsFailedButWanOk_ConcludesDnsServerProblem()
        {
            var steps = new List<DiagnosisStep>
            {
                Step("Netværksadapter", DiagnosisStepStatus.Ok),
                Step("Gateway", DiagnosisStepStatus.Ok),
                Step("DNS", DiagnosisStepStatus.Failed),
                Step("Internet (WAN)", DiagnosisStepStatus.Ok),
                Step("Captive portal", DiagnosisStepStatus.Ok)
            };

            var result = DiagnosisWizardService.BuildConclusion(steps, isCgnat: false,
                wanPing: (10, 0));

            Assert.Contains("DNS", result.Conclusion);
        }

        [Fact]
        public void WanFailed_ConcludesIspProblem()
        {
            var steps = new List<DiagnosisStep>
            {
                Step("Netværksadapter", DiagnosisStepStatus.Ok),
                Step("Gateway", DiagnosisStepStatus.Ok),
                Step("DNS", DiagnosisStepStatus.Ok),
                Step("Internet (WAN)", DiagnosisStepStatus.Failed),
                Step("Captive portal", DiagnosisStepStatus.Ok)
            };

            var result = DiagnosisWizardService.BuildConclusion(steps, isCgnat: false);

            Assert.Contains("internettet er nede", result.Conclusion, System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("internetudbyder", result.Recommendation, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TraceHopWithHighLoss_ConcludesProblemAtThatHop()
        {
            var steps = new List<DiagnosisStep>
            {
                Step("Netværksadapter", DiagnosisStepStatus.Ok),
                Step("Gateway", DiagnosisStepStatus.Ok),
                Step("DNS", DiagnosisStepStatus.Ok),
                Step("Internet (WAN)", DiagnosisStepStatus.Ok),
                Step("Captive portal", DiagnosisStepStatus.Ok)
            };

            var goodHop = new TraceHopInfo { HopNumber = 1, IpAddress = "192.168.1.1" };
            for (int i = 0; i < 10; i++) goodHop.LatencySeries.Add(5);

            var badHop = new TraceHopInfo { HopNumber = 3, IpAddress = "10.0.0.1", HostName = "isp-core.example" };
            for (int i = 0; i < 10; i++) badHop.LatencySeries.Add(i < 2 ? null : 20.0); // ~20% tab

            var hops = new List<TraceHopInfo> { goodHop, badHop };

            var result = DiagnosisWizardService.BuildConclusion(steps, isCgnat: false,
                wanPing: (10, 0), hops: hops);

            Assert.Contains("isp-core.example", result.Conclusion);
            Assert.Contains("hop 3", result.Conclusion);
        }

        [Fact]
        public void DoubleNat_WithoutTraceProblem_ConcludesDoubleNatWarning()
        {
            var steps = new List<DiagnosisStep>
            {
                Step("Netværksadapter", DiagnosisStepStatus.Ok),
                Step("Gateway", DiagnosisStepStatus.Ok),
                Step("DNS", DiagnosisStepStatus.Ok),
                Step("Internet (WAN)", DiagnosisStepStatus.Ok),
                Step("Captive portal", DiagnosisStepStatus.Ok)
            };

            var result = DiagnosisWizardService.BuildConclusion(steps, isCgnat: true,
                wanPing: (10, 0));

            Assert.Contains("double-NAT", result.Conclusion);
        }

        [Fact]
        public void EverythingOk_ConcludesNetworkHealthy()
        {
            var steps = new List<DiagnosisStep>
            {
                Step("Netværksadapter", DiagnosisStepStatus.Ok),
                Step("Gateway", DiagnosisStepStatus.Ok),
                Step("DNS", DiagnosisStepStatus.Ok),
                Step("Internet (WAN)", DiagnosisStepStatus.Ok),
                Step("Captive portal", DiagnosisStepStatus.Ok)
            };

            var result = DiagnosisWizardService.BuildConclusion(steps, isCgnat: false,
                wanPing: (12, 0));

            Assert.Contains("sundt", result.Conclusion, System.StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Ingen handling nødvendig.", result.Recommendation);
        }

        [Fact]
        public void CopyableReport_ContainsAllStepsAndConclusion()
        {
            var steps = new List<DiagnosisStep>
            {
                Step("Netværksadapter", DiagnosisStepStatus.Ok, "Wi-Fi er forbundet"),
                Step("Gateway", DiagnosisStepStatus.Ok, "5 ms, 0% tab")
            };

            var result = DiagnosisWizardService.BuildConclusion(steps, isCgnat: false, wanPing: (10, 0));

            Assert.Contains("Wi-Fi er forbundet", result.CopyableReport);
            Assert.Contains("5 ms, 0% tab", result.CopyableReport);
            Assert.Contains(result.Conclusion, result.CopyableReport);
            Assert.Contains(result.Recommendation, result.CopyableReport);
        }

        // ── IsInCgnatRange ────────────────────────────────────────────────────
        // Regression: double-NAT-tjekket testede oprindeligt routerens LOKALE
        // gateway-IP (fx 192.168.1.1), som næsten aldrig ligger i CGNAT-området —
        // trinnet var derfor et permanent, stille no-op der altid konkluderede
        // "ingen double-NAT" uanset den faktiske topologi. Rettet til at teste den
        // offentlige WAN-IP i stedet (hvor CGNAT rent faktisk viser sig).

        [Theory]
        [InlineData("100.64.0.1")]
        [InlineData("100.100.50.1")]
        [InlineData("100.127.255.254")]
        public void IsInCgnatRange_DetectsAddressesInsideRange(string ip)
        {
            Assert.True(DiagnosisWizardService.IsInCgnatRange(ip));
        }

        [Theory]
        [InlineData("192.168.1.1")]   // typisk lokal router-gateway-IP
        [InlineData("100.63.255.255")] // lige under området
        [InlineData("100.128.0.0")]    // lige over området
        [InlineData("5.103.145.2")]    // almindelig offentlig IP
        [InlineData(null)]
        [InlineData("")]
        [InlineData("ikke-en-ip")]
        public void IsInCgnatRange_RejectsAddressesOutsideRange(string? ip)
        {
            Assert.False(DiagnosisWizardService.IsInCgnatRange(ip));
        }
    }
}
