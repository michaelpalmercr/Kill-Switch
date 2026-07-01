using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace KillSwitch;

public sealed class QItem
{
    public string Original { get; set; } = "";
    public string Stored { get; set; } = "";
    public bool IsDir { get; set; }
}

/// <summary>A reversible removal batch (files moved to quarantine + exported registry keys).</summary>
public sealed class QBatch
{
    public string Id { get; set; } = "";
    public string When { get; set; } = "";
    public string Note { get; set; } = "";
    public List<QItem> Items { get; set; } = new();
    public List<string> RegFiles { get; set; } = new();
}

/// <summary>
/// Moves files/folders to %AppData%\KillSwitch\Quarantine\&lt;batch&gt; instead of deleting, so removals
/// are reversible. Locked files fall back to delete-on-reboot (not reversible — flagged to the user).
/// </summary>
public static class Quarantine
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string src, string? dst, int flags);
    private const int DELAY_UNTIL_REBOOT = 0x4;

    public static string Root => Path.Combine(AppSettings.Dir, "Quarantine");

    public static QBatch NewBatch(string note)
    {
        string id = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
        var b = new QBatch { Id = id, When = DateTime.Now.ToString("u"), Note = note };
        Directory.CreateDirectory(Path.Combine(Root, id));
        return b;
    }

    public static string BatchDir(QBatch b) => Path.Combine(Root, b.Id);

    /// <summary>Move a file/folder into the batch. Throws IOException if locked / UnauthorizedAccessException if ACL-blocked.</summary>
    public static void Stash(QBatch b, string path)
    {
        bool isDir = Directory.Exists(path);
        try { if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal); } catch { }

        string name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(name)) name = "item";
        string stored = Path.Combine(BatchDir(b), name + "_" + Guid.NewGuid().ToString("N")[..6]);

        if (isDir) Directory.Move(path, stored); else File.Move(path, stored);
        b.Items.Add(new QItem { Original = path, Stored = stored, IsDir = isDir });
    }

    public static bool ScheduleDeleteOnReboot(string path) => MoveFileEx(path, null, DELAY_UNTIL_REBOOT);

    public static void ExportRegKey(QBatch b, string regKeyPath)
    {
        try
        {
            string file = Path.Combine(BatchDir(b), "reg_" + Guid.NewGuid().ToString("N")[..6] + ".reg");
            var r = ProcessRunner.Run("reg.exe", $"export \"{regKeyPath}\" \"{file}\" /y");
            if (r.Ok && File.Exists(file)) b.RegFiles.Add(file);
        }
        catch { }
    }

    public static void Save(QBatch b)
    {
        try { File.WriteAllText(Path.Combine(BatchDir(b), "manifest.json"), JsonSerializer.Serialize(b)); } catch { }
    }

    public static List<QBatch> All()
    {
        var list = new List<QBatch>();
        try
        {
            if (!Directory.Exists(Root)) return list;
            foreach (var dir in new DirectoryInfo(Root).GetDirectories().OrderByDescending(d => d.Name))
            {
                var mf = Path.Combine(dir.FullName, "manifest.json");
                if (!File.Exists(mf)) continue;
                try { var b = JsonSerializer.Deserialize<QBatch>(File.ReadAllText(mf)); if (b != null) list.Add(b); } catch { }
            }
        }
        catch { }
        return list;
    }

    public static QBatch? Latest() => All().FirstOrDefault();

    public static (int restored, int failed) Restore(QBatch b)
    {
        int ok = 0, fail = 0;
        foreach (var it in b.Items)
        {
            try
            {
                var parent = Path.GetDirectoryName(it.Original);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                if (it.IsDir) Directory.Move(it.Stored, it.Original); else File.Move(it.Stored, it.Original);
                ok++;
            }
            catch { fail++; }
        }
        foreach (var rf in b.RegFiles) { try { ProcessRunner.Run("reg.exe", $"import \"{rf}\""); } catch { } }
        return (ok, fail);
    }
}
