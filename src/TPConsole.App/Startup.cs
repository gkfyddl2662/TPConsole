using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace TPConsole.App;

/// <summary>
/// "Start with Windows". The app runs as administrator, and Windows silently skips elevated apps in
/// the Run key, so this is a logon task with highest privileges (no UAC prompt at logon).
/// </summary>
public static class Startup
{
    const string Task = "TPConsole";

    public static bool IsSet => Shell.Run("schtasks", $"/query /tn {Task}").Code == 0;

    public static void Set(bool on)
    {
        Shell.Run("schtasks", on
            ? $"/create /f /tn {Task} /sc onlogon /rl highest /tr \"\\\"{Environment.ProcessPath}\\\" --tray\""
            : $"/delete /f /tn {Task}");
    }

    /// <summary>Whether TOPPING Control Center also starts with Windows (both would write to the device).</summary>
    public static bool ControlCenterAutostarts()
    {
        const string run = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
        bool Enabled(RegistryKey hive, string approvedKey, string name)
        {
            // Task Manager's "Disabled" is recorded here: first byte 3 = disabled.
            using var a = hive.OpenSubKey(approved + approvedKey);
            return a?.GetValue(name) is not byte[] b || b.Length == 0 || (b[0] & 1) == 0;
        }
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        foreach (var path in new[] { run, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run" })
        {
            using var k = hive.OpenSubKey(path);
            if (k is null) continue;
            foreach (var name in k.GetValueNames())
                if (k.GetValue(name) is string v && v.Contains("ToppingPro", StringComparison.OrdinalIgnoreCase)
                    && Enabled(hive, path.Contains("WOW6432") ? "Run32" : "Run", name))
                    return true;
        }
        foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Startup), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup) })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.GetFiles(dir, "*.lnk"))
                if (Path.GetFileName(f).Contains("TOPPING", StringComparison.OrdinalIgnoreCase)
                    && Enabled(dir.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)) ? Registry.CurrentUser : Registry.LocalMachine, "StartupFolder", Path.GetFileName(f)))
                    return true;
        }
        return false;
    }
}

/// <summary>Runs a console tool without a window.</summary>
static class Shell
{
    /// <summary>Output (stdout + stderr) and exit code; the command and its output go to <paramref name="log"/> if given.</summary>
    public static (string Output, int Code) Run(string exe, string args, Action<string>? log = null)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        log?.Invoke($"> {exe} {args} (exit {p.ExitCode}) {output.Trim()}");
        return (output, p.ExitCode);
    }
}
