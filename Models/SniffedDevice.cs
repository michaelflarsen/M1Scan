using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace M1Scan.Models
{
    /// <summary>
    /// En enhed opdaget passivt via raw-socket sniffing (Find IP-fanen).
    /// Aggregeres pr. kilde-IP — én række pr. unik afsender på segmentet.
    /// </summary>
    public class SniffedDevice : ObservableObject
    {
        private string _ipAddress = string.Empty;
        private string _macAddress = string.Empty;
        private string _vendor = string.Empty;
        private DateTime _firstSeen = DateTime.Now;
        private DateTime _lastSeen = DateTime.Now;
        private long _packetCount;
        private bool _isOutsideSubnet;
        private bool _isOnSegment;
        private bool _isCarrierNat;
        private string _lastProtocol = string.Empty;

        public string IpAddress
        {
            get => _ipAddress;
            set { if (SetProperty(ref _ipAddress, value)) OnPropertyChanged(nameof(IpSortValue)); }
        }

        public uint IpSortValue
        {
            get
            {
                if (System.Net.IPAddress.TryParse(_ipAddress, out var addr))
                {
                    var b = addr.GetAddressBytes();
                    return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
                }
                return 0;
            }
        }

        public string MacAddress { get => _macAddress; set => SetProperty(ref _macAddress, value); }
        public string Vendor { get => _vendor; set => SetProperty(ref _vendor, value); }

        public DateTime FirstSeen
        {
            get => _firstSeen;
            set { if (SetProperty(ref _firstSeen, value)) OnPropertyChanged(nameof(FirstSeenFormatted)); }
        }

        public DateTime LastSeen
        {
            get => _lastSeen;
            set { if (SetProperty(ref _lastSeen, value)) OnPropertyChanged(nameof(LastSeenFormatted)); }
        }

        public long PacketCount { get => _packetCount; set => SetProperty(ref _packetCount, value); }

        /// <summary>True hvis IP'en ligger uden for det lokale subnet.</summary>
        public bool IsOutsideSubnet
        {
            get => _isOutsideSubnet;
            set { if (SetProperty(ref _isOutsideSubnet, value)) { OnPropertyChanged(nameof(IsForeignOnSegment)); OnPropertyChanged(nameof(Note)); } }
        }

        /// <summary>True hvis enheden beviseligt sidder på vores eget L2-segment (samme switch):
        /// enten i vores subnet, eller den har sendt broadcast/multicast (kun naboer når os),
        /// eller dens MAC kunne resolves via ARP.</summary>
        public bool IsOnSegment
        {
            get => _isOnSegment;
            set { if (SetProperty(ref _isOnSegment, value)) { OnPropertyChanged(nameof(IsForeignOnSegment)); OnPropertyChanged(nameof(Note)); } }
        }

        /// <summary>True hvis IP'en ligger i CGNAT-rangen 100.64.0.0/10 (RFC 6598) — ISP'ens
        /// Carrier-Grade NAT-lag mellem dig og deres offentlige IP. Ikke en enhed på dit LAN.</summary>
        public bool IsCarrierNat
        {
            get => _isCarrierNat;
            set { if (SetProperty(ref _isCarrierNat, value)) { OnPropertyChanged(nameof(IsForeignOnSegment)); OnPropertyChanged(nameof(Note)); } }
        }

        /// <summary>Det interessante fund: en enhed på samme switch men med en IP uden for
        /// dit subnet (fx en ukendt 172.10.1.5 mens du er på 192.168.1.x). CGNAT tæller ikke —
        /// det er ISP-infrastruktur, ikke en fysisk nabo.</summary>
        public bool IsForeignOnSegment => _isOutsideSubnet && _isOnSegment && !_isCarrierNat;

        /// <summary>Kort klassificering vist i tabellen, så man ikke forveksler ISP-lag med et fund.</summary>
        public string Note
        {
            get
            {
                if (_isCarrierNat) return "CGNAT (RFC 6598) – ISP, ikke dit LAN";
                if (IsForeignOnSegment) return "Fremmed enhed på dit segment!";
                if (_isOutsideSubnet) return "WAN-endepunkt";
                return "Lokalt subnet";
            }
        }

        public string LastProtocol { get => _lastProtocol; set => SetProperty(ref _lastProtocol, value); }

        public string FirstSeenFormatted => _firstSeen.ToString("HH:mm:ss");
        public string LastSeenFormatted => _lastSeen.ToString("HH:mm:ss");
    }
}
