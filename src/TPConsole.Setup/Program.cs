// TPConsole installer / uninstaller (one exe, two modes).
// Setup mode (app.zip embedded): extracts the self-contained app to Program Files, adds Start menu
// (and optional desktop) shortcuts and an "Installed apps" entry whose uninstall runs Uninstall.exe.
// Uninstall mode (no payload, or /uninstall): Uninstall.exe is this project built without the payload.
// Silent mode (/silent [/wait=<pid>] [/tray]): the app's auto-update. No window; waits for the app to exit,
// installs, logs to %APPDATA%\TPConsole\update.log and starts the app again.
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Program
{
    public const string Name = "TPConsole";
    public const string AppExe = "TPConsole.App.exe";
    public const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + Name;
    public static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Name);
    public static readonly string StartMenuLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Name + ".lnk");
    public static readonly string DesktopLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), Name + ".lnk");
    public static readonly string Version = Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "?";

    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.zip");
        bool uninstall = payload == null || args.Contains("/uninstall", StringComparer.OrdinalIgnoreCase);
        if (!uninstall && args.Contains("/silent")) { Silent(payload!, args); return; }

        // Uninstall.exe lives in the folder it deletes: run from a temp copy instead.
        if (uninstall && !args.Contains("/temp"))
        {
            var tmp = Path.Combine(Path.GetTempPath(), "TPConsole-Uninstall.exe");
            File.Copy(Application.ExecutablePath, tmp, true);
            Process.Start(new ProcessStartInfo(tmp, "/uninstall /temp") { UseShellExecute = false });
            return;
        }
        Application.Run(new SetupForm(uninstall, payload));
    }

    static void Silent(Stream payload, string[] args)
    {
        var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Name, "update.log");
        string result;
        try
        {
            var wait = args.FirstOrDefault(a => a.StartsWith("/wait="));
            if (wait != null && int.TryParse(wait.Substring(6), out var pid))
                try { using var p = Process.GetProcessById(pid); p.WaitForExit(30000); } catch (ArgumentException) { }
            for (int i = 0; i < 20 && AppRunning(); i++) Thread.Sleep(500);
            Install(payload, File.Exists(DesktopLink), new Progress<(int, string)>());
            result = "updated to " + Version;
        }
        catch (Exception ex)
        {
            result = $"update to {Version} failed: {ex.Message}";
            // The app skips this version for automatic updates, so a broken setup can't restart it forever.
            try { using var key = Registry.LocalMachine.CreateSubKey(UninstallKey); key.SetValue("UpdateFailed", Version); } catch (Exception) { }
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.AppendAllText(log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {result}\r\n");
        }
        catch (IOException) { }
        // Start the app again either way (the old version is still in place if the install failed).
        var exe = Path.Combine(InstallDir, AppExe);
        if (File.Exists(exe)) Process.Start(new ProcessStartInfo(exe, args.Contains("/tray") ? "--tray" : "") { UseShellExecute = true });
    }

    public static bool AppRunning() =>
        Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppExe)).Length > 0 || Process.GetProcessesByName(Legacy + ".App").Length > 0;

    public static bool ServiceExists(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name);
        return key != null;
    }

    public static int Run(string exe, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true })!;
        p.WaitForExit();
        return p.ExitCode;
    }

    public static void Shortcut(string path, string target)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        var lnk = shell.CreateShortcut(path);
        lnk.TargetPath = target;
        lnk.WorkingDirectory = Path.GetDirectoryName(target);
        lnk.IconLocation = target + ",0";
        lnk.Save();
    }

    public static void Install(Stream payload, bool desktop, IProgress<(int Percent, string Text)> progress)
    {
        // Extract next to the install folder, then swap: a failed or interrupted install leaves the old
        // version working.
        string staging = InstallDir + ".new", old = InstallDir + ".old";
        foreach (var d in new[] { staging, old }) if (Directory.Exists(d)) Directory.Delete(d, true);
        Directory.CreateDirectory(staging);

        using (var zip = new ZipArchive(payload, ZipArchiveMode.Read))
        {
            var root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
            int done = 0, total = zip.Entries.Count;
            foreach (var entry in zip.Entries)
            {
                var dest = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Bad entry: " + entry.FullName);
                if (entry.Name.Length == 0) Directory.CreateDirectory(dest);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    entry.ExtractToFile(dest, true);
                }
                if (++done % 10 == 0 || done == total) progress.Report((done * 95 / total, $"파일 복사 중… ({done}/{total})"));
            }
        }

        progress.Report((95, "이전 버전 바꾸는 중…"));
        if (Directory.Exists(InstallDir)) Retry(() => Directory.Move(InstallDir, old)); // fails while files are in use
        try { Retry(() => Directory.Move(staging, InstallDir)); }
        catch
        {
            if (!Directory.Exists(InstallDir) && Directory.Exists(old)) Directory.Move(old, InstallDir);
            throw;
        }
        TryDelete(old);

        progress.Report((96, "바로가기 만드는 중…"));
        var exe = Path.Combine(InstallDir, AppExe);
        Shortcut(StartMenuLink, exe);
        if (desktop) Shortcut(DesktopLink, exe);
        else if (File.Exists(DesktopLink)) File.Delete(DesktopLink);

        progress.Report((98, "설치된 앱 목록에 등록 중…"));
        long kb = new DirectoryInfo(InstallDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / 1024;
        using (var key = Registry.LocalMachine.CreateSubKey(UninstallKey))
        {
            key.SetValue("DisplayName", Name);
            key.SetValue("DisplayVersion", Version);
            key.SetValue("Publisher", Name);
            key.SetValue("DisplayIcon", exe);
            key.SetValue("InstallLocation", InstallDir);
            key.SetValue("UninstallString", $"\"{Path.Combine(InstallDir, "Uninstall.exe")}\" /uninstall");
            key.SetValue("EstimatedSize", (int)Math.Min(kb, int.MaxValue), RegistryValueKind.DWord);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.DeleteValue("UpdateFailed", false);
        }
        RemoveLegacy(exe);
        progress.Report((100, "설치 완료"));
    }

    // The app used to be called ToppingCtl: remove that install (settings move over in the app itself)
    // and keep its "start with Windows" choice.
    const string Legacy = "ToppingCtl";

    static void RemoveLegacy(string exe)
    {
        if (Run("schtasks", $"/query /tn {Legacy}") == 0)
        {
            Run("schtasks", $"/delete /f /tn {Legacy}");
            Run("schtasks", $"/create /f /tn {Name} /sc onlogon /rl highest /tr \"\\\"{exe}\\\" --tray\"");
        }
        foreach (var lnk in new[] { StartMenuLink, DesktopLink })
        {
            var old = Path.Combine(Path.GetDirectoryName(lnk)!, Legacy + ".lnk");
            if (File.Exists(old)) File.Delete(old);
        }
        var dir = Path.Combine(Path.GetDirectoryName(InstallDir)!, Legacy);
        TryDelete(dir);
        Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + Legacy, false);
    }

    // A just-exited app (or antivirus) can hold files for a moment.
    static void Retry(Action move)
    {
        for (int i = 0; ; i++)
        {
            try { move(); return; }
            catch (Exception e) when (i < 10 && e is IOException or UnauthorizedAccessException) { Thread.Sleep(500); }
        }
    }

    public static void Uninstall(bool settings, IProgress<(int Percent, string Text)> progress)
    {
        progress.Report((10, "Windows 시작 시 실행 해제 중…"));
        Run("schtasks", $"/delete /f /tn {Name}");

        progress.Report((30, "프로그램 파일 삭제 중…"));
        foreach (var lnk in new[] { StartMenuLink, DesktopLink }) if (File.Exists(lnk)) File.Delete(lnk);
        if (Directory.Exists(InstallDir)) Directory.Delete(InstallDir, true);

        progress.Report((70, "캐시 삭제 중…"));
        // WebView2 cache; settings and backups (incl. remove-virtual-routing.ps1) are in %APPDATA%.
        TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name));
        if (settings) TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Name));

        Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false);
        progress.Report((100, "제거 완료"));
    }

    static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // The temp copy of Uninstall.exe deletes itself once it has exited.
    public static void DeleteSelfLater() =>
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 2 /nobreak >nul & del /f /q \"{Application.ExecutablePath}\"")
        { UseShellExecute = false, CreateNoWindow = true });
}
