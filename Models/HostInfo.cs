using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MMPing.Models
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

        public string OsGuess { get => _osGuess; set => SetProperty(ref _osGuess, value); }
        public string Vendor { get => _vendor; set => SetProperty(ref _vendor, value); }
        public string NetBiosName { get => _netBiosName; set => SetProperty(ref _netBiosName, value); }
        public bool IsPort80Open
        {
            get => _isPort80Open;
            set { if (SetProperty(ref _isPort80Open, value)) OnPropertyChanged(nameof(Port80Text)); }
        }
        public string Port80Text => _isPort80Open ? "Åben" : string.Empty;

        public string LastSeenFormatted => _lastSeen == default ? "-" : _lastSeen.ToString("HH:mm:ss");
    }
}
