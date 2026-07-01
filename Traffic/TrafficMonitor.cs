using System.Diagnostics;

namespace KillSwitch;

/// <summary>
/// Owns a capture backend, attributes each packet to a process (via the IP Helper port→PID map),
/// and maintains per-app cumulative usage, live rates, and a rolling recent-packet buffer.
/// Thread-safe; the UI polls the snapshot methods on a timer.
/// </summary>
public sealed class TrafficMonitor : IDisposable
{
    private const int RecentCap = 3000;

    private readonly object _lock = new();
    private readonly Dictionary<int, AppUsage> _apps = new();
    private readonly Queue<PacketRecord> _recent = new();
    private Dictionary<ulong, int> _portMap = new();
    private readonly Dictionary<string, string> _dns = new(StringComparer.OrdinalIgnoreCase); // ip -> hostname

    private IPacketCapture? _capture;
    private System.Threading.Timer? _refresh;
    private readonly Stopwatch _sinceRefresh = new();

    public bool Running { get; private set; }
    public string? LastError { get; private set; }
    public string EngineName => _capture?.Name ?? "(stopped)";
    public CaptureEngine Engine { get; private set; } = CaptureEngine.RawSocket;

    public void Start(CaptureEngine engine)
    {
        Stop();
        Engine = engine;
        LastError = null;
        try
        {
            _capture = CreateCapture(engine);
            _capture.Packet += OnPacket;
            _capture.Start();
            Running = true;
            _sinceRefresh.Restart();
            _refresh = new System.Threading.Timer(_ => RefreshTick(), null, 1000, 1000);
        }
        catch (Exception ex)
        {
            // If Npcap failed to start, fall back to raw sockets so capture still works.
            if (engine == CaptureEngine.Npcap)
            {
                try
                {
                    if (_capture != null) { _capture.Packet -= OnPacket; try { _capture.Dispose(); } catch { } }
                    _capture = new RawSocketCapture();
                    _capture.Packet += OnPacket;
                    _capture.Start();
                    Running = true;
                    _sinceRefresh.Restart();
                    _refresh = new System.Threading.Timer(_ => RefreshTick(), null, 1000, 1000);
                    return;
                }
                catch { }
            }
            LastError = ex.Message;
            Running = false;
        }
    }

    private static IPacketCapture CreateCapture(CaptureEngine engine)
    {
        if (engine == CaptureEngine.Npcap && NpcapCapture.IsInstalled())
            return new NpcapCapture();
        // Raw sockets, or Npcap requested but not installed (caller is warned separately).
        return new RawSocketCapture();
    }

    public void Stop()
    {
        Running = false;
        _refresh?.Dispose();
        _refresh = null;
        if (_capture != null)
        {
            _capture.Packet -= OnPacket;
            try { _capture.Dispose(); } catch { }
            _capture = null;
        }
    }

    public void Dispose() => Stop();

    private void OnPacket(RawPacket p)
    {
        int pid = 0;
        if ((p.Proto == "TCP" || p.Proto == "UDP") && p.LocalPort > 0)
        {
            var key = ConnectionInfo.MakeKey(p.Proto == "TCP" ? L4Protocol.Tcp : L4Protocol.Udp, p.LocalPort);
            lock (_lock) { _portMap.TryGetValue(key, out pid); }
        }

        var (name, path) = ProcessResolver.Resolve(pid);

        lock (_lock)
        {
            if (!_apps.TryGetValue(pid, out var app))
            {
                app = new AppUsage { Pid = pid, Name = name, ExePath = path };
                _apps[pid] = app;
            }
            app.Name = name;
            app.ExePath ??= path;
            app.LastSeen = p.Time;
            if (p.Outbound) { app.BytesOut += p.Length; app.PacketsOut++; }
            else { app.BytesIn += p.Length; app.PacketsIn++; }

            if (p.RemotePort > 0)
            {
                var rip = p.Remote.ToString();
                if (rip != "0.0.0.0" && rip != "::")
                {
                    if (app.Remotes.Count < 200) app.Remotes.Add(rip);

                    // Per-destination byte tally (so we can show who receives our uploads).
                    if (!app.Dests.TryGetValue(rip, out var d) && app.Dests.Count < 500)
                    {
                        d = new DestUsage { Ip = rip };
                        app.Dests[rip] = d;
                    }
                    if (d != null)
                    {
                        if (p.Outbound) d.BytesOut += p.Length; else d.BytesIn += p.Length;
                        d.Packets++;
                        d.LastSeen = p.Time;
                    }
                }
            }

            if (p.Payload != null) InspectPayload(p, app);

            _recent.Enqueue(new PacketRecord(in p, pid, name));
            while (_recent.Count > RecentCap) _recent.Dequeue();
        }
    }

