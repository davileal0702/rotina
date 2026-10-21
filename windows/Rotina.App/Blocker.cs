using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Rotina.Core;

namespace Rotina.App;

public sealed record BlockedApp(string Path, bool Closed);

/// <summary>
/// Closes programs with a visible window that the running activity does not allow.
/// Runs in the user's session because the service (session 0) cannot see the user's windows.
/// </summary>
public static class Blocker
{
    static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + Path.DirectorySeparatorChar;
    static readonly string Self = Environment.ProcessPath ?? "";

    /// <param name="closeChrome">The Chrome extension is missing: Chrome is closed too, even if allowed.</param>
    public static List<BlockedApp> Enforce(IReadOnlyCollection<string> allowed, bool closeChrome = false)
    {
        var result = new List<BlockedApp>();
        foreach (var (pid, path) in WindowOwners())
        {
            var chrome = closeChrome && Path.GetFileName(path).Equals("chrome.exe", StringComparison.OrdinalIgnoreCase);
            if (!chrome && !Blocking.ShouldBlock(path, allowed, WindowsDir, Self)) continue;
            try
            {
                using var p = Process.GetProcessById(pid);
                // A Chrome that just opened gets a minute for its extension to wake up and say hello.
                if (chrome && DateTime.Now - p.StartTime < TimeSpan.FromSeconds(60)) continue;
                p.Kill();
                result.Add(new(path, true));
            }
            catch (Win32Exception) { result.Add(new(path, false)); } // elevated: a standard user can't close it
            catch (InvalidOperationException) { } // already gone
            catch (ArgumentException) { }
        }
        return result;
    }

    /// <summary>Distinct (pid, exe path) of programs that own a visible top-level window.</summary>
    static Dictionary<int, string> WindowOwners()
    {
        var owners = new Dictionary<int, string>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != 0 || GetWindowTextLength(hwnd) == 0
                || (GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            var path = ExePath(pid);
            // Store apps (Settings, Calculator...) draw inside ApplicationFrameHost; the real app owns the inner CoreWindow.
            if (path is not null && Path.GetFileName(path).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
            {
                var core = FindWindowEx(hwnd, 0, "Windows.UI.Core.CoreWindow", null);
                if (core == 0) return true;
                GetWindowThreadProcessId(core, out pid);
                path = ExePath(pid);
            }
            if (path is not null) owners[(int)pid] = path;
            return true;
        }, 0);
        return owners;
    }

    static string? ExePath(uint pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == 0) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    const int GW_OWNER = 4, GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80;
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    delegate bool EnumWindowsProc(nint hwnd, nint lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, nint lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] static extern nint GetWindow(nint hwnd, int cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint FindWindowEx(nint parent, nint after, string? cls, string? title);
    [DllImport("kernel32.dll")] static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(nint h, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint h);
}
