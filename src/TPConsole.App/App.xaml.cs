using System.Windows;

namespace TPConsole.App;

public partial class App : System.Windows.Application
{
    // Two instances would both own the device and the profile file.
    Mutex? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        // One instance owns the device. Launching again just brings the running one to the front.
        var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\TPConsole.Show");
        _single = new Mutex(true, @"Local\TPConsole.App", out bool first);
        if (!first)
        {
            showSignal.Set();
            Shutdown();
            return;
        }
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var window = new MainWindow();
        new Thread(() =>
        {
            while (showSignal.WaitOne()) window.Dispatcher.BeginInvoke(window.ShowFromTray);
        }) { IsBackground = true }.Start();
        if (e.Args is ["--tray"]) new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle(); // hotkeys need a window handle
        else window.Show();
    }
}
