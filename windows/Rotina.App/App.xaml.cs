using System.Threading;
using System.Windows;

namespace Rotina.App;

public partial class App : Application
{
    EventWaitHandle? showSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // One Rotina per user session: a second launch just brings the first one's window up.
        showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Rotina.App.Show", out var first);
        if (!first)
        {
            showSignal.Set();
            Shutdown();
            return;
        }
        // The window hides to the tray instead of closing, and the block toast must not end the app.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var window = new MainWindow();
        MainWindow = window;
        // --background: started at login or relaunched by the service; stay in the tray.
        if (!e.Args.Contains("--background")) window.Show();
        new Thread(() =>
        {
            while (showSignal.WaitOne()) Dispatcher.Invoke(window.ShowFromTray);
        }) { IsBackground = true }.Start();
    }
}
