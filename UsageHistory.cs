using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KillSwitch;

/// <summary>One hour of traffic for an app. Hour = Unix epoch hours (seconds/3600).</summary>
public sealed class HourBucket
{
    public long Hour { get; set; }
    public long In { get; set; }
    public long Out { get; set; }
}

/// <summary>A zero-filled point for graphing.</summary>
public readonly record struct HourPoint(DateTime Hour, long In, long Out)
{
    public long Total => In + Out;
}

/// <summary>Persistent per-app, per-hour usage history at %AppData%\KillSwitch\usage.json (kept ~3 weeks).</summary>
public sealed class UsageHistory
{
    private const int KeepHours = 24 * 21;

    public Dictionary<string, List<HourBucket>> Apps { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    private readonly Dictionary<string, Dictionary<long, HourBucket>> _idx = new(StringComparer.OrdinalIgnoreCase);

    private static string FilePath => Path.Combine(AppSettings.Dir, "usage.json");
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = false };

    public static long NowHour() => DateTimeOffset.Now.ToUnixTimeSeconds() / 3600;

    public static UsageHistory Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var h = JsonSerializer.Deserialize<UsageHistory>(File.ReadAllText(FilePath));
                if (h != null) { h.Reindex(); return h; }
            }
        }
        catch { }
        return new UsageHistory();
    }

    private void Reindex()
    {
        // System.Text.Json deserializes dictionaries with the default (case-sensitive) comparer,
        // so rebuild Apps case-insensitively (merging any case-duplicate keys), then index it.
        var rebuilt = new Dictionary<string, List<HourBucket>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, list) in Apps)
        {
            if (rebuilt.TryGetValue(key, out var existing)) existing.AddRange(list);
            else rebuilt[key] = list;
        }
        Apps = rebuilt;

        _idx.Clear();
        foreach (var key in Apps.Keys.ToList())
        {
            var d = new Dictionary<long, HourBucket>();
            foreach (var b in Apps[key])
            {
                if (d.TryGetValue(b.Hour, out var ex)) { ex.In += b.In; ex.Out += b.Out; }
                else d[b.Hour] = b;
            }
            Apps[key] = d.Values.OrderBy(x => x.Hour).ToList();
            _idx[key] = d;
        }
    }

    public void Add(string key, long dIn, long dOut)
    {
        if (string.IsNullOrEmpty(key) || (dIn <= 0 && dOut <= 0)) return;
        long hour = NowHour();

        if (!_idx.TryGetValue(key, out var d)) { d = new Dictionary<long, HourBucket>(); _idx[key] = d; }
        if (!Apps.TryGetValue(key, out var list)) { list = new List<HourBucket>(); Apps[key] = list; }
        if (!d.TryGetValue(hour, out var b)) { b = new HourBucket { Hour = hour }; d[hour] = b; list.Add(b); }
        b.In += dIn;
        b.Out += dOut;

        // Prune anything older than the retention window.
        long cutoff = hour - KeepHours;
        if (list.Count > 0 && list[0].Hour < cutoff)
        {
            list.RemoveAll(x => x.Hour < cutoff);
            foreach (var stale in d.Keys.Where(k => k < cutoff).ToList()) d.Remove(stale);
        }
    }

    /// <summary>Zero-filled hourly series for the last <paramref name="hours"/> hours (oldest → newest).</summary>
    public List<HourPoint> Series(string key, int hours)
    {
        var points = new List<HourPoint>(hours);
        long now = NowHour();
        _idx.TryGetValue(key, out var d);
        for (long h = now - hours + 1; h <= now; h++)
        {
            var dt = DateTimeOffset.FromUnixTimeSeconds(h * 3600).LocalDateTime;
            if (d != null && d.TryGetValue(h, out var b)) points.Add(new HourPoint(dt, b.In, b.Out));
            else points.Add(new HourPoint(dt, 0, 0));
        }
        return points;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opts));
        }
        catch { }
    }
}
