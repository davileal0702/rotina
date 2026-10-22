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

    /// <summary>
    /// The Chrome extension has gone quiet (disabled or removed): Chrome is closed during an activity.
    /// 60 s, because Chrome puts the extension to sleep and its 30 s alarm can be late.
    /// </summary>
    public static bool ExtensionMissing(int? secondsSinceHeartbeat) => secondsSinceHeartbeat is null or > 60;

    /// <param name="exePath">Full path of the program that owns a visible window.</param>
    /// <param name="allowed">Effective allowed list (activity + always allowed), full .exe paths.</param>
    /// <param name="windowsDir">Windows folder ending with a backslash, e.g. "C:\Windows\".</param>
    /// <param name="self">Rotina's own executable, never closed.</param>
    // ...\WindowsApps\Name_Version_Arch_ResourceId_PublisherId\... (Store / MSIX packages)
    static readonly System.Text.RegularExpressions.Regex StorePackage = new(
        @"\\WindowsApps\\([^_\\]+)_[^_\\]*_[^_\\]*_[^_\\]*_([^_\\]+)\\",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>"spotifyab.spotifymusic__zpdnekdrzrea0" for any file of that Store package, without version; null otherwise.</summary>
    public static string? StorePackageId(string path) =>
        StorePackage.Match(path) is { Success: true } m ? $"{m.Groups[1].Value}__{m.Groups[2].Value}".ToLowerInvariant() : null;

    public static bool ShouldBlock(string exePath, IEnumerable<string> allowed, string windowsDir, string self)
    {
        if (exePath.Equals(self, StringComparison.OrdinalIgnoreCase)) return false;
        if (allowed.Contains(exePath, StringComparer.OrdinalIgnoreCase)) return false;
        // Store app: allowing any program of the package allows the whole package, in any version.
        if (StorePackageId(exePath) is { } pkg && allowed.Any(a => StorePackageId(a) == pkg)) return false;
        // Shell, Start menu, lock screen and other system parts live here; only the listed tools are closed.
        if (exePath.StartsWith(windowsDir, StringComparison.OrdinalIgnoreCase))
            return WindowsTools.Contains(Path.GetFileName(exePath));
        return true;
    }
}
