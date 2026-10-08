using System.Text.Json;
using System.Text.Json.Serialization;

namespace TPConsole.Core;

/// <summary>A named snapshot of the whole mixer (routing and levels).</summary>
public sealed class Preset
{
    public string Name { get; set; } = "";
    public Mixer Mixer { get; set; } = new();
    /// <summary>Optional Windows side: app outputs, device volumes, default devices (applied by the app host).</summary>
    public System.Text.Json.Nodes.JsonObject? Windows { get; set; }
}

/// <summary>Switch to a preset while an app runs (and optionally back when it closes).</summary>
public sealed class PresetRule
{
    public string App { get; set; } = "";
    public string Preset { get; set; } = "";
    public bool Revert { get; set; } = true;
}

public sealed class AppSettings
{
    /// <summary>"auto" (follow Windows), "en" or "ko".</summary>
    public string Language { get; set; } = "auto";
    /// <summary>"dark" (default), "light" or "auto" (follow Windows app mode).</summary>
    public string Theme { get; set; } = "dark";
    /// <summary>First-run hints dismissed.</summary>
    public bool HintsSeen { get; set; }
    /// <summary>E2x2 sources/loopbacks use the Windows device name; renaming one renames the device.</summary>
    public bool SyncWindowsNames { get; set; } = true;
    /// <summary>Whole-interface scale (1 = 100%).</summary>
    public double UiScale { get; set; } = 1;
    /// <summary>Routing graph zoom (1 = 100%).</summary>
    public double RoutingZoom { get; set; } = 1;
    /// <summary>Closing the window keeps the app running in the tray.</summary>
    public bool CloseToTray { get; set; } = true;
    public List<Hotkey> Hotkeys { get; set; } = [];
    /// <summary>Keep the device's own memory (used without a PC) in step with the app, automatically.</summary>
    public bool AutoStoreOnDevice { get; set; } = true;
    public bool HideServiceSessions { get; set; }
    public List<PresetRule> PresetRules { get; set; } = [];
    /// <summary>Order of the send rows inside each mix ("0".."3" -> source keys).</summary>
    public Dictionary<string, List<string>> MixOrder { get; set; } = [];
    public bool MiniOnTop { get; set; } = true;
    /// <summary>Screen refresh for meters and wires in Hz (0 = off). The E2x2's own levels arrive at ~15 Hz.</summary>
    public int MeterRate { get; set; } = 30;
    /// <summary>Install a newer GitHub release and restart (waits while the window is in use).</summary>
    public bool AutoUpdate { get; set; } = true;
}

/// <summary>A global shortcut. Action: "muteInput:0" (IN 1), "muteInput:2" (IN 2), "muteInput:1" (Mobile IN),
/// "muteOutput:out12", "preset:Name", "nextPreset".</summary>
public sealed class Hotkey
{
    public string Action { get; set; } = "";
    public bool Ctrl { get; set; }
    public bool Alt { get; set; }
    public bool Shift { get; set; }
    public bool Win { get; set; }
    /// <summary>Windows virtual-key code.</summary>
    public int Key { get; set; }
}

/// <summary>Device-wide settings and info the device reports (HID 11.xx / 12.xx); null until reported.</summary>
public sealed class DeviceInfo
{
    public bool? AutoStandby { get; set; }      // 11.02
    public bool? MobileApp { get; set; }        // 11.03
    public int? Brightness { get; set; }        // 11.04: 0 dim, 1 normal, 2 bright
    public string? HardwareVersion { get; set; } // 12.01
    public string? SoftwareVersion { get; set; } // 12.02
}

/// <summary>Everything the app persists: the mixer plus user labels for mixes/sources.</summary>
public sealed class Profile
{
    public AppSettings Settings { get; set; } = new();
    public List<Preset> Presets { get; set; } = [];
    /// <summary>Hash of the mixer last written to the device's memory, and when.</summary>
    public string? StoredMixer { get; set; }
    /// <summary>Preset last loaded or saved; the UI shows unsaved changes against it.</summary>
    public string? ActivePreset { get; set; }

