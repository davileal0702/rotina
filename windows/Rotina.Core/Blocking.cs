namespace Rotina.Core;

/// <summary>Decides which program windows get closed during an activity. Pure, so the rule is testable.</summary>
public static class Blocking
{
    // Tools inside C:\Windows that could undo the block. Blocked unless allowed, even though the folder is otherwise left alone.
    public static readonly HashSet<string> WindowsTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "taskmgr.exe", "cmd.exe", "powershell.exe", "powershell_ise.exe", "regedit.exe", "mmc.exe",
        "control.exe", "SystemSettings.exe", "msconfig.exe", "resmon.exe", "perfmon.exe",
    };

    /// <param name="exePath">Full path of the program that owns a visible window.</param>
    /// <param name="allowed">Effective allowed list (activity + always allowed), full .exe paths.</param>
    /// <param name="windowsDir">Windows folder ending with a backslash, e.g. "C:\Windows\".</param>
    /// <param name="self">Rotina's own executable, never closed.</param>
    public static bool ShouldBlock(string exePath, IEnumerable<string> allowed, string windowsDir, string self)
    {
        if (exePath.Equals(self, StringComparison.OrdinalIgnoreCase)) return false;
        if (allowed.Contains(exePath, StringComparer.OrdinalIgnoreCase)) return false;
        // Shell, Start menu, lock screen and other system parts live here; only the listed tools are closed.
        if (exePath.StartsWith(windowsDir, StringComparison.OrdinalIgnoreCase))
            return WindowsTools.Contains(Path.GetFileName(exePath));
        return true;
    }
}
