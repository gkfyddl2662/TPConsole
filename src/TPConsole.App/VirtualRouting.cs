using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using TPConsole.Core;

namespace TPConsole.App;

/// <summary>
/// Thesycon's DSP mixer plugin (tusbaudiodsp_mixer.sys): an upper filter on the E2x2's USB audio
/// device. With it the driver's virtual channels carry sound and can be routed (Device Parameters\Plugin).
///
/// Driver side (static analysis): the function driver loads the plugin
/// when Config\EnablePlugin != 0 and its license has the DSP-plugin feature bit (this PC: set),
/// and asks the upper filter for interface {ea47cd8b-ed19-429e-a183-af9f8e3e33ee}.
/// </summary>
public static class VirtualRouting
{
    public const string Service = "tusbaudiodsp_mixer";
    public static string BackupDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TPConsole", "backup");
    const string EnumKey = @"SYSTEM\CurrentControlSet\Enum\USB\VID_152A&PID_8756";
    static string DriverPath => Path.Combine(Environment.SystemDirectory, "drivers", Service + ".sys");
    static string ServiceKey => $@"SYSTEM\CurrentControlSet\Services\{Service}";

    /// <summary>Installed / enabled / loaded, and whether the plugin fits the driver. Read-only.</summary>
    public static JsonObject Status()
    {
        bool file = File.Exists(DriverPath);
        bool service, deleting;
        // A service whose driver is still loaded stays registered with DeleteFlag=1 until it unloads.
        using (var k = Registry.LocalMachine.OpenSubKey(ServiceKey)) { service = k is not null; deleting = k?.GetValue("DeleteFlag") is int f && f != 0; }
        bool filtered = false, enabled = false;
        foreach (var inst in Instances())
        {
            filtered |= UpperFilters(inst).Contains(Service, StringComparer.OrdinalIgnoreCase);
            using var cfg = Registry.LocalMachine.OpenSubKey($@"{EnumKey}\{inst}\Device Parameters\Config");
            enabled |= cfg?.GetValue("EnablePlugin") is int v && v != 0;
        }
        string? state = null;
        if (service)
        {
            try { using var sc = new ServiceController(Service); state = sc.Status.ToString(); } catch (Exception) { }
        }
        var version = file ? FileVersionInfo.GetVersionInfo(DriverPath).FileVersion : null;
        var driver = DriverVersion();
        return new JsonObject
        {
            ["installed"] = file && service && !deleting,
            // Removed, but the loaded driver keeps its file until the E2x2 re-plugs or Windows restarts.
            ["removing"] = deleting || (file && !service),
            ["enabled"] = filtered && enabled,
            // A half state (only one of the two switches) counts as off; turning it on/off repairs it.
            ["partial"] = filtered != enabled,
            ["loaded"] = state == nameof(ServiceControllerStatus.Running),
            ["version"] = version,
            ["driverVersion"] = driver,
            ["bundledVersion"] = BundledVersion,
            // Driver and plugin check an exact interface version: same release only.
            ["bundledCompatible"] = MajorMinor(BundledVersion) == MajorMinor(driver),
            ["compatible"] = version is null || MajorMinor(version) == MajorMinor(driver),
        };
    }

    /// <summary>Version of the E2x2's USB audio driver file (ToppingProUsbAudio).</summary>
    public static string? DriverVersion()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\ToppingProUsbAudio");
            var image = k?.GetValue("ImagePath") as string;
            if (image is null) return null;
            image = Environment.ExpandEnvironmentVariables(image.Replace(@"\SystemRoot\", @"%SystemRoot%\", StringComparison.OrdinalIgnoreCase));
            return File.Exists(image) ? FileVersionInfo.GetVersionInfo(image).FileVersion : null;
        }
        catch (Exception) { return null; }
    }

    static string? MajorMinor(string? v) => v?.Split('.') is [var major, var minor, ..] ? $"{major}.{minor}" : null;

