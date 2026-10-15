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
        // Not the install folder (the Start menu shortcut starts there): a working directory holds its folder
        // open, and WebView2 and the update setup inherit it, so the update could not swap the folder.
        Environment.CurrentDirectory = System.IO.Path.GetTempPath();
        var window = new MainWindow();
        new Thread(() =>
        {
            while (showSignal.WaitOne()) window.Dispatcher.BeginInvoke(window.ShowFromTray);
        }) { IsBackground = true }.Start();
        if (e.Args is ["--tray"]) new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle(); // hotkeys need a window handle
        else window.Show();
    }
}