    // Caller holds _lock.
    private void InspectPayload(RawPacket p, AppUsage app)
    {
        var payload = p.Payload!;
        try
        {
            if (p.RemotePort == 53 || p.LocalPort == 53)
            {
                var dns = Inspect.ParseDns(payload);
                foreach (var (ip, host) in dns.Answers)
                    if (host.Length > 0) _dns[ip.ToString()] = host;
                if (!string.IsNullOrEmpty(dns.Query) && app.Domains.Count < 100) app.Domains.Add(dns.Query!);
            }
            else if (p.RemotePort == 443 || p.LocalPort == 443)
            {
                var sni = Inspect.ParseSni(payload);
                if (!string.IsNullOrEmpty(sni))
                {
                    _dns[p.Remote.ToString()] = sni!;
                    if (app.Domains.Count < 100) app.Domains.Add(sni!);
                }
            }
            else if (p.RemotePort == 80 || p.LocalPort == 80)
            {
                var h = Inspect.ParseHttp(payload);
                if (h is { } hh && hh.Host.Length > 0)
                {
                    _dns[p.Remote.ToString()] = hh.Host;
                    if (app.Domains.Count < 100) app.Domains.Add(hh.Host);
                }
            }
        }
        catch { /* malformed — ignore */ }
    }

    /// <summary>Resolve a remote IP to a hostname seen via DNS/SNI/HTTP, or null.</summary>
    public string? ResolveHost(string ip)
    {
        lock (_lock) { return _dns.TryGetValue(ip, out var h) ? h : null; }
    }

    private void RefreshTick()
    {
        List<ConnectionInfo> conns;
        try { conns = IpHelper.GetConnections(); }
        catch { return; }

        double dt = _sinceRefresh.Elapsed.TotalSeconds;
        if (dt <= 0) dt = 1;
        _sinceRefresh.Restart();

        var connCounts = new Dictionary<int, int>();
        foreach (var c in conns)
            connCounts[c.Pid] = connCounts.TryGetValue(c.Pid, out var n) ? n + 1 : 1;

        lock (_lock)
        {
            _portMap = IpHelper.BuildPortMap(conns);
            foreach (var app in _apps.Values)
            {
                app.Connections = connCounts.TryGetValue(app.Pid, out var n) ? n : 0;
                app.RateOut = (app.BytesOut - app._prevOut) / dt;
                app.RateIn = (app.BytesIn - app._prevIn) / dt;
                app._prevOut = app.BytesOut;
                app._prevIn = app.BytesIn;
            }
        }
    }

    // ---------------- snapshots for the UI ----------------

    public List<AppUsage> SnapshotApps()
    {
        lock (_lock)
        {
            return _apps.Values
                .Where(a => a.TotalBytes > 0 || a.Connections > 0)
                .Select(a => new AppUsage
                {
                    Pid = a.Pid, Name = a.Name, ExePath = a.ExePath,
                    BytesIn = a.BytesIn, BytesOut = a.BytesOut,
                    PacketsIn = a.PacketsIn, PacketsOut = a.PacketsOut,
                    RateIn = a.RateIn, RateOut = a.RateOut,
                    Connections = a.Connections, LastSeen = a.LastSeen, Blocked = a.Blocked,
                    Remotes = new HashSet<string>(a.Remotes),
                    Domains = new HashSet<string>(a.Domains),
                })
                .OrderByDescending(a => a.RateIn + a.RateOut)
                .ThenByDescending(a => a.TotalBytes)
                .ToList();
        }
    }

    /// <summary>Per-destination traffic for one process, with hostnames resolved. For the Destinations view.</summary>
    public List<DestUsage> SnapshotDests(int pid)
    {
        lock (_lock)
        {
            if (!_apps.TryGetValue(pid, out var app)) return new List<DestUsage>();
            return app.Dests.Values.Select(d => new DestUsage
            {
                Ip = d.Ip,
                Host = _dns.TryGetValue(d.Ip, out var h) ? h : null,
                BytesIn = d.BytesIn,
                BytesOut = d.BytesOut,
                Packets = d.Packets,
                LastSeen = d.LastSeen,
            }).ToList();
        }
    }

    public PacketRecord[] SnapshotRecent(int max = 500)
    {
        lock (_lock)
        {
            int skip = Math.Max(0, _recent.Count - max);
            return _recent.Skip(skip).ToArray();
        }
    }

    public void MarkBlocked(IReadOnlySet<string> blockedPaths)
    {
        lock (_lock)
            foreach (var app in _apps.Values)
                app.Blocked = app.ExePath != null && blockedPaths.Contains(app.ExePath);
    }
}
