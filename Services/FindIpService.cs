using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace M1Scan.Services
{
    /// <summary>Én fanget IPv4-pakke, reduceret til det Find IP-fanen har brug for.</summary>
    public readonly struct CapturedPacket
    {
        public CapturedPacket(string sourceIp, string destIp, byte protocol, int length)
        {
            SourceIp = sourceIp;
            DestIp = destIp;
            Protocol = protocol;
            Length = length;
        }

        public string SourceIp { get; }
        public string DestIp { get; }
        public byte Protocol { get; }
        public int Length { get; }

        /// <summary>Bedste-gæt protokolnavn ud fra IP-protokolnummer + destinations-heuristik.</summary>
        public string ProtocolName
        {
            get
            {
                return Protocol switch
                {
                    1 => "ICMP",
                    2 => "IGMP",
                    6 => "TCP",
                    17 => "UDP",
                    _ => $"IP/{Protocol}"
                };
            }
        }
    }

    public interface IFindIpService
    {
        bool IsCapturing { get; }

        /// <summary>Starter passiv promiscuous capture på den angivne lokale interface-IP.
        /// Hver modtaget IPv4-pakke rejser <see cref="PacketCaptured"/> (på en baggrundstråd).</summary>
        void Start(string localInterfaceIp);
        void Stop();

        event Action<CapturedPacket>? PacketCaptured;

        /// <summary>Rejses hvis captureløkken stopper af sig selv (fx NIC deaktiveret eller
        /// kabel trukket midt i capture) — så UI'en kan nulstille sin "kører"-tilstand.</summary>
        event Action? CaptureStopped;
    }

    /// <summary>
    /// Passiv netværks-sniffer bygget på en Windows raw-socket i promiscuous mode
    /// (SIO_RCVALL). Fanger alle IPv4-pakker som NIC'et modtager — inkl. broadcast/
    /// multicast fra enheder på samme L2-segment, også dem uden for det lokale subnet.
    /// Kræver administrator-rettigheder (allerede et krav for M1Scan).
    ///
    /// Bemærk: en raw IP-socket ser Ethernet-headeren *ikke*, så kilde-MAC hentes ikke
    /// herfra — den resolves separat via ARP (SendARP virker på tværs af subnets på
    /// samme segment). Uden Npcap begrænser switchen desuden unicast, så det er primært
    /// broadcast/multicast "wakeup calls" der fanges — hvilket er præcis det vi jager.
    /// </summary>
    public class FindIpService : IFindIpService, IDisposable
    {
        // SIO_RCVALL: modtag alle pakker, ikke kun dem adresseret til denne socket.
        private const int RCVALL_ON = 1;

        private Socket? _socket;
        private CancellationTokenSource? _cts;
        private readonly object _lock = new();

        public bool IsCapturing { get; private set; }

        public event Action<CapturedPacket>? PacketCaptured;
        public event Action? CaptureStopped;

        public void Start(string localInterfaceIp)
        {
            lock (_lock)
            {
                if (IsCapturing) return;

                if (!IPAddress.TryParse(localInterfaceIp, out var ip))
                    throw new ArgumentException($"Ugyldig interface-IP: {localInterfaceIp}");

                Socket? socket = null;
                try
                {
                    socket = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.IP);
                    socket.Bind(new IPEndPoint(ip, 0));
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.HeaderIncluded, true);

                    // Slå promiscuous receive til.
                    byte[] inValue = BitConverter.GetBytes(RCVALL_ON);
                    byte[] outValue = new byte[4];
                    socket.IOControl(IOControlCode.ReceiveAll, inValue, outValue);
                }
                catch
                {
                    // Bind/IOControl kan fejle (ikke-admin, virtuel adapter) — undgå at lække socket-handlet.
                    socket?.Dispose();
                    throw;
                }

                _socket = socket;
                _cts = new CancellationTokenSource();
                IsCapturing = true;

                // Token'et hentes HER, ikke inde i lambdaen. Blev _cts.Token først
                // læst på trådpuljen, kunne et Stop() imellem have sat _cts = null,
                // og lambdaen ville kaste NullReferenceException i en task ingen
                // observerer.
                var token = _cts.Token;
                _ = Task.Run(() => ReceiveLoopAsync(socket, token));
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                Teardown();
            }
        }

        // Rydder op og lukker socket'en. Skal kaldes under _lock. Returnerer false hvis
        // vi ikke var i gang (så kaldere kan undlade at rejse events).
        private bool Teardown()
        {
            if (!IsCapturing) return false;
            IsCapturing = false;

            try { _cts?.Cancel(); } catch { }

            try
            {
                // Slå promiscuous fra igen inden vi lukker.
                byte[] inValue = BitConverter.GetBytes(0);
                _socket?.IOControl(IOControlCode.ReceiveAll, inValue, new byte[4]);
            }
            catch { }

            try { _socket?.Close(); } catch { }
            _socket?.Dispose();
            _socket = null;

            _cts?.Dispose();
            _cts = null;
            return true;
        }

        private async Task ReceiveLoopAsync(Socket socket, CancellationToken ct)
        {
            var buffer = new byte[65535];
            bool unexpected = false;
            while (!ct.IsCancellationRequested)
            {
                int received;
                try
                {
                    received = await socket.ReceiveAsync(buffer, SocketFlags.None, ct);
                }
                catch (OperationCanceledException) { break; }   // deliberat Stop()
                catch (ObjectDisposedException) { break; }       // socket lukket af Stop()
                catch (SocketException) { unexpected = true; break; } // NIC væk / kabel trukket
                catch { continue; }

                if (received < 20) continue; // mindre end en IPv4-header

                var packet = ParseIpv4(buffer, received);
                if (packet.HasValue)
                {
                    try { PacketCaptured?.Invoke(packet.Value); }
                    catch { /* en subscriber-fejl må ikke dræbe captureløkken */ }
                }
            }

            // Døde løkken uventet, så ryd op og fortæl UI'en så den ikke hænger i "kører".
            if (unexpected)
            {
                bool wasRunning;
                lock (_lock) { wasRunning = Teardown(); }
                if (wasRunning) CaptureStopped?.Invoke();
            }
        }

        // Udtrækker kilde/dest-IP, protokol og længde fra en rå IPv4-header.
        private static CapturedPacket? ParseIpv4(byte[] b, int length)
        {
            int version = b[0] >> 4;
            if (version != 4) return null;

            byte protocol = b[9];
            string src = $"{b[12]}.{b[13]}.{b[14]}.{b[15]}";
            string dst = $"{b[16]}.{b[17]}.{b[18]}.{b[19]}";
            return new CapturedPacket(src, dst, protocol, length);
        }

        public void Dispose() => Stop();
    }
}
