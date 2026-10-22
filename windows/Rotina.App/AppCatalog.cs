using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Rotina.App;

public sealed record WinApp(string Name, string Path, ImageSource? Icon);

/// <summary>Windows apps the user can pick: Start Menu shortcuts plus apps open right now.</summary>
public static class AppCatalog
{
    static Task<List<WinApp>>? loading;
    static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + Path.DirectorySeparatorChar;

    // Started once, in the background: resolving a few hundred shortcuts and icons takes a second or two.
    public static Task<List<WinApp>> All() => loading ??= Task.Run(Load);

    public static WinApp Describe(string path)
    {
        if (!File.Exists(path)) return new(Path.GetFileNameWithoutExtension(path) + " (não encontrado)", path, null);
        var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
        return new(string.IsNullOrWhiteSpace(description) ? Path.GetFileNameWithoutExtension(path) : description.Trim(), path, IconOf(path));
    }

    static List<WinApp> Load()
    {
        var found = new Dictionary<string, WinApp>(StringComparer.OrdinalIgnoreCase);
        // shortcut: Store (MSIX) apps have no .lnk, so they only show up while open; upgrade with the shell AppsFolder if missed.
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                                     Environment.GetFolderPath(Environment.SpecialFolder.StartMenu) })
        foreach (var lnk in SafeEnumerate(root))
        {
            string target;
            try { target = shell.CreateShortcut(lnk).TargetPath; }
            catch (Exception) { continue; }
            if (!IsUsefulExe(target)) continue;
            found.TryAdd(target, new(Path.GetFileNameWithoutExtension(lnk), target, IconOf(target)));
        }
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.MainWindowHandle == 0 || p.MainModule?.FileName is not { } path || !IsUsefulExe(path)) continue;
                    // Open windows from C:\Windows are hosts like ApplicationFrameHost; real tools come from the Start Menu.
                    if (path.StartsWith(WindowsDir, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!found.ContainsKey(path)) found[path] = Describe(path);
                }
                catch (Win32Exception) { } // elevated or protected process
                catch (InvalidOperationException) { } // exited meanwhile
            }
        }
        foreach (var app in StoreApps())
            if (!found.Values.Any(f => Rotina.Core.Blocking.StorePackageId(f.Path) == Rotina.Core.Blocking.StorePackageId(app.Path)))
                found[app.Path] = app;
        return [.. found.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>
    /// Store (MSIX) apps installed for this user, such as Spotify and Claude: they have no Start Menu .lnk.
    /// One entry per package; the blocking rule then allows every program of that package (Blocking.StorePackageId).
    /// </summary>
    static IEnumerable<WinApp> StoreApps()
    {
        using var packages = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
        if (packages is null) yield break;
        foreach (var name in packages.GetSubKeyNames())
        {
            WinApp? app = null;
            try
            {
                using var key = packages.OpenSubKey(name);
                if (key?.GetValue("PackageRootFolder") is not string root || root.StartsWith(WindowsDir, StringComparison.OrdinalIgnoreCase)) continue;
                var manifest = new System.Xml.XmlDocument();
                manifest.Load(Path.Combine(root, "AppxManifest.xml"));
                if (manifest.GetElementsByTagName("Framework") is { Count: > 0 } fw && fw[0]!.InnerText == "true") continue;
                // Apps hidden from the Start Menu (helpers, extensions) are not offered.
                var visual = manifest.GetElementsByTagName("uap:VisualElements");
                if (visual.Count > 0 && (visual[0] as System.Xml.XmlElement)?.GetAttribute("AppListEntry") == "none") continue;
                var exe = manifest.GetElementsByTagName("Application").OfType<System.Xml.XmlElement>()
                    .Select(a => a.GetAttribute("Executable")).FirstOrDefault(e => e.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                if (exe is null || !File.Exists(Path.Combine(root, exe))) continue;
                var label = manifest.GetElementsByTagName("DisplayName") is { Count: > 0 } dn ? dn[0]!.InnerText : "";
                if (label.Length == 0 || label.StartsWith("ms-resource", StringComparison.OrdinalIgnoreCase))
                    label = name.Split('_')[0].Split('.').Last(); // "SpotifyAB.SpotifyMusic_..." -> "SpotifyMusic"
                var path = Path.Combine(root, exe);
                app = new WinApp(label, path, IconOf(path));
            }
            catch (Exception) { } // unreadable manifest: skip that package
            if (app is not null) yield return app;
        }
    }

    static IEnumerable<string> SafeEnumerate(string root)
    {
        try { return Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList(); }
        catch (IOException) { return []; }
    }

    static bool IsUsefulExe(string path) =>
        path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path)
        && !Path.GetFileName(path).StartsWith("unins", StringComparison.OrdinalIgnoreCase)
        && !path.Contains("uninstall", StringComparison.OrdinalIgnoreCase);

    static ImageSource? IconOf(string path)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null) return null;
            var image = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze(); // built on a worker thread, shown on the UI thread
            return image;
        }
        catch (Exception) { return null; }
    }
}
