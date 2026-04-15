using System;

namespace MMPing.Models
{
    public class NetworkAdapter
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string MacAddress { get; set; } = string.Empty;
        public string[] IpAddresses { get; set; } = Array.Empty<string>();
        public string[] DnsServers { get; set; } = Array.Empty<string>();
        public string? Gateway { get; set; }
        public string? SubnetMask { get; set; }
        public bool IsDhcpEnabled { get; set; }
        public string DhcpText => IsDhcpEnabled ? "DHCP" : "Statisk";
        public bool IsConnected { get; set; }
        public string Status { get; set; } = "Unknown";
        public int Index { get; set; }

        public override string ToString() => string.IsNullOrEmpty(Description) ? Name : Description;
    }
}
