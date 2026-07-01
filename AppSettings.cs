using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KillSwitch;

public enum KillMechanism
{
    /// <summary>Block all traffic with high-priority Windows Firewall rules (fast, reversible).</summary>
    Firewall = 0,
    /// <summary>Disable the network adapters entirely (hard kill).</summary>
    Adapter = 1,
}

/// <summary>A recurring time window during which the internet is automatically blocked.</summary>
public sealed class ScheduleWindow
{
    /// <summary>Index = (int)DayOfWeek, where 0 = Sunday ... 6 = Saturday. True = window applies that day.</summary>
    public bool[] Days { get; set; } = { false, true, true, true, true, true, false }; // Mon–Fri by default

    /// <summary>Window start, "HH:mm" 24h.</summary>
    public string Start { get; set; } = "23:00";

    /// <summary>Window end, "HH:mm" 24h. If End &lt;= Start the window crosses midnight.</summary>
    public string End { get; set; } = "07:00";

    public bool Enabled { get; set; } = true;

    [JsonIgnore]
    public TimeOnly StartTime => TimeOnly.TryParse(Start, out var t) ? t : new TimeOnly(0, 0);
    [JsonIgnore]
    public TimeOnly EndTime => TimeOnly.TryParse(End, out var t) ? t : new TimeOnly(0, 0);

    /// <summary>True if <paramref name="now"/> falls inside this window. The selected days refer to the day the window STARTS.</summary>
    public bool IsActiveAt(DateTime now)
    {
        if (!Enabled) return false;
        var t = TimeOnly.FromDateTime(now);
        var start = StartTime;
        var end = EndTime;
        int today = (int)now.DayOfWeek;
        int yesterday = (today + 6) % 7;

        if (start < end)
        {
            // Same-day window, e.g. 09:00–17:00.
            return Days[today] && t >= start && t < end;
        }

        // Crosses midnight, e.g. 23:00–07:00. Evening part belongs to today; morning part to yesterday's start day.
        if (start == end) return false; // zero-length / full-day ambiguous: treat as inactive
        if (t >= start) return Days[today];
        if (t < end) return Days[yesterday];
        return false;
    }

    public string DaysLabel()
    {
        string[] names = { "Su", "Mo", "Tu", "We", "Th", "Fr", "Sa" };
        if (Days.All(d => d)) return "Every day";
        if (Days[1] && Days[2] && Days[3] && Days[4] && Days[5] && !Days[0] && !Days[6]) return "Weekdays";
        if (Days[0] && Days[6] && !Days[1] && !Days[2] && !Days[3] && !Days[4] && !Days[5]) return "Weekends";
        var picked = Enumerable.Range(0, 7).Where(i => Days[i]).Select(i => names[i]);
        return picked.Any() ? string.Join(" ", picked) : "(no days)";
    }
}

/// <summary>A per-application firewall rule: block a specific program, optionally on a schedule.</summary>
public sealed class AppRule
{
    public string Path { get; set; } = "";      // full exe path (the firewall match key)
    public string Name { get; set; } = "";       // friendly display name
    public bool BlockedNow { get; set; } = false; // manual block currently applied
    public bool ScheduleEnabled { get; set; } = false;
    public List<ScheduleWindow> Schedule { get; set; } = new();
}

/// <summary>Persisted user settings + runtime state. Stored at %AppData%\KillSwitch\config.json.</summary>
public sealed class AppSettings
{
    public KillMechanism Mechanism { get; set; } = KillMechanism.Firewall;

    /// <summary>Per-application block rules (managed apps), used in normal (blocklist) mode.</summary>
    public List<AppRule> AppRules { get; set; } = new();

    /// <summary>Default-deny: when true, only AllowedApps may reach the internet.</summary>
    public bool AllowlistMode { get; set; } = false;

    /// <summary>Apps permitted to connect while AllowlistMode is on.</summary>
    public List<AppRule> AllowedApps { get; set; } = new();

    /// <summary>Apps that stay online during a global kill switch (exempt from the cut).</summary>
    public List<AppRule> SafeApps { get; set; } = new();

    /// <summary>Blocked destination IPs (block-by-address, for ownerless/unattributed traffic).</summary>
    public List<string> BlockedIps { get; set; } = new();

    /// <summary>Apps with their own block schedule (blocked only during their windows).</summary>
    public List<AppRule> ScheduledApps { get; set; } = new();

    /// <summary>Apps force-closed and prevented from running (via IFEO) until unlocked.</summary>
    public List<AppRule> LockedApps { get; set; } = new();

    /// <summary>How the current global firewall cut was applied: "" | "blockall" | "allowlist". Runtime state.</summary>
    public string GlobalBlockMode { get; set; } = "";

    /// <summary>If true, allow DNS (port 53) during a cut so safe apps can resolve names. Default off = strict lockdown.</summary>
    public bool AllowDnsDuringCut { get; set; } = false;

    // AI analysis
    public string AiProvider { get; set; } = "claude";   // "claude" | "gemini"
    public string AiApiKey { get; set; } = "";            // Claude (Anthropic) key
    public string AiModel { get; set; } = "claude-opus-4-8";
    public string GeminiApiKey { get; set; } = "";        // Google AI Studio key
    public string GeminiModel { get; set; } = "gemini-2.0-flash";

    // HTTPS inspection (MITM) — opt-in, off by default
    public bool MitmEnabled { get; set; } = false;
    public int MitmPort { get; set; } = 8888;

    public bool ScheduleEnabled { get; set; } = false;
    public List<ScheduleWindow> Schedule { get; set; } = new();

    // Global hotkey (defaults to Ctrl+Alt+K). Stored as the raw WinForms values.
    public bool HotkeyEnabled { get; set; } = true;
    public int HotkeyModifiers { get; set; } = (int)(Keys.Control | Keys.Alt);
    public int HotkeyKey { get; set; } = (int)Keys.K;

    public bool StartWithWindows { get; set; } = false;
    public bool StartMinimized { get; set; } = true;

    /// <summary>Adapters we disabled (so a hard kill can re-enable exactly those). Runtime state.</summary>
    public List<string> DisabledAdapters { get; set; } = new();

    // ---- persistence ----

    [JsonIgnore]
    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KillSwitch");

    [JsonIgnore]
    public static string FilePath => Path.Combine(Dir, "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
                if (s != null) return s;
            }
        }
        catch { /* fall through to defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* best-effort */ }
    }
}
