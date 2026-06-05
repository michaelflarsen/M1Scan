using System;
using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using M1Scan.Models;

namespace M1Scan.Services
{
    public interface IIpConfigService
    {
        Task<bool> SetStaticIpAsync(string adapterName, string ipAddress, string subnetMask, string gateway);
        Task<bool> SetDhcpAsync(string adapterName);
        Task<bool> ResetNetworkAdapterAsync(string adapterName);
        Task<string> FlushDnsAsync();
        Task<string> RenewDhcpAsync(string adapterName);
    }

    public class IpConfigService : IIpConfigService
    {
        private static readonly Regex AdapterNamePattern =
            new(@"^[\w\s\-\.\(\)#]+$", RegexOptions.Compiled);

        private static bool IsValidAdapterName(string name) =>
            !string.IsNullOrWhiteSpace(name) && AdapterNamePattern.IsMatch(name);

        public async Task<bool> SetStaticIpAsync(string adapterName, string ipAddress, string subnetMask, string gateway)
        {
            if (!IsValidAdapterName(adapterName) ||
                !IPAddress.TryParse(ipAddress, out _) ||
                !IPAddress.TryParse(subnetMask, out _) ||
                (!string.IsNullOrEmpty(gateway) && !IPAddress.TryParse(gateway, out _)))
                return false;

            return await Task.Run(() =>
            {
                try
                {
                    if (string.IsNullOrEmpty(gateway))
                        RunProcess("netsh", "interface", "ipv4", "set", "address",
                            $"name={adapterName}", "static", ipAddress, subnetMask);
                    else
                        RunProcess("netsh", "interface", "ipv4", "set", "address",
                            $"name={adapterName}", "static", ipAddress, subnetMask, gateway);
                    return true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error setting static IP: {ex.Message}");
                    return false;
                }
            });
        }

        public async Task<bool> SetDhcpAsync(string adapterName)
        {
            if (!IsValidAdapterName(adapterName)) return false;

            return await Task.Run(() =>
            {
                try
                {
                    RunProcess("netsh", "interface", "ipv4", "set", "address",
                        $"name={adapterName}", "dhcp");
                    return true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error setting DHCP: {ex.Message}");
                    return false;
                }
            });
        }

        public async Task<bool> ResetNetworkAdapterAsync(string adapterName)
        {
            if (!IsValidAdapterName(adapterName)) return false;

            return await Task.Run(() =>
            {
                try
                {
                    RunProcess("ipconfig", "/release", adapterName);
                    System.Threading.Thread.Sleep(2000);
                    RunProcess("ipconfig", "/renew", adapterName);
                    return true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error resetting adapter: {ex.Message}");
                    return false;
                }
            });
        }

        public async Task<string> FlushDnsAsync()
        {
            return await Task.Run(() =>
            {
                try { return RunProcessWithOutput("ipconfig", "/flushdns"); }
                catch (Exception ex) { return $"Error: {ex.Message}"; }
            });
        }

        public async Task<string> RenewDhcpAsync(string adapterName)
        {
            return await Task.Run(() =>
            {
                try
                {
                    RunProcess("ipconfig", "/renew", adapterName);
                    return "DHCP renewed successfully";
                }
                catch (Exception ex) { return $"Error: {ex.Message}"; }
            });
        }

        private static void RunProcess(string fileName, params string[] args)
        {
            var psi = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var process = new Process { StartInfo = psi };
            process.Start();
            process.WaitForExit(10_000);
            if (!process.HasExited) process.Kill();
        }

        private static string RunProcessWithOutput(string fileName, params string[] args)
        {
            var psi = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var process = new Process { StartInfo = psi };
            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);
            if (!process.HasExited) process.Kill();
            return output;
        }
    }
}
