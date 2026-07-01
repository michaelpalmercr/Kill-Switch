using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace KillSwitch;

public enum L4Protocol { Tcp, Udp }

/// <summary>One row of the OS connection/endpoint table, with the owning process id.</summary>
public sealed class ConnectionInfo
{
    public L4Protocol Protocol;
    public IPAddress Local = IPAddress.Any;
    public int LocalPort;
    public IPAddress Remote = IPAddress.Any;
    public int RemotePort;
    public string State = "";
    public int Pid;

    public bool IsIPv6 => Local.AddressFamily == AddressFamily.InterNetworkV6;
    public ulong Key => MakeKey(Protocol, LocalPort);

    /// <summary>Attribution key: protocol + local port is enough to map a packet to its owning process.</summary>
    public static ulong MakeKey(L4Protocol p, int localPort) => ((ulong)p << 32) | (uint)localPort;
}

/// <summary>
/// Reads the live TCP/UDP tables (IPv4 + IPv6) via iphlpapi, including the owning PID,
/// using GetExtendedTcpTable / GetExtendedUdpTable. Cheap enough to poll a few times a second.
/// </summary>
public static class IpHelper
{
    private const int AF_INET = 2;
    private const int AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const int UDP_TABLE_OWNER_PID = 1;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTable, ref int size, bool order, int af, int cls, int reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr pTable, ref int size, bool order, int af, int cls, int reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRow4 { public uint state, local, localPort, remote, remotePort, pid; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRow6
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] local;
        public uint localScope, localPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] remote;
        public uint remoteScope, remotePort, state, pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UdpRow4 { public uint local, localPort, pid; }

    [StructLayout(LayoutKind.Sequential)]
    private struct UdpRow6
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] local;
        public uint localScope, localPort, pid;
    }

    private static readonly string[] TcpStates =
        { "", "CLOSED", "LISTEN", "SYN-SENT", "SYN-RCVD", "ESTAB", "FIN-WAIT1",
          "FIN-WAIT2", "CLOSE-WAIT", "CLOSING", "LAST-ACK", "TIME-WAIT", "DELETE-TCB" };

    private static int Ntohs(uint p) => (int)(((p & 0xFF) << 8) | ((p >> 8) & 0xFF));

    public static List<ConnectionInfo> GetConnections()
    {
        var list = new List<ConnectionInfo>(256);
        try { ReadTcp(AF_INET, list); } catch { }
        try { ReadTcp(AF_INET6, list); } catch { }
        try { ReadUdp(AF_INET, list); } catch { }
        try { ReadUdp(AF_INET6, list); } catch { }
        return list;
    }

    /// <summary>Map of (protocol, localPort) -> owning PID for fast packet attribution.</summary>
    public static Dictionary<ulong, int> BuildPortMap(List<ConnectionInfo> conns)
    {
        var map = new Dictionary<ulong, int>(conns.Count);
        foreach (var c in conns) map[c.Key] = c.Pid; // last writer wins; fine for attribution
        return map;
    }

    private static void ReadTcp(int af, List<ConnectionInfo> list)
    {
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, true, af, TCP_TABLE_OWNER_PID_ALL, 0);
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buf, ref size, true, af, TCP_TABLE_OWNER_PID_ALL, 0) != 0) return;
            int count = Marshal.ReadInt32(buf);
            IntPtr ptr = buf + 4;
            if (af == AF_INET)
            {
                int rs = Marshal.SizeOf<TcpRow4>();
                for (int i = 0; i < count; i++, ptr += rs)
                {
                    var r = Marshal.PtrToStructure<TcpRow4>(ptr);
                    list.Add(new ConnectionInfo
                    {
                        Protocol = L4Protocol.Tcp,
                        Local = new IPAddress(r.local),
                        LocalPort = Ntohs(r.localPort),
                        Remote = new IPAddress(r.remote),
                        RemotePort = Ntohs(r.remotePort),
                        State = r.state < TcpStates.Length ? TcpStates[r.state] : r.state.ToString(),
                        Pid = (int)r.pid,
                    });
                }
            }
            else
            {
                int rs = Marshal.SizeOf<TcpRow6>();
                for (int i = 0; i < count; i++, ptr += rs)
                {
                    var r = Marshal.PtrToStructure<TcpRow6>(ptr);
                    list.Add(new ConnectionInfo
                    {
                        Protocol = L4Protocol.Tcp,
                        Local = new IPAddress(r.local),
                        LocalPort = Ntohs(r.localPort),
                        Remote = new IPAddress(r.remote),
                        RemotePort = Ntohs(r.remotePort),
                        State = r.state < TcpStates.Length ? TcpStates[r.state] : r.state.ToString(),
                        Pid = (int)r.pid,
                    });
                }
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static void ReadUdp(int af, List<ConnectionInfo> list)
    {
        int size = 0;
        GetExtendedUdpTable(IntPtr.Zero, ref size, true, af, UDP_TABLE_OWNER_PID, 0);
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedUdpTable(buf, ref size, true, af, UDP_TABLE_OWNER_PID, 0) != 0) return;
            int count = Marshal.ReadInt32(buf);
            IntPtr ptr = buf + 4;
            if (af == AF_INET)
            {
                int rs = Marshal.SizeOf<UdpRow4>();
                for (int i = 0; i < count; i++, ptr += rs)
                {
                    var r = Marshal.PtrToStructure<UdpRow4>(ptr);
                    list.Add(new ConnectionInfo
                    {
                        Protocol = L4Protocol.Udp,
                        Local = new IPAddress(r.local),
                        LocalPort = Ntohs(r.localPort),
                        Remote = IPAddress.Any,
                        RemotePort = 0,
                        State = "",
                        Pid = (int)r.pid,
                    });
                }
            }
            else
            {
                int rs = Marshal.SizeOf<UdpRow6>();
                for (int i = 0; i < count; i++, ptr += rs)
                {
                    var r = Marshal.PtrToStructure<UdpRow6>(ptr);
                    list.Add(new ConnectionInfo
                    {
                        Protocol = L4Protocol.Udp,
                        Local = new IPAddress(r.local),
                        LocalPort = Ntohs(r.localPort),
                        Remote = IPAddress.IPv6Any,
                        RemotePort = 0,
                        State = "",
                        Pid = (int)r.pid,
                    });
                }
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
