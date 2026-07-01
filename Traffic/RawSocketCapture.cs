using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace KillSwitch;

/// <summary>
/// Packet capture using Windows raw sockets + SIO_RCVALL — no driver required, admin only.
/// Binds one raw socket per local interface address and receives every IP packet it carries.
/// IPv4 is fully supported; IPv6 is best-effort (RCVALL isn't always permitted) and skipped on error.
/// </summary>
public sealed class RawSocketCapture : IPacketCapture
{
    public string Name => "Raw sockets (no driver)";

    public event Action<RawPacket>? Packet;

    private readonly List<Socket> _sockets = new();
    private readonly List<Thread> _threads = new();
    private HashSet<string> _localAddrs = new();
    private volatile bool _running;

    public void Start()
    {
        if (_running) return;
        _running = true;

        var addresses = LocalUnicastAddresses();
        _localAddrs = addresses.Select(a => a.ToString()).ToHashSet();

        foreach (var addr in addresses)
        {
            try
            {
                var family = addr.AddressFamily;
                var proto = family == AddressFamily.InterNetwork ? ProtocolType.IP : ProtocolType.IPv6;
                var s = new Socket(family, SocketType.Raw, proto);
                s.Bind(new IPEndPoint(addr, 0));
                // SIO_RCVALL = receive all packets on this interface.
                s.IOControl(IOControlCode.ReceiveAll, BitConverter.GetBytes(1), null);
                s.ReceiveBufferSize = 1 << 20;
                _sockets.Add(s);

                var t = new Thread(() => ReceiveLoop(s)) { IsBackground = true, Name = "rawcap-" + addr };
                _threads.Add(t);
                t.Start();
            }
            catch
            {
                // IPv6 RCVALL or a transient bind failure — skip this address.
            }
        }
    }

    public void Stop()
    {
        _running = false;
        foreach (var s in _sockets)
        {
            try { s.Close(); } catch { }
            try { s.Dispose(); } catch { }
        }
        _sockets.Clear();
        _threads.Clear();
    }

    public void Dispose() => Stop();

    private static List<IPAddress> LocalUnicastAddresses()
    {
        var result = new List<IPAddress>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                var a = ua.Address;
                if (IPAddress.IsLoopback(a)) continue;
                if (a.AddressFamily == AddressFamily.InterNetworkV6 && (a.IsIPv6LinkLocal || a.IsIPv6Multicast)) continue;
                if (!result.Contains(a)) result.Add(a);
            }
        }
        return result;
    }

    private void ReceiveLoop(Socket s)
    {
        var buf = new byte[65535];
        while (_running)
        {
            int n;
            try { n = s.Receive(buf); }
            catch { break; } // socket closed on Stop()
            if (n <= 0) continue;

            try
            {
                var pkt = buf[0] >> 4 == 6 ? ParseIPv6(buf, n) : ParseIPv4(buf, n);
                if (pkt is { } p) Packet?.Invoke(p);
            }
            catch { /* malformed packet — ignore */ }
        }
    }

    private RawPacket? ParseIPv4(byte[] b, int n)
    {
        if (n < 20) return null;
        int ihl = (b[0] & 0x0F) * 4;
        if (ihl < 20 || n < ihl) return null;

        byte proto = b[9];
        int totalLen = (b[2] << 8) | b[3];
        int length = totalLen > 0 ? totalLen : n;

        var src = new IPAddress(new[] { b[12], b[13], b[14], b[15] });
        var dst = new IPAddress(new[] { b[16], b[17], b[18], b[19] });

        int srcPort = 0, dstPort = 0;
        byte[]? payload = null;
        if ((proto == 6 || proto == 17) && n >= ihl + 4)
        {
            srcPort = (b[ihl] << 8) | b[ihl + 1];
            dstPort = (b[ihl + 2] << 8) | b[ihl + 3];
            int dataOff = proto == 6
                ? (n >= ihl + 13 ? ihl + (((b[ihl + 12] >> 4) & 0xF) * 4) : -1)
                : ihl + 8;
            payload = ExtractPayload(b, n, dataOff, srcPort, dstPort);
        }
        return Build(proto, src, srcPort, dst, dstPort, length, payload);
    }

    private static byte[]? ExtractPayload(byte[] b, int n, int dataOff, int srcPort, int dstPort)
    {
        if (dataOff <= 0 || dataOff >= n) return null;
        bool dns = srcPort == 53 || dstPort == 53;
        bool tls = srcPort == 443 || dstPort == 443;
        bool http = srcPort == 80 || dstPort == 80;
        if (!dns && !tls && !http) return null;
        if (tls && b[dataOff] != 0x16) return null;        // only the TLS handshake record (ClientHello carries SNI)
        if (http && !Inspect.LooksLikeHttp(b, dataOff)) return null;

        int len = Math.Min(n - dataOff, 2048);             // SNI / DNS / HTTP headers fit comfortably
        var slice = new byte[len];
        Array.Copy(b, dataOff, slice, 0, len);
        return slice;
    }

    private RawPacket? ParseIPv6(byte[] b, int n)
    {
        if (n < 40) return null;
        byte next = b[6];
        int payloadLen = (b[4] << 8) | b[5];
        int length = 40 + payloadLen;

        var src = new IPAddress(b.AsSpan(8, 16).ToArray());
        var dst = new IPAddress(b.AsSpan(24, 16).ToArray());

        int srcPort = 0, dstPort = 0;
        byte[]? payload = null;
        if ((next == 6 || next == 17) && n >= 44)
        {
            srcPort = (b[40] << 8) | b[41];
            dstPort = (b[42] << 8) | b[43];
            int dataOff = next == 6 ? (n >= 53 ? 40 + (((b[52] >> 4) & 0xF) * 4) : -1) : 48;
            payload = ExtractPayload(b, n, dataOff, srcPort, dstPort);
        }
        return Build(next, src, srcPort, dst, dstPort, length, payload);
    }

    private RawPacket Build(byte proto, IPAddress src, int srcPort, IPAddress dst, int dstPort, int length, byte[]? payload)
    {
        bool outbound = _localAddrs.Contains(src.ToString());
        var local = outbound ? src : dst;
        var localPort = outbound ? srcPort : dstPort;
        var remote = outbound ? dst : src;
        var remotePort = outbound ? dstPort : srcPort;

        string label = proto switch
        {
            6 => "TCP",
            17 => "UDP",
            1 => "ICMP",
            58 => "ICMPv6",
            2 => "IGMP",
            _ => "IP/" + proto,
        };
        return new RawPacket(DateTime.Now, outbound, label, local, localPort, remote, remotePort, length, payload);
    }
}