    public Mixer Mixer { get; set; } = new();
    public Dictionary<string, string> Names { get; set; } = [];
    /// <summary>ASIO host process name -> playback pair (0..3) it outputs to. ASIO bypasses Windows,
    /// so this can't be observed by Windows; it's auto-detected from driver meters or dragged by the user.
    /// -1 = cleared by the user (don't auto-detect).</summary>
    public Dictionary<string, int> AsioRoutes { get; set; } = [];
    /// <summary>Extra Windows devices on the driver's virtual channels (needs the DSP mixer plugin).</summary>
    public List<VirtualDevice> VirtualDevices { get; set; } = [];
    /// <summary>Plugin routes between virtual and hardware channels (stereo pairs).</summary>
    public List<VirtualRoute> VirtualRoutes { get; set; } = [];
    /// <summary>Devices + routes as last written to the registry (JSON), to show "not applied yet".</summary>
    public string? VirtualApplied { get; set; }
}

/// <summary>A stereo Windows device on two virtual channels. Kind: "playback" or "recording".</summary>
public sealed class VirtualDevice
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "playback";
}

/// <summary>
/// One stereo connection inside the plugin. From: "v:{id}" (virtual playback), "hwin:{0..4}"
/// (Analog 1/2, Mobile IN, Loopback 1/2, 3/4, 5/6) or "appin:{0..3}" (what Windows plays on Playback 1/2..7/8). To: "v:{id}" (virtual recording),
/// "hwout:{0..3}" (Playback 1/2..7/8 into the E2x2) or "apprec:{0..4}" (an existing recording device).
/// </summary>
public sealed class VirtualRoute
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

