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
        return [.. found.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)];
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
