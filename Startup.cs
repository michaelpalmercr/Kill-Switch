namespace KillSwitch;

/// <summary>
/// "Start with Windows" for an elevated app. A plain Run-key entry can't auto-elevate
/// (UAC would block it at logon), so we register a Scheduled Task that runs at logon
/// with highest privileges instead.
/// </summary>
public static class Startup
{
    private const string TaskName = "KillSwitch_Autostart";

    public static bool IsEnabled()
    {
        var r = ProcessRunner.Run("schtasks.exe", $"/Query /TN \"{TaskName}\"");
        return r.Ok;
    }

    public static string? SetEnabled(bool enabled)
    {
        if (enabled)
        {
            string exe = Environment.ProcessPath ?? Application.ExecutablePath;
            var r = ProcessRunner.Run("schtasks.exe",
                $"/Create /TN \"{TaskName}\" /TR \"\\\"{exe}\\\"\" /SC ONLOGON /RL HIGHEST /F");
            return r.Ok ? null : r.Output;
        }
        else
        {
            var r = ProcessRunner.Run("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F");
            return r.Ok ? null : r.Output;
        }
    }
}
