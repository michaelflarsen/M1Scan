using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace M1Scan.Services
{
    /// <summary>
    /// Udfaldet af en netsh/ipconfig-operation. Success afspejler barnets faktiske
    /// exit-kode — ikke blot at Process.Start ikke kastede. Message indeholder den
    /// rå output-tekst ved fejl, så brugeren kan se HVAD der gik galt.
    /// </summary>
    public readonly record struct IpConfigResult(bool Success, string Message)
    {
        public static IpConfigResult Ok(string message = "") => new(true, message);
        public static IpConfigResult Fail(string message) => new(false, message);
    }

    public interface IIpConfigService
    {
        Task<IpConfigResult> SetStaticIpAsync(string adapterName, string ipAddress, string subnetMask, string gateway);
        Task<IpConfigResult> SetDhcpAsync(string adapterName);
        Task<IpConfigResult> ResetNetworkAdapterAsync(string adapterName);
        Task<IpConfigResult> FlushDnsAsync();
        Task<IpConfigResult> RenewDhcpAsync(string adapterName);
    }

    public class IpConfigService : IIpConfigService
    {
        // netsh/ipconfig får rigeligt med 10 s; ipconfig /release+/renew kan være langsom
        // på et mættet netværk, så den får sin egen, længere grænse.
        private const int DefaultTimeoutMs = 10_000;
        private const int DhcpTimeoutMs = 30_000;

        private static readonly Regex AdapterNamePattern =
            new(@"^[\w\s\-\.\(\)#]+$", RegexOptions.Compiled);

        private static bool IsValidAdapterName(string name) =>
            !string.IsNullOrWhiteSpace(name) && AdapterNamePattern.IsMatch(name);

        public async Task<IpConfigResult> SetStaticIpAsync(string adapterName, string ipAddress, string subnetMask, string gateway)
        {
            if (!IsValidAdapterName(adapterName))
                return IpConfigResult.Fail($"Ugyldigt adapternavn: '{adapterName}'");
            if (!IPAddress.TryParse(ipAddress, out _))
                return IpConfigResult.Fail($"Ugyldig IP-adresse: '{ipAddress}'");
            if (!IPAddress.TryParse(subnetMask, out _))
                return IpConfigResult.Fail($"Ugyldig subnetmaske: '{subnetMask}'");
            if (!string.IsNullOrEmpty(gateway) && !IPAddress.TryParse(gateway, out _))
                return IpConfigResult.Fail($"Ugyldig gateway: '{gateway}'");

            return await Task.Run(() =>
            {
                try
                {
                    var run = string.IsNullOrEmpty(gateway)
                        ? RunProcess(DefaultTimeoutMs, "netsh", "interface", "ipv4", "set", "address",
                              $"name={adapterName}", "static", ipAddress, subnetMask)
                        : RunProcess(DefaultTimeoutMs, "netsh", "interface", "ipv4", "set", "address",
                              $"name={adapterName}", "static", ipAddress, subnetMask, gateway);

                    return ToResult(run, "Statisk IP anvendt");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"SetStaticIpAsync failed: {ex}");
                    return IpConfigResult.Fail($"Kunne ikke starte netsh: {ex.Message}");
                }
            });
        }

        public async Task<IpConfigResult> SetDhcpAsync(string adapterName)
        {
            if (!IsValidAdapterName(adapterName))
                return IpConfigResult.Fail($"Ugyldigt adapternavn: '{adapterName}'");

            return await Task.Run(() =>
            {
                try
                {
                    var run = RunProcess(DefaultTimeoutMs, "netsh", "interface", "ipv4", "set", "address",
                        $"name={adapterName}", "dhcp");
                    return ToResult(run, "DHCP anvendt");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"SetDhcpAsync failed: {ex}");
                    return IpConfigResult.Fail($"Kunne ikke starte netsh: {ex.Message}");
                }
            });
        }

        public async Task<IpConfigResult> ResetNetworkAdapterAsync(string adapterName)
        {
            if (!IsValidAdapterName(adapterName))
                return IpConfigResult.Fail($"Ugyldigt adapternavn: '{adapterName}'");

            return await Task.Run(async () =>
            {
                try
                {
                    var release = RunProcess(DhcpTimeoutMs, "ipconfig", "/release", adapterName);
                    if (release.ExitCode != 0)
                        return IpConfigResult.Fail($"ipconfig /release fejlede: {Summarize(release)}");

                    await Task.Delay(2000).ConfigureAwait(false);

                    var renew = RunProcess(DhcpTimeoutMs, "ipconfig", "/renew", adapterName);
                    return ToResult(renew, "Adapter nulstillet");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"ResetNetworkAdapterAsync failed: {ex}");
                    return IpConfigResult.Fail($"Kunne ikke starte ipconfig: {ex.Message}");
                }
            });
        }

        public async Task<IpConfigResult> FlushDnsAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    var run = RunProcess(DefaultTimeoutMs, "ipconfig", "/flushdns");
                    return ToResult(run, "DNS-cache tømt");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"FlushDnsAsync failed: {ex}");
                    return IpConfigResult.Fail($"Kunne ikke starte ipconfig: {ex.Message}");
                }
            });
        }

        public async Task<IpConfigResult> RenewDhcpAsync(string adapterName)
        {
            if (!IsValidAdapterName(adapterName))
                return IpConfigResult.Fail($"Ugyldigt adapternavn: '{adapterName}'");

            return await Task.Run(() =>
            {
                try
                {
                    var run = RunProcess(DhcpTimeoutMs, "ipconfig", "/renew", adapterName);
                    return ToResult(run, "DHCP-lease fornyet");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"RenewDhcpAsync failed: {ex}");
                    return IpConfigResult.Fail($"Kunne ikke starte ipconfig: {ex.Message}");
                }
            });
        }

        private static IpConfigResult ToResult(ProcessRun run, string successMessage) =>
            run.ExitCode == 0
                ? IpConfigResult.Ok(successMessage)
                : IpConfigResult.Fail($"{successMessage} fejlede: {Summarize(run)}");

        // netsh skriver fejl på stdout, ipconfig på begge — vis den første ikke-tomme
        // linje, ellers bliver beskeden til en væg af hjælpetekst.
        private static string Summarize(ProcessRun run)
        {
            var text = string.IsNullOrWhiteSpace(run.Output) ? run.Error : run.Output;
            var firstLine = text?
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.Length > 0);

            return string.IsNullOrEmpty(firstLine)
                ? $"exit-kode {run.ExitCode}"
                : $"{firstLine} (exit-kode {run.ExitCode})";
        }

        private readonly record struct ProcessRun(int ExitCode, string Output, string Error);

        /// <summary>
        /// Starter et konsolværktøj og venter på det. stdout/stderr læses ASYNKRONT
        /// mens vi venter — læses de ikke, blokerer barnet når pipe-bufferen (~4 KB)
        /// fylder, og WaitForExit hænger til timeout. Argumenter går via ArgumentList,
        /// så intet quotes/escapes manuelt (ingen injection-flade).
        /// </summary>
        private static ProcessRun RunProcess(int timeoutMs, string fileName, params string[] args)
        {
            var psi = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException($"Kunne ikke starte '{fileName}'.");

            // Start læsningen FØR WaitForExit, ellers er deadlocken tilbage.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* allerede væk */ }
                return new ProcessRun(-1, SafeResult(stdoutTask), $"Timeout efter {timeoutMs / 1000}s");
            }

            // WaitForExit(int) venter ikke på at de omdirigerede streams lukkes —
            // det gør den parameterløse overload. Nødvendig for at få al output med.
            process.WaitForExit();

            return new ProcessRun(process.ExitCode, SafeResult(stdoutTask), SafeResult(stderrTask));
        }

        private static string SafeResult(Task<string> task)
        {
            try { return task.GetAwaiter().GetResult(); }
            catch { return string.Empty; }
        }
    }
}
