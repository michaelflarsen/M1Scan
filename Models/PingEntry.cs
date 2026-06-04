using System;
using M1Scan.Utils;

namespace M1Scan.Models
{
    public enum PingStatus { Waiting, Online, Offline, Timeout }

    public class PingEntry : ObservableObject
    {
        private string _ipAddress = string.Empty;
        private string _description = string.Empty;
        private bool _isOnline;
        private long _roundtripMs;
        private DateTime _lastChecked = DateTime.MinValue;
        private PingStatus _status = PingStatus.Waiting;
        private bool _isFollowOpen;
        private string _followIpInput = string.Empty;
        private string _followMaskInput = "255.255.255.0";
        private string _followGatewayInput = string.Empty;
        private bool? _port80Open;
        private bool? _port443Open;
        private bool? _port8080Open;
        private bool _isCheckingPorts;

        public string IpAddress
        {
            get => _ipAddress;
            set => SetProperty(ref _ipAddress, value);
        }

        public string Description
        {
            get => _description;
            set => SetProperty(ref _description, value);
        }

        public bool IsOnline
        {
            get => _isOnline;
            set => SetProperty(ref _isOnline, value);
        }

        public long RoundtripMs
        {
            get => _roundtripMs;
            set => SetProperty(ref _roundtripMs, value);
        }

        public DateTime LastChecked
        {
            get => _lastChecked;
            set { if (SetProperty(ref _lastChecked, value)) OnPropertyChanged(nameof(LastCheckedFormatted)); }
        }

        public PingStatus Status
        {
            get => _status;
            set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(StatusText)); }
        }

        public bool IsFollowOpen
        {
            get => _isFollowOpen;
            set => SetProperty(ref _isFollowOpen, value);
        }

        public string FollowIpInput
        {
            get => _followIpInput;
            set => SetProperty(ref _followIpInput, value);
        }

        public string FollowMaskInput
        {
            get => _followMaskInput;
            set => SetProperty(ref _followMaskInput, value);
        }

        public string FollowGatewayInput
        {
            get => _followGatewayInput;
            set => SetProperty(ref _followGatewayInput, value);
        }

        public bool? Port80Open
        {
            get => _port80Open;
            set => SetProperty(ref _port80Open, value);
        }

        public bool? Port443Open
        {
            get => _port443Open;
            set => SetProperty(ref _port443Open, value);
        }

        public bool? Port8080Open
        {
            get => _port8080Open;
            set => SetProperty(ref _port8080Open, value);
        }

        public bool IsCheckingPorts
        {
            get => _isCheckingPorts;
            set => SetProperty(ref _isCheckingPorts, value);
        }

        public string LastCheckedFormatted =>
            _lastChecked == DateTime.MinValue ? "—" : _lastChecked.ToString("HH:mm:ss");

        public string StatusText => _status switch
        {
            PingStatus.Online  => "Online",
            PingStatus.Offline => "Offline",
            PingStatus.Timeout => "Timeout",
            _                  => "Waiting"
        };
    }
}