    // ---- actions ---------------------------------------------------------------------------------

    /// <summary>
    /// The plugin is another company's signed driver file, so TPConsole doesn't ship it: the user provides a
    /// copy once (Settings → Virtual routing), kept here. One already installed in System32 is adopted.
    /// </summary>
    static readonly string PluginCopy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TPConsole", "plugin", Service + ".sys");

    static string? BundledFile()
    {
        if (!File.Exists(PluginCopy) && File.Exists(DriverPath)) SetPluginFile(DriverPath);
        return File.Exists(PluginCopy) ? PluginCopy : null;
    }

    static string? BundledVersion => BundledFile() is { } f ? FileVersionInfo.GetVersionInfo(f).FileVersion : null;

    /// <summary>Keeps a user-chosen tusbaudiodsp_mixer.sys for installing; "" or why it was refused.</summary>
    public static string SetPluginFile(string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        if (!string.Equals(info.OriginalFilename, Service + ".sys", StringComparison.OrdinalIgnoreCase))
            return $"Not a Thesycon mixer plugin ({info.OriginalFilename ?? Path.GetFileName(path)})";
        Directory.CreateDirectory(Path.GetDirectoryName(PluginCopy)!);
        File.Copy(path, PluginCopy, overwrite: true);
        return "";
    }

    /// <summary>Copies the bundled plugin and registers the (demand-start) service. Does not touch the device.</summary>
    public static string Install(Action<string> log)
    {
        if (BundledFile() is not { } plugin) return "Choose the plugin file (tusbaudiodsp_mixer.sys) first";
        if (MajorMinor(BundledVersion) != MajorMinor(DriverVersion()))
            return $"Plugin {BundledVersion} does not match the E2x2 driver {DriverVersion()}";
        if (Status()["removing"]!.GetValue<bool>())
            return "REMOVE_PENDING";
        Backup(log);
        File.Copy(plugin, DriverPath, overwrite: true);
        log($"copied plugin {BundledVersion} -> {DriverPath}");
        using (var k = Registry.LocalMachine.OpenSubKey(ServiceKey))
            if (k is null)
                Shell.Run("sc.exe", $"create {Service} type= kernel start= demand error= normal binPath= \"System32\\drivers\\{Service}.sys\"", log);
        WriteRestoreScript();
        return Status()["installed"]!.GetValue<bool>() ? "" : "Service was not created";
    }

