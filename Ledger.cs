using System.IO;
using System.Text.Json;

namespace KillSwitch;

/// <summary>One app's accumulated network history.</summary>
public sealed class LedgerEntry
{
    public string Key { get; set; } = "";     // exe path, or name if pathless
    public string Name { get; set; } = "";
    public long BytesIn { get; set; }
    public long BytesOut { get; set; }
    public string FirstSeen { get; set; } = "";
    public string LastSeen { get; set; } = "";

    /// <summary>Domains/hosts this app has been seen contacting (capped).</summary>
    public List<string> Domains { get; set; } = new();

    public long TotalBytes => BytesIn + BytesOut;
}

/// <summary>Persistent ledger of per-app traffic, kept across runs at %AppData%\KillSwitch\ledger.json.</summary>
public sealed class Ledger
{
    public Dictionary<string, LedgerEntry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static string FilePath => Path.Combine(AppSettings.Dir, "ledger.json");
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = false };

    public static Ledger Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var l = JsonSerializer.Deserialize<Ledger>(File.ReadAllText(FilePath));
                if (l != null) { l.NormalizeKeys(); return l; }
            }
        }
        catch { }
        return new Ledger();
    }

    /// <summary>JSON load drops the case-insensitive comparer — rebuild it, merging any case-duplicate keys.</summary>
    private void NormalizeKeys()
    {
        var rebuilt = new Dictionary<string, LedgerEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in Entries)
        {
            if (rebuilt.TryGetValue(k, out var ex))
            {
                ex.BytesIn += v.BytesIn;
                ex.BytesOut += v.BytesOut;
                foreach (var d in v.Domains) if (!ex.Domains.Contains(d)) ex.Domains.Add(d);
            }
            else rebuilt[k] = v;
        }
        Entries = rebuilt;
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
