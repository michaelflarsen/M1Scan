using System;
using System.Diagnostics;
using System.Threading.Tasks;
using MMPing.Models;

namespace MMPing.Services
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
        public async Task<bool> SetStaticIpAsync(string adapterName, string ipAddress, string subnetMask, string gateway)
        {
            return await Task.Run(() =>
            {
                try
                {
                    // netsh interface ipv4 set address name="Ethernet" static 192.168.1.100 255.255.255.0 192.168.1.1
                    var commands = new[]
                    {
                        $"netsh interface ipv4 set address name=\"{adapterName}\" static {ipAddress} {subnetMask} {gateway}"
                    };

                    foreach (var cmd in commands)
                    {
                        ExecuteNetshCommand(cmd);
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error setting static IP: {ex.Message}");
                    return false;
                }
            });
        }

        public async Task<bool> SetDhcpAsync(string adapterName)
        {
            return await Task.Run(() =>
            {
                try
                {
                    // netsh interface ipv4 set address name="Ethernet" dhcp
                    var cmd = $"netsh interface ipv4 set address name=\"{adapterName}\" dhcp";
                    ExecuteNetshCommand(cmd);
                    return true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error setting DHCP: {ex.Message}");
                    return false;
                }
            });
        }

        public async Task<bool> ResetNetworkAdapterAsync(string adapterName)
        {
            return await Task.Run(() =>
            {
                try
                {
                    // ipconfig /release and /renew
                    ExecuteCommand("ipconfig", $"/release \"{adapterName}\"");
                    System.Threading.Thread.Sleep(2000);
                    ExecuteCommand("ipconfig", $"/renew \"{adapterName}\"");
                    return true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error resetting adapter: {ex.Message}");
                    return false;
                }
            });
        }

        public async Task<string> FlushDnsAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    return ExecuteCommand("ipconfig", "/flushdns");
                }
                catch (Exception ex)
                {
                    return $"Error: {ex.Message}";
                }
            });
        }

        public async Task<string> RenewDhcpAsync(string adapterName)
        {
            return await Task.Run(() =>
            {
                try
                {
                    ExecuteCommand("ipconfig", "/renew");
                    return "DHCP renewed successfully";
                }
                catch (Exception ex)
                {
                    return $"Error: {ex.Message}";
                }
            });
        }

        private string ExecuteCommand(string command, string arguments)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    Verb = "runas" // Run as administrator
                }
            };

            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            return output;
        }

        private void ExecuteNetshCommand(string netshCommand)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = netshCommand.Replace("netsh ", ""),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    Verb = "runas"
                }
            };

            process.Start();
            process.WaitForExit();
        }
    }
}