    /// <summary>
    /// On: the filter sits above the E2x2's driver and the driver uses it. Restarts the E2x2 (sound drops
    /// out for a moment). If the device fails to start afterwards, it is switched back off automatically.
    /// </summary>
    public static string SetEnabled(bool on, Action<string> log)
    {
        if (on && !Status()["installed"]!.GetValue<bool>()) return "Not installed";
        Backup(log);
        var instances = Instances().ToList();
        foreach (var inst in instances)
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{EnumKey}\{inst}", writable: true);
            if (key is null) continue;
            var filters = UpperFilters(inst).Where(f => !f.Equals(Service, StringComparison.OrdinalIgnoreCase)).ToList();
            if (on) filters.Add(Service);
            if (filters.Count > 0) key.SetValue("UpperFilters", filters.ToArray(), RegistryValueKind.MultiString);
            else key.DeleteValue("UpperFilters", throwOnMissingValue: false);
            using var cfg = key.CreateSubKey(@"Device Parameters\Config");
            cfg.SetValue("EnablePlugin", on ? 1 : 0, RegistryValueKind.DWord);
            log($"{inst}: UpperFilters={string.Join(",", filters)} EnablePlugin={(on ? 1 : 0)}");
        }
        var since = DateTime.Now;
        if (!RestartAll(instances, log)) return Blocked(since);
        if (on && instances.Any(i => !Started(i, log)))
        {
            log("device did not start with the plugin -> switching it back off");
            SetEnabled(false, log);
            return "The E2x2 did not start with the plugin; switched back off";
        }
        return "";
    }

    /// <summary>Off (if on), then removes the service and the file.</summary>
    public static string Uninstall(Action<string> log)
    {
        // Virtual devices would be silent without the plugin: back to the driver's own devices first.
        bool restart = false;
        foreach (var inst in Instances())
        {
            using var cfg = Registry.LocalMachine.OpenSubKey($@"{EnumKey}\{inst}\Device Parameters\Config", writable: true);
            if (cfg is null) continue;
            foreach (var sf in OutFormats.Append("StreamFormatIn_01"))
            {
                using var k = cfg.OpenSubKey(sf);
                restart |= k?.GetValue("DefaultVirtualChannelProfile") is not null;
            }
            SelectAll(cfg, false, false);
        }
        var s = Status();
        if (s["enabled"]!.GetValue<bool>() || s["partial"]!.GetValue<bool>())
        {
            var r = SetEnabled(false, log);
            if (r.Length > 0) return r;
        }
        else if (restart) RestartAll(Instances().ToList(), log);
        Shell.Run("sc.exe", $"delete {Service}", log);
        try { File.Delete(DriverPath); log($"deleted {DriverPath}"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Still loaded (the E2x2 has not restarted since it was switched off): Windows deletes it at the next boot.
            log("driver still loaded; file is deleted at the next restart");
            MoveFileEx(DriverPath, null, 4 /* MOVEFILE_DELAY_UNTIL_REBOOT */);
        }
        return "";
    }

    // ---- virtual devices --------------------------------------------------------------------
    // Function-driver side (confirmed): Config\VirtualChannelProfiles\<name>\Vchan_NN,
    // selected per stream format by DefaultVirtualChannelProfile (Out = Windows playback, In = recording);
    // a SoundDeviceProfiles entry with Virtual=1 makes a Windows device whose ChannelIndex counts virtual channels.
    // Plugin side (from a public report, not yet checked against the .sys): Device Parameters\Plugin values
    // "w_<Src><ch>_<Dst><ch>" = gain, 0x1000000 = 0 dB.
    const string VOut = "tcvout", VIn = "tcvin", PlaybackProfile = "tcplbv", RecordingProfile = "tcrecv";
    const int Unity = 0x1000000;
    public const int MaxPerKind = 8;

    public static string Signature(List<VirtualDevice> devices, List<VirtualRoute> routes) =>
        System.Text.Json.JsonSerializer.Serialize(new { devices, routes });

    /// <summary>Stable pin GUID per virtual device (Windows keeps names/settings per pin).</summary>
    static string PinGuid(string kind, int id) => $"{{7c0a{(kind == "playback" ? 1 : 2)}{id:x3}-5443-4c00-8000-746f7070696e}}";

    /// <summary>
    /// Writes the virtual channels, Windows devices and plugin routes, then restarts the E2x2.
    /// The driver's own devices are always kept (a copy of plb/rec plus the extras). Reverts if the
    /// device does not start.
    /// </summary>
    public static string ApplyDevices(List<VirtualDevice> devices, List<VirtualRoute> routes, Action<string> log)
    {
        var plays = devices.Where(d => d.Kind == "playback").OrderBy(d => d.Id).Take(MaxPerKind).ToList();
        var recs = devices.Where(d => d.Kind == "recording").OrderBy(d => d.Id).Take(MaxPerKind).ToList();
        Backup(log);
        var instances = Instances().ToList();
        foreach (var inst in instances)
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{EnumKey}\{inst}", writable: true)!;
            using var cfg = key.CreateSubKey(@"Device Parameters\Config");
            WriteChannels(cfg, VOut, plays);
            WriteChannels(cfg, VIn, recs);
            WriteDevices(cfg, "plb", PlaybackProfile, plays, "playback");
            WriteDevices(cfg, "rec", RecordingProfile, recs, "recording");
            SelectAll(cfg, plays.Count > 0, recs.Count > 0);
            using var plugin = key.CreateSubKey(@"Device Parameters\Plugin");
            WriteRoutes(plugin, plays, recs, routes, log);
            log($"{inst}: {plays.Count} virtual playback, {recs.Count} virtual recording, {routes.Count} routes");
        }
        foreach (var d in plays.Concat(recs))
        {
            using var mc = Registry.LocalMachine.CreateSubKey($@"SYSTEM\CurrentControlSet\Control\MediaCategories\{PinGuid(d.Kind, d.Id)}");
            mc.SetValue("Name", Label(d));
        }
        var since2 = DateTime.Now;
        if (!RestartAll(instances, log)) return Blocked(since2);
        if (instances.Any(i => !Started(i, log)))
        {
            log("device did not start with the virtual devices -> back to the driver's own devices");
            foreach (var inst in instances)
            {
                using var cfg = Registry.LocalMachine.OpenSubKey($@"{EnumKey}\{inst}\Device Parameters\Config", writable: true);
                if (cfg is not null) SelectAll(cfg, false, false);
            }
            RestartAll(instances, log);
            return "The E2x2 did not start with these devices; back to its own devices";
        }
        return "";
    }

    static readonly string[] OutFormats = ["StreamFormatOut_01", "StreamFormatOut_02"];

    /// <summary>Selects our profiles for the stream formats, or the driver's own (plb/rec) when there is nothing virtual.</summary>
    static void SelectAll(RegistryKey cfg, bool plays, bool recs)
    {
        foreach (var sf in OutFormats) { SelectVirtual(cfg, sf, plays ? VOut : null); SelectProfile(cfg, sf, plays ? PlaybackProfile : "plb"); }
        SelectVirtual(cfg, "StreamFormatIn_01", recs ? VIn : null);
        SelectProfile(cfg, "StreamFormatIn_01", recs ? RecordingProfile : "rec");
    }

    public static string Label(VirtualDevice d) =>
        string.IsNullOrWhiteSpace(d.Name) ? (d.Kind == "playback" ? $"Virtual Playback {d.Id}" : $"Virtual Recording {d.Id}") : d.Name.Trim();

    static void WriteChannels(RegistryKey cfg, string profile, List<VirtualDevice> list)
    {
        cfg.DeleteSubKeyTree($@"VirtualChannelProfiles\{profile}", throwOnMissingSubKey: false);
        if (list.Count == 0) return;
        int ch = 0;
        foreach (var d in list)
            foreach (var side in new[] { "L", "R" })
            {
                using var v = cfg.CreateSubKey($@"VirtualChannelProfiles\{profile}\Vchan_{ch++:D2}");
                // ChannelName: required, at most 41 characters.
                var name = $"{Label(d)} {side}";
                v.SetValue("ChannelName", name.Length > 41 ? name[..41] : name);
                v.SetValue("BitsPerSample", 32, RegistryValueKind.DWord);
                v.SetValue("ExposeToAsio", 0, RegistryValueKind.DWord); // keep DAW channel lists as they are
                v.SetValue("Hidden", 0, RegistryValueKind.DWord);
            }
    }

    static void SelectVirtual(RegistryKey cfg, string streamFormat, string? profile)
    {
        using var k = cfg.OpenSubKey(streamFormat, writable: true);
        if (k is null) return;
        if (profile is null) k.DeleteValue("DefaultVirtualChannelProfile", throwOnMissingValue: false);
        else k.SetValue("DefaultVirtualChannelProfile", profile);
    }

    static void SelectProfile(RegistryKey cfg, string streamFormat, string profile)
    {
        using var k = cfg.OpenSubKey(streamFormat, writable: true);
        if (k is null) return;
        k.SetValue("DefaultSoundDeviceProfile", profile);
        // The driver reads Current before Default; keep it in step.
        if (k.GetValue("CurrentSoundDeviceProfile") is not null) k.SetValue("CurrentSoundDeviceProfile", profile);
    }

    /// <summary>Our profile = the driver's own (copied, never reduced) + one entry per virtual device.</summary>
    static void WriteDevices(RegistryKey cfg, string baseProfile, string profile, List<VirtualDevice> list, string kind)
    {
        cfg.DeleteSubKeyTree($@"SoundDeviceProfiles\{profile}", throwOnMissingSubKey: false);
        if (list.Count == 0) return;
        using var src = cfg.OpenSubKey($@"SoundDeviceProfiles\{baseProfile}") ?? throw new InvalidOperationException($"Driver profile {baseProfile} missing");
        using var dst = cfg.CreateSubKey($@"SoundDeviceProfiles\{profile}");
        CopyTree(src, dst);
        int next = src.GetSubKeyNames().Select(n => int.TryParse(n.AsSpan(2), out var i) ? i : 0).DefaultIfEmpty(0).Max() + 1;
        int ch = 0;
        foreach (var d in list)
        {
            using var sd = dst.CreateSubKey($"sd{next++}");
            sd.SetValue("DisplayName", Label(d));
            sd.SetValue("PinCategoryGuid", "{DFF21FE3-F70F-11D0-B917-00A0C9223196}");
            sd.SetValue("PinNameGuid", PinGuid(kind, d.Id));
            sd.SetValue("Type", 0, RegistryValueKind.DWord);
            sd.SetValue("Virtual", 1, RegistryValueKind.DWord);
            for (int c = 0; c < 2; c++)
            {
                using var loc = sd.CreateSubKey($"Loc_{c:D2}");
                loc.SetValue("ChannelIndex", ch++, RegistryValueKind.DWord);
            }
        }
    }

    static void CopyTree(RegistryKey from, RegistryKey to)
    {
        foreach (var name in from.GetValueNames()) to.SetValue(name, from.GetValue(name)!, from.GetValueKind(name));
        foreach (var sub in from.GetSubKeyNames())
        {
            using var f = from.OpenSubKey(sub)!;
            using var t = to.CreateSubKey(sub);
            CopyTree(f, t);
        }
    }

    /// <summary>Plugin routes, per channel (L→L, R→R). Our values are rewritten; others are left alone.</summary>
    static void WriteRoutes(RegistryKey plugin, List<VirtualDevice> plays, List<VirtualDevice> recs,
        List<VirtualRoute> routes, Action<string> log)
    {
        foreach (var n in plugin.GetValueNames().Where(n => n.StartsWith("w_", StringComparison.Ordinal))) plugin.DeleteValue(n);
        (string Name, int Ch)? Side(string end, bool from)
        {
            var (kind, arg) = (end.Split(':')[0], end.Split(':').ElementAtOrDefault(1));
            if (!int.TryParse(arg, out var n)) return null;
            switch (kind)
            {
                case "v":
                    int pi = plays.FindIndex(d => d.Id == n), ri = recs.FindIndex(d => d.Id == n);
                    if (from && pi >= 0) return ("VirtIn", pi * 2);
                    if (!from && ri >= 0) return ("VirtOut", ri * 2);
                    return null;
                case "hwin" when from && n is >= 0 and < 5: return ("DevIn", n * 2);
                // What Windows apps play on Playback 1/2..7/8 (before it reaches the E2x2).
                case "appin" when from && n is >= 0 and < 4: return ("AppIn", n * 2);
                case "hwout" when !from && n is >= 0 and < 4: return ("DevOut", n * 2);
                case "apprec" when !from && n is >= 0 and < 5: return ("AppOut", n * 2);
                default: return null;
            }
        }
        // Format 1 = named crossings (w_VirtIn0_DevOut0). Without a value the plugin uses the identity
        // matrix: hardware playback/recording pass straight through (kept), but virtual playback k also
        // loops into virtual recording k — switched off here unless asked for.
        plugin.SetValue("WeightRegistryFormat", 1, RegistryValueKind.DWord);
        int vin = plays.Count * 2, vout = recs.Count * 2;
        for (int k = 0; k < Math.Min(vin, vout); k++) plugin.SetValue($"w_VirtIn{k}_VirtOut{k}", 0, RegistryValueKind.DWord);
        foreach (var r in routes)
        {
            if (Side(r.From, true) is not { } a || Side(r.To, false) is not { } b) { log($"skipped route {r.From} -> {r.To}"); continue; }
            for (int c = 0; c < 2; c++)
                plugin.SetValue($"w_{a.Name}{a.Ch + c}_{b.Name}{b.Ch + c}", Unity, RegistryValueKind.DWord);
        }
    }

    // ---- helpers ---------------------------------------------------------------------------

    static IEnumerable<string> Instances()
    {
        using var root = Registry.LocalMachine.OpenSubKey(EnumKey);
        if (root is null) yield break;
        foreach (var inst in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(inst);
            // Only the node the USB audio driver runs on (not stale entries of other drivers).
            if (string.Equals(k?.GetValue("Service") as string, "ToppingProUsbAudio", StringComparison.OrdinalIgnoreCase)) yield return inst;
        }
    }

    static string[] UpperFilters(string inst)
    {
        using var k = Registry.LocalMachine.OpenSubKey($@"{EnumKey}\{inst}");
        return k?.GetValue("UpperFilters") as string[] ?? [];
    }


    /// <summary>Whether the device runs after a restart (Configuration Manager: started, no problem code).</summary>
    static bool Started(string inst, Action<string> log)
    {
        var id = $@"USB\VID_152A&PID_8756\{inst}";
        for (int i = 0; i < 20; i++)
        {
            if (CM_Locate_DevNodeW(out var node, id, 0) == 0 && CM_Get_DevNode_Status(out var status, out var problem, node, 0) == 0)
            {
                if ((status & 0x8 /* DN_STARTED */) != 0 && problem == 0) return true;
                if (problem != 0 && i > 4) { log($"device problem code {problem}"); return false; }
            }
            Thread.Sleep(500);
        }
        log("device not started");
        return false;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);
    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);

    static void Backup(Action<string> log)
    {
        Directory.CreateDirectory(BackupDir);
        var file = Path.Combine(BackupDir, $"e2x2_enum_before_plugin_{DateTime.Now:yyyyMMdd_HHmmss}.reg");
        Shell.Run("reg", $"export \"HKLM\\{EnumKey}\" \"{file}\" /y", log);
    }

    /// <summary>
    /// A script that undoes everything without this app (e.g. from Safe Mode, or with the E2x2 unplugged
    /// if the plugin ever crashes Windows at start-up). Written next to the registry backups.
    /// </summary>
    static void WriteRestoreScript()
    {
        var path = Path.Combine(BackupDir, "remove-virtual-routing.ps1");
        File.WriteAllText(path, $$"""
            # Removes Thesycon's DSP mixer plugin from the E2x2 OTG (run in an administrator PowerShell).
            # If Windows crashes at start-up: unplug the E2x2, start Windows (or Safe Mode), then run this.
            $enum = 'HKLM:\{{EnumKey}}'
            Get-ChildItem $enum | ForEach-Object {
                $p = Get-ItemProperty $_.PSPath
                if ($p.UpperFilters) {
                    $rest = @($p.UpperFilters | Where-Object { $_ -ne '{{Service}}' })
                    if ($rest.Count) { Set-ItemProperty $_.PSPath UpperFilters $rest -Type MultiString }
                    else { Remove-ItemProperty $_.PSPath UpperFilters }
                }
                $cfg = Join-Path $_.PSPath 'Device Parameters\Config'
                if (Test-Path $cfg) {
                    Set-ItemProperty $cfg EnablePlugin 0 -Type DWord
                    # Back to the driver's own devices (plb/rec), without the virtual ones.
                    foreach ($sf in 'StreamFormatOut_01', 'StreamFormatOut_02', 'StreamFormatIn_01') {
                        $k = Join-Path $cfg $sf
                        if (!(Test-Path $k)) { continue }
                        $own = if ($sf -like '*In*') { 'rec' } else { 'plb' }
                        Set-ItemProperty $k DefaultSoundDeviceProfile $own
                        if ((Get-ItemProperty $k).CurrentSoundDeviceProfile) { Set-ItemProperty $k CurrentSoundDeviceProfile $own }
                        Remove-ItemProperty $k DefaultVirtualChannelProfile -ErrorAction SilentlyContinue
                    }
                    foreach ($p2 in 'SoundDeviceProfiles\{{PlaybackProfile}}', 'SoundDeviceProfiles\{{RecordingProfile}}', 'VirtualChannelProfiles\{{VOut}}', 'VirtualChannelProfiles\{{VIn}}') {
                        Remove-Item (Join-Path $cfg $p2) -Recurse -ErrorAction SilentlyContinue
                    }
                }
            }
            sc.exe delete {{Service}}
            Remove-Item "$env:SystemRoot\System32\drivers\{{Service}}.sys" -ErrorAction SilentlyContinue
            Get-ChildItem $enum | Where-Object { (Get-ItemProperty $_.PSPath).Service -eq 'ToppingProUsbAudio' } |
                ForEach-Object { pnputil /restart-device "USB\VID_152A&PID_8756\$($_.PSChildName)" }
            # Full registry state from before: reg import "<backup .reg in this folder>"
            """);
    }

    /// <summary>
    /// Restarts the E2x2 so its driver stack is rebuilt (filters, profiles). False when Windows refused
    /// (pnputil exit 3010): something still has the device open — a DAW on ASIO, the audio engine while
    /// sound plays, TPConsole itself. The registry is set either way; it applies at the next re-plug/reboot.
    /// </summary>
    static bool _rebootPending;
    static bool RestartAll(List<string> instances, Action<string> log)
    {
        bool ok = true;
        _rebootPending = false;
        foreach (var inst in instances)
        {
            var (_, code) = Shell.Run("pnputil", $"/restart-device \"USB\\VID_152A&PID_8756\\{inst}\"", log);
            // 0 = restarted. 3010 = refused now (in use); 50 = refused because an earlier refusal left the
            // device waiting for a reboot — it stays that way until the E2x2 is re-plugged or Windows restarts.
            if (code != 0) ok = false;
            if (code == 50) _rebootPending = true;
        }
        return ok;
    }

    /// <summary>"RESTART_BLOCKED|app1, app2": the programs Windows says vetoed the restart (event 225).</summary>
    static string Blocked(DateTime since)
    {
        var apps = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var q = new System.Diagnostics.Eventing.Reader.EventLogQuery("System", System.Diagnostics.Eventing.Reader.PathType.LogName,
                $"*[System[Provider[@Name='Microsoft-Windows-Kernel-PnP'] and (EventID=225) and TimeCreated[@SystemTime>='{since.AddSeconds(-2).ToUniversalTime():yyyy-MM-ddTHH:mm:ss.fffZ}']]]");
            using var r = new System.Diagnostics.Eventing.Reader.EventLogReader(q);
            for (var e = r.ReadEvent(); e is not null; e = r.ReadEvent())
                using (e)
                {
                    var m = System.Text.RegularExpressions.Regex.Match(e.FormatDescription() ?? "", @"\\([^\\]+\.exe)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (m.Success) apps.Add(m.Groups[1].Value);
                }
        }
        catch (Exception) { }
        return "RESTART_BLOCKED|" + string.Join(", ", apps) + (_rebootPending ? "|pending" : "");
    }


    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool MoveFileEx(string existing, string? newName, int flags);
}