/// <summary>
/// Owns the device connection and the profile. Mutations go through <see cref="Apply"/>, which
/// sends only the wire values that changed. The device can't be read back, so on every
/// (re)connect the whole profile is pushed, like Control Center does.
/// </summary>
public sealed class Engine : IDisposable
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    readonly string _path;
    readonly Lock _lock = new();
    readonly Dictionary<Param, int> _sent = [];
    readonly Dictionary<Param, int> _meters = [];
    readonly CancellationTokenSource _cts = new();
    E2x2Device? _device;
    Timer? _saveTimer;

    public Profile Profile { get; }
    public bool Connected => _device is not null;
    /// <summary>Front-panel Input/Playback knob (35.03), 0..100; null until the device reports it.</summary>
    public int? MonitorMixKnob { get; private set; }
    public DeviceInfo Device { get; } = new();

    /// <summary>Raised (on a worker thread) when the profile or connection status changed.</summary>
    public event Action? Changed;

    readonly bool _readOnly;

    /// <param name="readOnly">For diagnostics (CLI): never stores to the device's flash (11.05) or rewrites profile.json on exit.</param>
    /// <param name="paused">Start without opening the device (TOPPING Control Center owns it); see <see cref="Pause"/>.</param>
    public Engine(bool readOnly = false, bool paused = false)
    {
        _readOnly = readOnly;
        _paused = paused;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        // The app used to be called ToppingCtl: its settings, backups and logs move over once.
        string legacy = Path.Combine(appData, "ToppingCtl"), dir = Path.Combine(appData, "TPConsole");
        if (!readOnly && Directory.Exists(legacy) && !Directory.Exists(dir))
            try { Directory.Move(legacy, dir); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        _path = Path.Combine(dir, "profile.json");
        Profile = LoadProfile(_path);
        if (File.Exists(_path)) Log($"loaded profile saved {File.GetLastWriteTime(_path):yyyy-MM-dd HH:mm:ss}; MON {string.Join(" ", Profile.Mixer.Inputs.Select(i => i.Monitor ? 1 : 0))}");
        _ = Task.Run(() => RunAsync(_cts.Token));
    }

    static Profile LoadProfile(string path)
    {
        if (File.Exists(path)) return JsonSerializer.Deserialize<Profile>(File.ReadAllText(path), Json)!;
        // First run: start from Control Center's current workspace so nothing changes audibly.
        var ws = Directory.Exists(ControlCenterImport.DefaultFolder)
            ? Directory.GetFiles(ControlCenterImport.DefaultFolder, "*.TPwork").OrderByDescending(File.GetLastWriteTime).FirstOrDefault()
            : null;
        return new Profile { Mixer = ws is null ? new Mixer() : ControlCenterImport.Load(ws) };
    }

    public void Apply(Action<Profile> change)
    {
        lock (_lock)
        {
            var before = InputFlags();
            change(Profile);
            LogInputChanges(before, "app");
            try { Push(full: false); }
            finally
            {
                // Saved even when the device write fails: the profile already holds the change.
                ScheduleSave();
                ScheduleStore();
            }
        }
        Changed?.Invoke();
    }

    // ---- device memory: written 5 s after the last change (a fader drag is many changes), and on exit -------
    Timer? _storeTimer;
    string MixerHash => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(JsonSerializer.SerializeToUtf8Bytes(Profile.Mixer, Json)));
    public bool StoredUpToDate => Profile.StoredMixer == MixerHash;

    void ScheduleStore()
    {
        if (!Profile.Settings.AutoStoreOnDevice) return;
        _storeTimer ??= new Timer(_ => StoreIfChanged());
        _storeTimer.Change(TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Re-sends everything and re-stores it on the device. Used after TOPPING Control Center ran: it
    /// pushes its own setup when it starts and stores it in the device's memory when it exits.
    /// </summary>
    public void Resync()
    {
        Log("Control Center closed: our setup sent again");
        Pause(false);
        lock (_lock)
        {
            _sent.Clear();
            Push(full: true);
            Profile.StoredMixer = null; // the device memory now holds Control Center's setup
            ScheduleStore();
        }
        Changed?.Invoke();
    }

    public void StoreIfChanged()
    {
        if (StoredUpToDate) return;
        if (StoreOnDevice()) Changed?.Invoke();
    }

    static Mixer Clone(Mixer m) => JsonSerializer.Deserialize<Mixer>(JsonSerializer.Serialize(m, Json), Json)!;

    /// <summary>Saves the current mixer under a name (overwrites a preset with that name).</summary>
    public void SavePreset(string name) => Apply(p =>
    {
        var existing = p.Presets.FirstOrDefault(x => x.Name == name);
        if (existing is null) p.Presets.Add(new Preset { Name = name, Mixer = Clone(p.Mixer) });
        else existing.Mixer = Clone(p.Mixer);
        p.ActivePreset = name;
    });

    /// <summary>Switches the device to a preset; only the values that differ are sent.</summary>
    public void LoadPreset(string name) => Apply(p =>
    {
        var preset = p.Presets.FirstOrDefault(x => x.Name == name);
        if (preset is null) return;
        p.Mixer = Clone(preset.Mixer);
        p.ActivePreset = name;
    });

    public void RenamePreset(string name, string newName) => Apply(p =>
    {
        var preset = p.Presets.FirstOrDefault(x => x.Name == name);
        if (preset is null || p.Presets.Any(x => x.Name == newName)) return;
        preset.Name = newName;
        if (p.ActivePreset == name) p.ActivePreset = newName;
    });

    public void DeletePreset(string name) => Apply(p =>
    {
        p.Presets.RemoveAll(x => x.Name == name);
        if (p.ActivePreset == name) p.ActivePreset = null;
    });

    /// <summary>
    /// Stores the current setup in the device's flash (HID 11.05), so it starts with it without a PC.
    /// The device has one slot. Flash wears, so this is only ever sent on an explicit user action.
    /// </summary>
    public bool StoreOnDevice()
    {
        lock (_lock)
        {
            if (_device is null) return false;
            Push(full: true); // make sure the device holds exactly what we show
            try { _device.Write(new Frame(0x11, 0x05, 1)); }
            catch (IOException) { return false; }
            Profile.StoredMixer = MixerHash;
            ScheduleSave();
            return true;
        }
    }

    public Dictionary<Param, int> SnapshotMeters()
    {
        lock (_meters) return new(_meters);
    }

    // Caller holds _lock.
    void Push(bool full)
    {
        if (_device is null) return;
        foreach (var f in Profile.Mixer.ToFrames())
        {
            if (!full && _sent.TryGetValue(f.Param, out var v) && v == f.Value) continue;
            try { _device.Write(f); }
            catch (IOException) { return; } // reader loop notices the disconnect and reconnects
            _sent[f.Param] = f.Value;
        }
    }

    // Saved 500 ms after the last change, but never later than 2 s after the first unsaved one, so a
    // stream of changes can't postpone it forever.
    long _dirtySince;

    void ScheduleSave()
    {
        if (_readOnly) return;
        _saveTimer ??= new Timer(_ => Save());
        long now = Environment.TickCount64;
        if (_dirtySince == 0) _dirtySince = now;
        _saveTimer.Change(Math.Max(0, Math.Min(500, _dirtySince + 2000 - now)), Timeout.Infinite);
    }

    public void Save()
    {
        if (_readOnly) return;
        try
        {
            string json;
            lock (_lock) { json = JsonSerializer.Serialize(Profile, Json); _dirtySince = 0; }
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path + ".tmp", json);
            File.Move(_path + ".tmp", _path, overwrite: true);
        }
        catch (Exception e) { Log($"profile save failed: {e.GetType().Name}: {e.Message}"); }
    }

    // ---- engine.log: profile load/save problems and every input-button change with its source ----
    readonly string _log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TPConsole", "engine.log");

    public void Log(string line)
    {
        if (_readOnly) return;
        try
        {
            if (File.Exists(_log) && new FileInfo(_log).Length > 256 * 1024) File.Move(_log, _log + ".old", overwrite: true);
            File.AppendAllText(_log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static readonly string[] InputNames = ["IN 1", "Mobile IN L", "IN 2", "Mobile IN R"];

    // Caller holds _lock.
    (bool, bool, bool, bool)[] InputFlags() =>
        Profile.Mixer.Inputs.Select(i => (i.Monitor, i.Phantom48V, i.Instrument, i.Mute)).ToArray();

    void LogInputChanges((bool Mon, bool P48, bool Inst, bool Mute)[] before, string source)
    {
        var after = InputFlags();
        for (int i = 0; i < Math.Min(before.Length, after.Length); i++)
        {
            void One(string name, bool a, bool b) { if (a != b) Log($"{InputNames.ElementAtOrDefault(i) ?? $"input {i}"} {name} {(b ? "on" : "off")} ({source})"); }
            One("MON", before[i].Mon, after[i].Item1);
            One("48V", before[i].P48, after[i].Item2);
            One("INST", before[i].Inst, after[i].Item3);
            One("MUTE", before[i].Mute, after[i].Item4);
        }
    }

    volatile bool _paused;

    /// <summary>
    /// Closes the HID connection (and keeps it closed) so Windows can restart the E2x2 — an open
    /// handle vetoes the restart. Resuming reconnects and re-sends the whole setup.
    /// </summary>
    public void Pause(bool on)
    {
        _paused = on;
        if (on) lock (_lock) { _device?.Dispose(); _device = null; }
    }

    async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                while (_paused) await Task.Delay(200, ct);
                var dev = E2x2Device.Open();
                // Start reading first so the device's replies to the connect request aren't lost.
                var reader = Task.Run(async () => { await foreach (var f in dev.ReadFramesAsync(ct)) OnFrame(f); }, ct);
                lock (_lock)
                {
                    _device = dev;
                    _sent.Clear();
                    Push(full: true);
                }
                Log("E2x2 connected: full setup sent");
                Changed?.Invoke();
                // "Connect" request: the device answers with its settings and versions. Repeat until it does.
                for (int i = 0; i < 5 && Device.SoftwareVersion is null && !reader.IsCompleted; i++)
                {
                    lock (_lock) dev.Write(new Frame(0x11, 0x01, 1));
                    await Task.WhenAny(reader, Task.Delay(2000, ct));
                }
                await reader;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or ObjectDisposedException) { }
            lock (_lock)
            {
                if (_device != null) Log("E2x2 disconnected");
                _device?.Dispose();
                _device = null;
            }
            Changed?.Invoke();
            try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { break; }
        }
    }

    void OnFrame(Frame f)
    {
        if (Meters.IsMeter(f.Param))
        {
            lock (_meters) _meters[f.Param] = f.Value;
            return;
        }
        // Anything the device says on its own besides meters and the monitor-mix knob (button presses,
        // settings it re-announces after a reset) goes to engine.log for problem reports.
        if (f.Param != new Param(0x35, 0x03)) Log($"device sent {f}");
        // Front-panel controls. The device already applied them, so record without re-sending.
        bool changed = true;
        lock (_lock)
        {
            var before = InputFlags();
            var m = Profile.Mixer;
            switch (f.Addr, f.Sub)
            {
                case ( >= 0x21 and <= 0x24, 0x01): m.Inputs[f.Addr - 0x21].Monitor = f.Value != 0; break;
                case ( >= 0x21 and <= 0x24, 0x02): m.Inputs[f.Addr - 0x21].Phantom48V = f.Value != 0; break;
                case ( >= 0x21 and <= 0x24, 0x03): m.Inputs[f.Addr - 0x21].Instrument = f.Value != 0; break;
                case (0x35, 0x02): m.PhoneGainHigh1 = f.Value != 0; break;
                case (0x36, 0x02): m.PhoneGainHigh2 = f.Value != 0; break;
                case (0x35, 0x03): MonitorMixKnob = f.Value; break;
                case (0x11, 0x02): Device.AutoStandby = f.Value != 0; break;
                case (0x11, 0x03): Device.MobileApp = f.Value != 0; break;
                case (0x11, 0x04): Device.Brightness = f.Value; break;
                case (0x12, 0x01): Device.HardwareVersion = Version(f.Value); break;
                case (0x12, 0x02): Device.SoftwareVersion = Version(f.Value); break;
                default: changed = false; break;
            }
            LogInputChanges(before, "device button");
            if (changed && f.Addr is not (0x11 or 0x12) && f.Param != new Param(0x35, 0x03))
            {
                _sent[f.Param] = f.Value;
                ScheduleSave();
            }
        }
        if (changed) Changed?.Invoke();
    }

    // Control Center shows versions as V<major>.<minor:00> from hi16.lo16.
    static string Version(int v) => $"V{(v >> 16) & 0xFFFF}.{v & 0xFFFF:00}";

    /// <summary>Device-wide settings (not part of the mixer sync): 11.02 standby, 11.03 mobile app, 11.04 brightness.</summary>
    public void SetDeviceSetting(byte sub, int value)
    {
        if (sub is not (0x02 or 0x03 or 0x04)) throw new ArgumentOutOfRangeException(nameof(sub));
        lock (_lock)
        {
            if (_device is null) return;
            try { _device.Write(new Frame(0x11, sub, value)); } catch (IOException) { return; }
        }
        // Same bookkeeping as when the device reports the setting itself.
        OnFrame(new Frame(0x11, sub, value));
    }

    public void Dispose()
    {
        // Like Control Center on exit: the device keeps the current setup for use without a PC.
        if (!_readOnly && Profile.Settings.AutoStoreOnDevice) StoreIfChanged();
        _storeTimer?.Dispose();
        _cts.Cancel();
        _saveTimer?.Dispose();
        if (!_readOnly) Save();
        lock (_lock) _device?.Dispose();
    }
}

public static class Meters
{
    /// <summary>Device -> PC level meters, value = dB x 10.</summary>
    public static bool IsMeter(Param p) => p switch
    {
        { Sub: 0x04 } when p.Addr is >= 0x21 and <= 0x24 => true,
        { Sub: 0x01 or 0x02 } when p.Addr is >= 0x31 and <= 0x34 => true,
        { Sub: 0x01 } when p.Addr is >= 0x41 and <= 0x48 => true,
        { Sub: 0x01 or 0x02 } when p.Addr is >= 0x51 and <= 0x56 => true,
        { Sub: 0x02 } when p.Addr is 0x5A or 0x5B => true,
        _ => false,
    };
}
