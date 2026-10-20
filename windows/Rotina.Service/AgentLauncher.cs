using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Rotina.Service;

/// <summary>Keeps Rotina.App (which does the blocking) running in the logged-on user's session.</summary>
public static class AgentLauncher
{
    const string ProcessName = "Rotina.App";
    static DateTime nextTry;

    public static void EnsureRunning(ILogger log)
    {
        var session = WTSGetActiveConsoleSessionId();
        if (session == 0xFFFFFFFF) return; // nobody at the console
        if (IsRunning((int)session) || DateTime.UtcNow < nextTry) return;
        nextTry = DateTime.UtcNow.AddSeconds(5); // a failing launch never spins every second

        if (AppPath() is not { } path)
        {
            log.LogWarning("Rotina.App.exe não encontrado; o bloqueio de apps não roda.");
            return;
        }
        if (!WTSQueryUserToken(session, out var token))
        {
            // Not LocalSystem (dev run from a console): we already are in the user's session.
            Process.Start(new ProcessStartInfo(path, "--background") { UseShellExecute = false })?.Dispose();
            return;
        }
        try
        {
            if (!CreateEnvironmentBlock(out var env, token, false)) env = 0;
            try
            {
                var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
                var cmd = new StringBuilder($"\"{path}\" --background");
                if (!CreateProcessAsUser(token, null, cmd, 0, 0, false, CREATE_UNICODE_ENVIRONMENT, env,
                        Path.GetDirectoryName(path), ref si, out var pi))
                    log.LogWarning("Falha ao abrir Rotina.App na sessão {Session}: erro {Error}", session, Marshal.GetLastWin32Error());
                else { CloseHandle(pi.hProcess); CloseHandle(pi.hThread); }
            }
            finally { if (env != 0) DestroyEnvironmentBlock(env); }
        }
        finally { CloseHandle(token); }
    }

    static bool IsRunning(int session)
    {
        var running = false;
        foreach (var p in Process.GetProcessesByName(ProcessName))
            using (p) running |= p.SessionId == session;
        return running;
    }

    static string? AppPath()
    {
        var baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDir, "..", "App", "Rotina.App.exe"), // installed: Program Files\Rotina\{Service,App}
            // shortcut: dev layout (service and app built side by side in the repo); installed path covers production.
            Path.Combine(baseDir, "..", "..", "..", "..", "Rotina.App", "bin", "Debug", "net10.0-windows", "Rotina.App.exe"),
        ];
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    const uint CREATE_UNICODE_ENVIRONMENT = 0x400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION
    {
        public nint hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll")] static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", SetLastError = true)] static extern bool WTSQueryUserToken(uint session, out nint token);
    [DllImport("userenv.dll", SetLastError = true)] static extern bool CreateEnvironmentBlock(out nint env, nint token, bool inherit);
    [DllImport("userenv.dll")] static extern bool DestroyEnvironmentBlock(nint env);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessAsUser(nint token, string? app, StringBuilder cmd, nint pa, nint ta, bool inherit,
        uint flags, nint env, string? dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint h);
}
