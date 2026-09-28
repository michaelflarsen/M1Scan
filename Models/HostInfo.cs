using System;
using System.Collections.Generic;
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
        private string _customName = string.Empty;

        public string HostName
        {
            get => _hostName;
            set { if (SetProperty(ref _hostName, value)) { OnPropertyChanged(nameof(DisplayName)); NotifyCategoryChanged(); } }
        }

        /// <summary>
        /// Brugerens eget navn på enheden (fra DeviceNameService, slået op på MAC).
        /// Tomt hvis brugeren ikke har navngivet den.
        /// </summary>
        public string CustomName
        {
            get => _customName;
            set
            {
                if (!SetProperty(ref _customName, value)) return;
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(HasCustomName));
            }
        }

        /// <summary>
        /// Navnet der vises i Scan-tabellen.
        ///
        /// Rækkefølge: brugerens eget navn → mDNS/DNS-navn → NetBIOS-navn → IP.
        ///
        /// De tre automatiske kilder er ofte uenige, og ingen af dem har altid ret:
        /// en PTR-record kan pege på en tjeneste frem for maskinen, og et mDNS-navn
        /// kan være en generisk fabriksstreng hvor routerens DHCP-navn er det
        /// brugeren selv satte. Derfor står brugerens eget navn øverst — det er den
        /// eneste kilde der pr. definition er rigtig.
        ///
        /// Reverse-DNS svigter desuden helt for mange LAN-enheder, og uden
        /// NetBIOS-faldbacket stod kolonnen med en rå IP selvom NetBIOS-opslaget
        /// faktisk havde fundet et navn.
        /// </summary>
        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(_customName))
                    return _customName;
                if (!string.IsNullOrWhiteSpace(_hostName) && _hostName != _ipAddress)
                    return _hostName;
                if (!string.IsNullOrWhiteSpace(_netBiosName))
                    return _netBiosName;
                return _ipAddress;
            }
        }

        /// <summary>True når brugeren selv har navngivet enheden — bruges til at
        /// markere rækken i UI'et, så et manuelt navn kan skelnes fra et opslag.</summary>
        public bool HasCustomName => !string.IsNullOrWhiteSpace(_customName);
        public string IpAddress
        {
            get => _ipAddress;
            set
            {
                if (!SetProperty(ref _ipAddress, value)) return;
                OnPropertyChanged(nameof(IpSortValue));
                OnPropertyChanged(nameof(DisplayName));
            }
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
        private string _originalVendor = string.Empty;
        private string _netBiosName = string.Empty;
        private bool _isPort80Open;
        private bool _isPort443Open;
        private bool _isPort8080Open;
        private bool _isPort502Open;

        public string OsGuess
        {
            get => _osGuess;
            set { if (SetProperty(ref _osGuess, value)) NotifyCategoryChanged(); }
        }

        // Vendor/OriginalVendor sættes under berigelsen, altså EFTER at rækken er
        // bundet. IsAlias er afledt af dem, så begge skal notificere den — ellers
        // genberegnes alias-triggeren kun når DataGrid'et tilfældigvis re-realiserer
        // rækken (virtualisering), og markeringen bliver dermed vilkårlig.
        public string Vendor
        {
            get => _vendor;
            set { if (SetProperty(ref _vendor, value)) { OnPropertyChanged(nameof(IsAlias)); NotifyCategoryChanged(); } }
        }

        public string OriginalVendor
        {
            get => _originalVendor;
            set { if (SetProperty(ref _originalVendor, value)) OnPropertyChanged(nameof(IsAlias)); }
        }

        public bool IsAlias => !string.IsNullOrEmpty(_vendor) && _vendor != _originalVendor;
        public string NetBiosName
        {
            get => _netBiosName;
            set { if (SetProperty(ref _netBiosName, value)) { OnPropertyChanged(nameof(DisplayName)); NotifyCategoryChanged(); } }
        }

        public bool IsPort80Open
        {
            get => _isPort80Open;
            set { if (SetProperty(ref _isPort80Open, value)) { OnPropertyChanged(nameof(Port80Text)); OnPropertyChanged(nameof(Url80)); NotifyCategoryChanged(); } }
        }
        public bool IsPort443Open
        {
            get => _isPort443Open;
            set { if (SetProperty(ref _isPort443Open, value)) { OnPropertyChanged(nameof(Port443Text)); OnPropertyChanged(nameof(Url443)); NotifyCategoryChanged(); } }
        }
        public bool IsPort8080Open
        {
            get => _isPort8080Open;
            set { if (SetProperty(ref _isPort8080Open, value)) { OnPropertyChanged(nameof(Port8080Text)); OnPropertyChanged(nameof(Url8080)); NotifyCategoryChanged(); } }
        }
        public bool IsPort502Open
        {
            get => _isPort502Open;
            set { if (SetProperty(ref _isPort502Open, value)) { OnPropertyChanged(nameof(Port502Text)); OnPropertyChanged(nameof(OtherPorts)); NotifyCategoryChanged(); } }
        }

        /// <summary>Bedste-gæt enhedstype ud fra TTL/vendor/porte/navn — se DeviceFingerprint.
        /// Beregnes on-demand, ingen egen state; NotifyCategoryChanged kaldes fra alle
        /// settere klassificeringen læser fra, så DataGrid'et ikke viser et forældet ikon.</summary>
        public Utils.DeviceCategory Category => Utils.DeviceFingerprint.Classify(this);
        public string CategoryIcon => Utils.DeviceFingerprint.IconFor(Category);
        public string CategoryLabel => Utils.DeviceFingerprint.LabelFor(Category);

        private void NotifyCategoryChanged()
        {
            OnPropertyChanged(nameof(Category));
            OnPropertyChanged(nameof(CategoryIcon));
            OnPropertyChanged(nameof(CategoryLabel));
        }

        public string OtherPorts   => _isPort502Open  ? "502"  : string.Empty;

        public string Port80Text   => _isPort80Open   ? "80"   : string.Empty;
        public string Port443Text  => _isPort443Open  ? "443"  : string.Empty;
        public string Port8080Text => _isPort8080Open ? "8080" : string.Empty;
        public string Port502Text  => _isPort502Open  ? "502"  : string.Empty;

        public string Url80   => _isPort80Open   ? $"http://{IpAddress}"      : string.Empty;
        public string Url443  => _isPort443Open  ? $"https://{IpAddress}"     : string.Empty;
        public string Url8080 => _isPort8080Open ? $"http://{IpAddress}:8080" : string.Empty;

        public string LastSeenFormatted => _lastSeen == default ? "-" : _lastSeen.ToString("HH:mm:ss");

        private bool _port80Changed, _port443Changed, _port8080Changed, _port502Changed;
        private string _portChangeSummary = string.Empty;

        /// <summary>Port skiftede tilstand i det senest afsluttede scan af denne host — driver
        /// glødet kant på selve port-chippen i Scan-grid'et.</summary>
        public bool Port80Changed   { get => _port80Changed;   private set => SetProperty(ref _port80Changed, value); }
        public bool Port443Changed  { get => _port443Changed;  private set => SetProperty(ref _port443Changed, value); }
        public bool Port8080Changed { get => _port8080Changed; private set => SetProperty(ref _port8080Changed, value); }
        public bool Port502Changed  { get => _port502Changed;  private set => SetProperty(ref _port502Changed, value); }

        /// <summary>Mindst én overvåget port skiftede i det senest afsluttede scan — driver
        /// ændrings-badget i Scan-grid'et.</summary>
        public bool HasPortChange => _port80Changed || _port443Changed || _port8080Changed || _port502Changed;

        public string PortChangeSummary { get => _portChangeSummary; private set => SetProperty(ref _portChangeSummary, value); }

        /// <summary>
        /// Sættes af NetworkScanViewModel.RecordPortChanges hver gang denne host
        /// tjekkes i et scan — også når intet ændrede sig, så et badge fra et
        /// tidligere scan ikke bliver hængende ved en genscan uden ændringer.
        /// </summary>
        public void SetPortChangeFlags(bool port80, bool port443, bool port8080, bool port502)
        {
            Port80Changed   = port80;
            Port443Changed  = port443;
            Port8080Changed = port8080;
            Port502Changed  = port502;

            if (port80 || port443 || port8080 || port502)
            {
                var parts = new List<string>();
                if (port80)   parts.Add($"Port 80 {(_isPort80Open   ? "åbnede" : "lukkede")}");
                if (port443)  parts.Add($"Port 443 {(_isPort443Open  ? "åbnede" : "lukkede")}");
                if (port8080) parts.Add($"Port 8080 {(_isPort8080Open ? "åbnede" : "lukkede")}");
                if (port502)  parts.Add($"Port 502 {(_isPort502Open  ? "åbnede" : "lukkede")}");
                PortChangeSummary = string.Join(", ", parts);
            }
            else
            {
                PortChangeSummary = string.Empty;
            }

            OnPropertyChanged(nameof(HasPortChange));
        }

        /// <summary>
        /// Fletter et nyere observationsresultat ind i denne (bundne) instans.
        ///
        /// Denne logik fandtes tidligere tre steder — FlushUiQueue, PingSingleAsync og
        /// UpdateHostInUI — med indbyrdes forskellige regler: kun én af dem beskyttede
        /// HostName mod at blive overskrevet med IP'en, og én satte IsReachable
        /// ubetinget, så et forsinket berigelsessvar kunne markere en levende host som
        /// offline. Én implementering fjerner den divergens.
        ///
        /// Grundregel: tom/ukendt data overskriver aldrig data vi allerede har, og
        /// IsReachable kan kun løftes til true — aldrig sænkes af et sent svar.
        /// </summary>
        /// <param name="authoritative">
        /// Sæt true når resultatet er en frisk, komplet måling af netop denne host
        /// (fx et eksplicit gen-ping), hvor "ikke længere online" er et gyldigt svar
        /// der SKAL slå igennem. Under et sweep er den false: der ankommer
        /// berigelsessvar ud af rækkefølge, og et sent svar må ikke markere en host
        /// offline blot fordi berigelsen ikke selv målte tilgængelighed.
        /// </param>
        /// <param name="portsAuthoritative">
        /// Styrer SÆRSKILT om porte erstattes (frisk måling vinder) eller kun kan
        /// tændes (kumulativ OR) — uafhængigt af <paramref name="authoritative"/>.
        /// Udelades den, følger porte samme regel som <paramref name="authoritative"/>
        /// (bagudkompatibelt). NetworkScanViewModel's enrichment-fase tjekker altid
        /// alle fire porte samlet i ÉT komplet kald pr. host, så det resultat er
        /// autoritativt for porte selvom hostens IsReachable ikke er det — ellers
        /// kunne en port der reelt lukkedes (fx en bruger der lukker en tjeneste)
        /// aldrig vises som lukket igen ved et flettet scan/auto-opdatering, kun ved
        /// et eksplicit gen-ping af netop den host.
        /// </param>
        public void MergeFrom(HostInfo other, bool authoritative = false, bool? portsAuthoritative = null)
        {
            var portsAuth = portsAuthoritative ?? authoritative;
            if (ReferenceEquals(this, other)) return;

            // Et hostname der blot gentager IP'en er en placeholder, ikke et navn.
            if (!string.IsNullOrEmpty(other.HostName) && other.HostName != other.IpAddress)
                HostName = other.HostName;

            if (other.ResponseTime > 0)               ResponseTime   = other.ResponseTime;
            if (!string.IsNullOrEmpty(other.Status))  Status         = other.Status;
            if (!string.IsNullOrEmpty(other.OsGuess)) OsGuess        = other.OsGuess;

            // Tilgængelighed: kun en autoritativ måling må sænke den igen, ellers
            // ville statusteksten kunne sige "Timeout" mens prikken blev grøn.
            if (authoritative)          IsReachable = other.IsReachable;
            else if (other.IsReachable) IsReachable = true;
            if (!string.IsNullOrEmpty(other.MacAddress))     MacAddress     = other.MacAddress;
            if (!string.IsNullOrEmpty(other.Vendor))         Vendor         = other.Vendor;
            if (!string.IsNullOrEmpty(other.OriginalVendor)) OriginalVendor = other.OriginalVendor;
            if (!string.IsNullOrEmpty(other.NetBiosName))    NetBiosName    = other.NetBiosName;
            if (!string.IsNullOrEmpty(other.CustomName))     CustomName     = other.CustomName;

            // Åbne porte er kumulative inden for et scan: to faser tjekker samme host,
            // og den sidste må ikke nulstille hvad den første fandt. En autoritativ
            // måling har tjekket alle fire porte og må gerne lukke dem igen.
            if (portsAuth)
            {
                IsPort80Open   = other.IsPort80Open;
                IsPort443Open  = other.IsPort443Open;
                IsPort8080Open = other.IsPort8080Open;
                IsPort502Open  = other.IsPort502Open;
            }
            else
            {
                if (other.IsPort80Open)   IsPort80Open   = true;
                if (other.IsPort443Open)  IsPort443Open  = true;
                if (other.IsPort8080Open) IsPort8080Open = true;
                if (other.IsPort502Open)  IsPort502Open  = true;
            }

            LastSeen = other.LastSeen;
        }
    }
}
