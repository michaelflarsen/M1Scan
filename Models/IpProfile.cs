using System;
using System.Collections.Generic;

namespace MMPing.Models
{
    public class IpProfile
    {
        public string ProfileName { get; set; } = string.Empty;
        public string AdapterName { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public string SubnetMask { get; set; } = string.Empty;
        public string? Gateway { get; set; }
        public string[] DnsServers { get; set; } = Array.Empty<string>();
        public bool IsDhcp { get; set; }
        public DateTime CreatedDate { get; set; } = DateTime.Now;
        public DateTime LastModified { get; set; } = DateTime.Now;
    }
}
