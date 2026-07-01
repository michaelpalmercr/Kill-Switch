using System.Net;

namespace KillSwitch;

public enum CaptureEngine { RawSocket, Npcap }

/// <summary>A packet as seen by a capture backend, before process attribution.</summary>
public readonly struct RawPacket
{
    public readonly DateTime Time;
    public readonly bool Outbound;
    public readonly string Proto;     // "TCP", "UDP", "ICMP", "IPv6", protocol number, ...
    public readonly IPAddress Local;
    public readonly int LocalPort;
    public readonly IPAddress Remote;
    public readonly int RemotePort;
    public readonly int Length;
    /// <summary>L4 payload, attached only for inspectable traffic (DNS/TLS-ClientHello/HTTP); else null.</summary>
    public readonly byte[]? Payload;

    public RawPacket(DateTime time, bool outbound, string proto, IPAddress local, int localPort,
                     IPAddress remote, int remotePort, int length, byte[]? payload = null)
    {
        Time = time; Outbound = outbound; Proto = proto;
        Local = local; LocalPort = localPort; Remote = remote; RemotePort = remotePort; Length = length;
        Payload = payload;
    }
}

/// <summary>A packet after the monitor has attributed it to a process. Shown in the live feed.</summary>
public readonly struct PacketRecord
{
    public readonly DateTime Time;
    public readonly bool Outbound;
    public readonly string Proto;
    public readonly IPAddress Local;
    public readonly int LocalPort;
    public readonly IPAddress Remote;
    public readonly int RemotePort;
    public readonly int Length;
    public readonly int Pid;
    public readonly string Process;

    public PacketRecord(in RawPacket p, int pid, string process)
    {
        Time = p.Time; Outbound = p.Outbound; Proto = p.Proto;
        Local = p.Local; LocalPort = p.LocalPort; Remote = p.Remote; RemotePort = p.RemotePort;
        Length = p.Length; Pid = pid; Process = process;
    }
}

/// <summary>Rolling per-process traffic stats.</summary>
public sealed class AppUsage
{
    public string Name = "";
    public string? ExePath;
    public int Pid;

    public long BytesIn, BytesOut;       // cumulative since monitor start
    public long PacketsIn, PacketsOut;
    public double RateIn, RateOut;       // bytes/sec, computed each sample
    public int Connections;
    public DateTime LastSeen;
    public bool Blocked;                 // a per-app firewall block rule is active

    /// <summary>Distinct remote IPs this process has talked to (capped). Used for block-by-destination.</summary>
    public HashSet<string> Remotes = new();

    /// <summary>Distinct domains/hosts this process has contacted (from DNS + TLS SNI + HTTP Host).</summary>
    public HashSet<string> Domains = new();

    /// <summary>Per-destination byte counters, keyed by remote IP. Reveals which site/IP is
    /// receiving our uploads (BytesOut) so it can be blocked individually.</summary>
    public Dictionary<string, DestUsage> Dests = new();

    // internal, for rate calc
    internal long _prevIn, _prevOut;

    public long TotalBytes => BytesIn + BytesOut;
}

/// <summary>Machine-wide traffic to a single remote IP, aggregated across every app.
/// Used by the Connections view: who is receiving our uploads and who is reaching into the PC.</summary>
public sealed class RemoteEndpoint
{
    public string Ip = "";
    public string? Host;                 // hostname from DNS / TLS SNI / HTTP Host, if known
    public string? Owner;                // org/company from WHOIS/RDAP, filled on demand
    public long BytesIn;                 // received FROM this IP (it reaching into us)
    public long BytesOut;                // SENT TO this IP (our data going out)
    public long Packets;
    public DateTime LastSeen;
    public SortedSet<string> Apps = new(StringComparer.OrdinalIgnoreCase); // apps talking to it

    public long TotalBytes => BytesIn + BytesOut;
    public bool HasOutbound => BytesOut > 0;
    public bool HasInbound => BytesIn > 0;
    /// <summary>Reaching into the PC without us sending anything back to it (unsolicited-looking).</summary>
    public bool InboundOnly => BytesIn > 0 && BytesOut == 0;
    public string Label => string.IsNullOrEmpty(Host) ? Ip : Host!;
    public string AppsLabel => Apps.Count == 0 ? "" : string.Join(", ", Apps);
}

/// <summary>Traffic to a single remote destination (one IP), for one app.</summary>
public sealed class DestUsage
{
    public string Ip = "";
    public string? Host;             // resolved hostname (DNS / TLS SNI / HTTP Host), if known
    public long BytesIn;             // received from this destination
    public long BytesOut;            // SENT to this destination — "what it's receiving from us"
    public long Packets;
    public DateTime LastSeen;

    public long TotalBytes => BytesIn + BytesOut;
    /// <summary>Best display label: hostname if we learned one, else the raw IP.</summary>
    public string Label => string.IsNullOrEmpty(Host) ? Ip : Host!;
}
