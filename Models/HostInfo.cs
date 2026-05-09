using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace M1Scan.Models
{
    public class HostInfo : ObservableObject
    {
        private string _hostName = string.Empty;
        private string _ipAddress = string.Empty;
        private string _macAddress = string.Empty;
        private int _responseTime;
        private bool _isReachable;
        private string _status = "Unknown";
        private DateTime _lastSeen = DateTime.Now;
        private string _adapterName = string.Empty;

        public string HostName { get => _hostName; set => SetProperty(ref _hostName, value); }
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
        public int ResponseTime { get => _responseTime; set => SetProperty(ref _responseTime, value); }
        public bool IsReachable { get => _isReachable; set => SetProperty(ref _isReachable, value); }
        public string Status { get => _status; set => SetProperty(ref _status, value); }
        public DateTime LastSeen { get => _lastSeen; set { SetProperty(ref _lastSeen, value); OnPropertyChanged(nameof(LastSeenFormatted)); } }
        public string AdapterName { get => _adapterName; set => SetProperty(ref _adapterName, value); }

        private string _osGuess = string.Empty;
        private string _vendor = string.Empty;
        private string _netBiosName = string.Empty;
        private bool _isPort80Open;
        private bool _isPort443Open;
        private bool _isPort8080Open;
        private bool _isPort502Open;

        public string OsGuess { get => _osGuess; set => SetProperty(ref _osGuess, value); }
        public string Vendor { get => _vendor; set => SetProperty(ref _vendor, value); }
        public string NetBiosName { get => _netBiosName; set => SetProperty(ref _netBiosName, value); }

        public bool IsPort80Open
        {
            get => _isPort80Open;
            set { if (SetProperty(ref _isPort80Open, value)) { OnPropertyChanged(nameof(Port80Text)); OnPropertyChanged(nameof(Url80)); } }
        }
        public bool IsPort443Open
        {
            get => _isPort443Open;
            set { if (SetProperty(ref _isPort443Open, value)) { OnPropertyChanged(nameof(Port443Text)); OnPropertyChanged(nameof(Url443)); } }
        }
        public bool IsPort8080Open
        {
            get => _isPort8080Open;
            set { if (SetProperty(ref _isPort8080Open, value)) { OnPropertyChanged(nameof(Port8080Text)); OnPropertyChanged(nameof(Url8080)); } }
        }
        public bool IsPort502Open
        {
            get => _isPort502Open;
            set { if (SetProperty(ref _isPort502Open, value)) OnPropertyChanged(nameof(Port502Text)); }
        }

        public string Port80Text   => _isPort80Open   ? "80"   : string.Empty;
        public string Port443Text  => _isPort443Open  ? "443"  : string.Empty;
        public string Port8080Text => _isPort8080Open ? "8080" : string.Empty;
        public string Port502Text  => _isPort502Open  ? "502"  : string.Empty;

        public string Url80   => _isPort80Open   ? $"http://{IpAddress}"      : string.Empty;
        public string Url443  => _isPort443Open  ? $"https://{IpAddress}"     : string.Empty;
        public string Url8080 => _isPort8080Open ? $"http://{IpAddress}:8080" : string.Empty;

        public string LastSeenFormatted => _lastSeen == default ? "-" : _lastSeen.ToString("HH:mm:ss");
    }
}
