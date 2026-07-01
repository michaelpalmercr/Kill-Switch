using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using SharpPcap;

namespace KillSwitch;

/// <summary>
/// Npcap-driver capture backend (full link-layer) via SharpPcap + PacketDotNet.
/// Only used when the Npcap runtime is installed; otherwise the monitor falls back to raw sockets.
/// </summary>
public sealed class NpcapCapture : IPacketCapture
{
    public string Name => "Npcap (Wireshark driver)";
    public event Action<RawPacket>? Packet;

    private readonly List<ILiveDevice> _devices = new();
    private HashSet<string> _localAddrs = new();
    private volatile bool _running;

    /// <summary>True if the Npcap (or legacy WinPcap) runtime is installed.</summary>
    public static bool IsInstalled()
    {
        string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return File.Exists(Path.Combine(sys, "Npcap", "wpcap.dll"))
            || File.Exists(Path.Combine(sys, "wpcap.dll"));
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _localAddrs = LocalAddrs();

        foreach (var dev in CaptureDeviceList.Instance)
        {
            try
            {
                dev.OnPacketArrival += OnArrival;
                dev.Open(new DeviceConfiguration { Mode = DeviceModes.Promiscuous, ReadTimeout = 1000 });
                dev.StartCapture();
                _devices.Add(dev);
            }
            catch
            {
                try { dev.OnPacketArrival -= OnArrival; } catch { }
                try { dev.Close(); } catch { }
            }
        }
        if (_devices.Count == 0) throw new InvalidOperationException("Npcap: no capture devices could be opened.");
    }

    public void Stop()
    {
        _running = false;
        foreach (var d in _devices)
        {
            try { d.StopCapture(); } catch { }
            try { d.OnPacketArrival -= OnArrival; } catch { }
            try { d.Close(); } catch { }
        }
        _devices.Clear();
    }

    public void Dispose() => Stop();

    private void OnArrival(object sender, PacketCapture e)
    {
        if (!_running) return;
        try
        {
            var raw = e.GetPacket();
            var parsed = PacketDotNet.Packet.ParsePacket(raw.LinkLayerType, raw.Data);
            var ip = parsed.Extract<PacketDotNet.IPPacket>();
            if (ip == null) return;

            IPAddress src = ip.SourceAddress, dst = ip.DestinationAddress;
            int srcPort = 0, dstPort = 0;
            string label;
            byte[]? payload = null;

            var tcp = parsed.Extract<PacketDotNet.TcpPacket>();
            if (tcp != null)
            {
                srcPort = tcp.SourcePort; dstPort = tcp.DestinationPort; label = "TCP";
                payload = ExtractPayload(tcp.PayloadData, srcPort, dstPort);
            }
            else
            {
                var udp = parsed.Extract<PacketDotNet.UdpPacket>();
                if (udp != null)
                {
                    srcPort = udp.SourcePort; dstPort = udp.DestinationPort; label = "UDP";
                    payload = ExtractPayload(udp.PayloadData, srcPort, dstPort);
                }
                else label = ip.Protocol.ToString().ToUpperInvariant();
            }

            bool outbound = _localAddrs.Contains(src.ToString());
            var local = outbound ? src : dst;
            var localPort = outbound ? srcPort : dstPort;
            var remote = outbound ? dst : src;
            var remotePort = outbound ? dstPort : srcPort;
            int length = raw.Data.Length;

            Packet?.Invoke(new RawPacket(DateTime.Now, outbound, label, local, localPort, remote, remotePort, length, payload));
        }
        catch { /* malformed frame — ignore */ }
    }

    private static byte[]? ExtractPayload(byte[]? data, int srcPort, int dstPort)
    {
        if (data == null || data.Length == 0) return null;
        bool dns = srcPort == 53 || dstPort == 53;
        bool tls = srcPort == 443 || dstPort == 443;
        bool http = srcPort == 80 || dstPort == 80;
        if (!dns && !tls && !http) return null;
        if (tls && data[0] != 0x16) return null;
        if (http && !Inspect.LooksLikeHttp(data, 0)) return null;

        int len = Math.Min(data.Length, 2048);
        var slice = new byte[len];
        Array.Copy(data, 0, slice, 0, len);
        return slice;
    }

    private static HashSet<string> LocalAddrs()
    {
        var set = new HashSet<string>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                set.Add(ua.Address.ToString());
        }
        return set;
    }
}
