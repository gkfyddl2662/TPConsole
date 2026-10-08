using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using TPConsole.Core;

namespace TPConsole.App;

// Hosts the web UI (wwwroot, built from /web) and bridges it to the Engine, Windows audio and the driver.
// The message protocol (ops and message types) is defined by web/src/lib/bridge.ts.
public partial class MainWindow : Window
{
    // TOPPING Control Center and TPConsole must not both drive the E2x2: while it runs, the engine lets go.
    static bool ControlCenterRunning() => System.Diagnostics.Process.GetProcessesByName("ToppingPro").Length > 0;
    readonly Engine _engine = new(paused: ControlCenterRunning());
    readonly WindowsAudio _audio = new();
    readonly DispatcherTimer _meterTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    readonly Tray _tray;
    Hotkeys? _hotkeys;
    bool _ready, _quitting, _webStarted;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            ApplyTheme();
            _hotkeys = new Hotkeys(new WindowInteropHelper(this).Handle, _engine, LoadPreset);
            SyncHotkeys();
        };
        // The web UI starts the first time the window is shown (not while sitting in the tray).
        IsVisibleChanged += async (_, _) =>
        {
            if (!IsVisible || _webStarted) return;
            _webStarted = true;
            await InitWebAsync();
            _ = CheckLatestAsync();
            RefreshStartup();
        };
        _tray = new Tray(_engine, ShowFromTray, Quit, n => Dispatcher.BeginInvoke(() => LoadPreset(n)));
        _engine.Changed += () => Dispatcher.BeginInvoke(() => { SendState(); SyncHotkeys(); ApplyMeterRate(); ApplyTheme(); });
        _updater.Updated += () => Dispatcher.BeginInvoke(() => { SendState(); MaybeUpdate(); });
        // Auto-update waits while the window is in use.
        Deactivated += (_, _) => MaybeUpdate();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        _perfTimer.Tick += (_, _) => SendPerf();
        _audio.VirtualDevices = () => _engine.Profile.VirtualDevices.ToList();
        _ = Task.Run(() => { _virtualRouting = SafeVirtualStatus(); Dispatcher.BeginInvoke(SendState); });
        _audio.TopologyChanged += () => Dispatcher.BeginInvoke(SendWindows);
        _audio.StatsLog = _engine.Log;
        _meterTimer.Tick += (_, _) => SendMeters();
        // Watch TOPPING Control Center: while it runs TPConsole leaves the E2x2 alone; when it closes, our
        // setup goes back (Engine.Resync). One process list per tick also serves automatic presets.
        var ccTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _ccRunning = ControlCenterRunning();
        ccTimer.Tick += (_, _) =>
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in System.Diagnostics.Process.GetProcesses()) { names.Add(p.ProcessName); p.Dispose(); }
            CheckPresetRules(names);
            bool running = names.Contains("ToppingPro");
            if (running == _ccRunning) return;
            _ccRunning = running;
            if (running) { _engine.Log("Control Center started: TPConsole paused"); _engine.Pause(true); }
            SendState();
            // Give it a moment to finish its exit-time write to the device.
            if (!running) Task.Delay(2000).ContinueWith(_ => { if (!_ccRunning) _engine.Resync(); });
        };
        ccTimer.Start();
        Closing += (_, e) =>
        {
            if (_quitting || !_engine.Profile.Settings.CloseToTray) return;
            e.Cancel = true; // keep controlling the device from the tray
            Hide();
            SetActive(false);
        };
        StateChanged += (_, _) => SetActive(WindowState != WindowState.Minimized && IsVisible);
        Closed += (_, _) => { Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged; _updater.Dispose(); _hotkeys?.Dispose(); _tray.Dispose(); _engine.Dispose(); _audio.Dispose(); };
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        SetActive(true);
    }

    bool _ccRunning;

    // ---- auto-update (Settings → Updates; see Updater) -----------------------------------------
    readonly Updater _updater = new();

    void MaybeUpdate()
    {
        if (_updater.Available != null && !_updater.AvailableFailedBefore && _engine.Profile.Settings.AutoUpdate && !IsActive)
            ApplyUpdate();
    }

    bool _updating;

    async void ApplyUpdate()
    {
        if (_quitting || _updating || !Updater.Installed) return;
        _updating = true;
        SendState();
        // Come back the way we were: in the tray unless the window was open.
        bool tray = !IsVisible || WindowState == WindowState.Minimized;
        try
        {
            if (await _updater.StartAsync(tray)) { Quit(); return; }
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or IOException or TaskCanceledException or System.ComponentModel.Win32Exception)
        {
            Result("update", e.Message);
        }
        _updating = false;
        SendState();
    }

    JsonObject UpdateStatus() => new()
    {
        ["current"] = Updater.Current.ToString(3),
        ["available"] = _updater.Available?.Version.ToString(3),
        ["failed"] = _updater.AvailableFailedBefore,
        ["installed"] = Updater.Installed,
        ["source"] = $"github.com/{Updater.Repo}",
        ["error"] = _updater.Error,
        ["updating"] = _updating,
    };

    // ---- problem report: the logs in one file to attach anywhere ------------------------------
    /// <summary>Versions plus the tails of engine.log, update.log and virtual-routing.log, with the user's
    /// profile path masked. Written to %TEMP%; returns its path.</summary>
    string WriteLogReport()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TPConsole");
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"TPConsole {Updater.Current} · Windows {Environment.OSVersion.Version} · driver {VirtualRouting.DriverVersion() ?? "?"} · " +
                      $"E2x2 firmware {_engine.Device.SoftwareVersion ?? "?"} · {(_engine.Connected ? "connected" : "not connected")} · {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        foreach (var (name, lines) in new[] { ("engine.log", 400), ("update.log", 50), (Path.Combine("backup", "virtual-routing.log"), 200) })
        {
            var path = Path.Combine(dir, name);
            sb.AppendLine().AppendLine($"===== {name} =====");
            if (!File.Exists(path)) { sb.AppendLine("(none)"); continue; }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var all = reader.ReadToEnd().Split('\n');
            foreach (var line in all.Skip(Math.Max(0, all.Length - lines))) sb.AppendLine(line.TrimEnd('\r'));
        }
        var text = sb.ToString().Replace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        var file = Path.Combine(Path.GetTempPath(), $"TPConsole-log-{DateTime.Now:yyyyMMdd-HHmm}.txt");
        File.WriteAllText(file, text);
        return file;
    }

    // ---- meter rate (Settings → Performance) -------------------------------------------------
    void ApplyMeterRate()
    {
        // Screen refresh. The E2x2's levels arrive at ~15 Hz regardless; faster refresh only makes
        // Windows/app meters and the glide between device values smoother.
        int rate = Math.Clamp(_engine.Profile.Settings.MeterRate, 0, 60);
        _audio.MeterInterval = rate == 0 ? 0 : 1000 / rate;
        if (rate > 0) _meterTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / rate);
        bool want = rate > 0 && _active && _ready;
        _audio.Metering = want;
        if (want) _meterTimer.Start(); else _meterTimer.Stop();
    }

    // ---- what TPConsole itself costs (its process + the web view's processes) ----------------
    readonly DispatcherTimer _perfTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    TimeSpan _perfCpu;
    DateTime _perfAt;
    void SendPerf()
    {
        var ids = new List<int> { Environment.ProcessId };
        try { ids.AddRange(Web.CoreWebView2.Environment.GetProcessInfos().Select(p => p.ProcessId)); } catch (Exception) { }
        TimeSpan cpu = TimeSpan.Zero;
        long mem = 0;
        foreach (var id in ids.Distinct())
        {
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(id);
                cpu += p.TotalProcessorTime;
                mem += p.PrivateMemorySize64;
            }
            catch (Exception) { }
        }
        var now = DateTime.UtcNow;
        if (_perfAt != default && cpu >= _perfCpu)
        {
            double pct = (cpu - _perfCpu).TotalMilliseconds / (now - _perfAt).TotalMilliseconds / Environment.ProcessorCount * 100;
            Post(new JsonObject { ["type"] = "perf", ["cpu"] = Math.Round(pct, 2), ["memMb"] = mem / (1024 * 1024) });
        }
        _perfCpu = cpu;
        _perfAt = now;
    }

    // ---- virtual routing (Thesycon DSP mixer plugin) ---------------------------------------
    JsonObject? _virtualRouting;
    bool _virtualRoutingBusy;
    static JsonObject? SafeVirtualStatus()
    {
        try { return VirtualRouting.Status(); } catch (Exception) { return null; }
    }

    /// <summary>Writes the virtual devices and routes; remembers what was applied on success.</summary>
    string ApplyVirtualDevices(Action<string> log)
    {
        var p = _engine.Profile;
        var devices = p.VirtualDevices.ToList();
        var routes = p.VirtualRoutes.ToList();
        var error = VirtualRouting.ApplyDevices(devices, routes, log);
        // Written to the registry also when the restart was refused: it applies at the next re-plug.
        if (error.Length == 0 || error.StartsWith("RESTART_BLOCKED"))
        {
            var applied = VirtualRouting.Signature(devices, routes);
            _engine.Apply(x => x.VirtualApplied = applied);
        }
        return error;
    }

    void VirtualRoutingOp(string action)
    {
        if (_virtualRoutingBusy) return;
        _virtualRoutingBusy = true;
        SendState();
        // Our own HID / driver handles would veto the E2x2 restart: let go of them meanwhile.
        bool restarts = action is "enable" or "disable" or "apply" or "remove";
        _ = Task.Run(() =>
        {
            var log = new System.Text.StringBuilder();
            string error;
            if (restarts) { _engine.Pause(true); _audio.Pause(true); Thread.Sleep(400); }
            try
            {
                error = action switch
                {
                    "install" => VirtualRouting.Install(l => log.AppendLine(l)),
                    "enable" => VirtualRouting.SetEnabled(true, l => log.AppendLine(l)),
                    "disable" => VirtualRouting.SetEnabled(false, l => log.AppendLine(l)),
                    "remove" => VirtualRouting.Uninstall(l => log.AppendLine(l)),
                    "apply" => ApplyVirtualDevices(l => log.AppendLine(l)),
                    _ => "",
                };
            }
            catch (Exception e) { error = e.Message; }
            finally { if (restarts) { _audio.Pause(false); _engine.Pause(_ccRunning); } }
            try
            {
                Directory.CreateDirectory(VirtualRouting.BackupDir);
                File.AppendAllText(Path.Combine(VirtualRouting.BackupDir, "virtual-routing.log"), $"--- {DateTime.Now} {action}\n{log}{error}\n");
            }
            catch (Exception) { }
            if (action == "remove" && error.Length == 0) _engine.Apply(x => x.VirtualApplied = null);
            _virtualRouting = SafeVirtualStatus();
            Dispatcher.BeginInvoke(() =>
            {
                _virtualRoutingBusy = false;
                SendState();
                Result("virtualRouting", error);
            });
        });
    }

    // ---- idle when nobody looks: no meters, web view suspended (near zero CPU in the tray) ----
    bool _active = true;
    async void SetActive(bool on)
    {
        if (on == _active) return;
        _active = on;
        if (!on)
        {
            _audio.Metering = false;
            _meterTimer.Stop();
            _perfTimer.Stop();
            if (Web.CoreWebView2 is { } core)
            {
                Web.Visibility = Visibility.Hidden; // suspending needs the controller hidden
                try { await core.TrySuspendAsync(); } catch (Exception) { }
            }
            return;
        }
        if (Web.CoreWebView2 is { } c)
        {
            Web.Visibility = Visibility.Visible;
            try { c.Resume(); } catch (Exception) { }
        }
        if (!_ready) return;
        // Catch up on everything skipped while suspended.
        _sentMeters.Clear();
        _sentPeaks.Clear();
        SendState();
        SendWindows();
        ApplyMeterRate();
        _perfTimer.Start();
    }

    /// <summary>Posts to the page unless it is suspended (state is re-sent on resume).</summary>
    void Post(JsonNode msg)
    {
        if (_active && Web.CoreWebView2 is { } core) core.PostWebMessageAsJson(msg.ToJsonString());
    }

    void Quit()
    {
        _quitting = true;
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    void SyncHotkeys()
    {
        var failed = _hotkeys?.Sync();
        if (failed is { Count: > 0 } && _ready)
            Result("hotkeys", string.Join(", ", failed.Select(h => h.Action)));
    }

    async Task InitWebAsync()
    {
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TPConsole", "WebView2");
        await Web.EnsureCoreWebView2Async(await CoreWebView2Environment.CreateAsync(null, data));
        var core = Web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.SetVirtualHostNameToFolderMapping("app.tpconsole", Path.Combine(AppContext.BaseDirectory, "wwwroot"),
            CoreWebView2HostResourceAccessKind.Deny);
        core.WebMessageReceived += (_, e) =>
        {
            // WebView2 drops exceptions thrown here without a trace; keep them in engine.log.
            try { OnMessage(e.WebMessageAsJson); }
            catch (Exception ex) { _engine.Log($"page message failed: {ex.GetType().Name}: {ex.Message} ({e.WebMessageAsJson[..Math.Min(200, e.WebMessageAsJson.Length)]})"); }
        };
        // TPCONSOLE_DEV_URL points at the Vite dev server for live reload while designing.
        Web.Source = new Uri(Environment.GetEnvironmentVariable("TPCONSOLE_DEV_URL") ?? "https://app.tpconsole/index.html");
    }

    void OnMessage(string json)
    {
        var msg = JsonNode.Parse(json)!;
        switch ((string?)msg["op"])
        {
            case "hello":
                _ready = true;
                SendState();
                SendWindows();
                ApplyMeterRate();
                if (_active) _perfTimer.Start();
                break;
            case "preset":
                var name = (string)msg["name"]!;
                switch ((string?)msg["action"])
                {
                    case "load": LoadPreset(name); break;
                    case "save":
                        _engine.SavePreset(name);
                        if ((bool?)msg["includeWindows"] == true) _engine.Apply(p => p.Presets.First(x => x.Name == name).Windows = WindowsSnapshot());
                        break;
                    case "rename": _engine.RenamePreset(name, (string)msg["newName"]!); break;
                    case "delete": _engine.DeletePreset(name); break;
                }
                break;
            case "storeOnDevice":
                bool ok = _engine.StoreOnDevice();
                Post(new JsonObject { ["type"] = "stored", ["ok"] = ok });
                break;
            case "setDefault":
                _audio.SetDefaultDevice((string)msg["endpoint"]!, (bool?)msg["communications"] ?? false);
                break;
            case "asioBuffer":
                var asioError = _audio.SetAsioBuffer((uint)msg["size"]!, (bool)msg["safeMode"]!);
                Result("asioBuffer", asioError);
                break;
            case "closeControlCenter":
                // Killed, not asked to close: it would hide in the tray (and store to the device's flash on exit).
                foreach (var p in System.Diagnostics.Process.GetProcessesByName("ToppingPro"))
                    using (p)
                        try { p.Kill(entireProcessTree: true); }
                        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Result("closeControlCenter", e.Message); }
                break;
            case "resetStats":
                _audio.ResetStats();
                break;
            case "shareLog":
                try
                {
                    var report = WriteLogReport();
                    if ((string)msg["action"]! == "folder")
                        System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{report}\"");
                    else
                        System.Windows.Clipboard.SetFileDropList([report]); // Ctrl+V in Discord etc. attaches the file
                    Result("shareLog", null);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
                {
                    Result("shareLog", e.Message);
                }
                break;
            case "firmware":
                _ = UpdateFirmwareAsync((string?)msg["file"] is "pick" ? PickFirmwareFile() : null);
                break;
            case "endpointVolume":
                _audio.SetEndpointVolume((string)msg["endpoint"]!, (float?)msg["volume"], (bool?)msg["mute"]);
                break;
            case "startWithWindows":
                Startup.Set((bool)msg["on"]!);
                RefreshStartup();
                break;
            case "mini":
                SetMini((bool)msg["on"]!);
                break;
            case "virtualRouting":
                VirtualRoutingOp((string)msg["action"]!);
                break;
            case "checkLatest":
                _ = CheckLatestAsync();
                break;
            case "pluginFile":
                var plugin = new Microsoft.Win32.OpenFileDialog { Filter = "Thesycon mixer plugin|tusbaudiodsp_mixer.sys|*.sys|*.sys", Title = "tusbaudiodsp_mixer.sys" };
                if (plugin.ShowDialog(this) == true)
                {
                    Result("pluginFile", VirtualRouting.SetPluginFile(plugin.FileName));
                    _virtualRouting = SafeVirtualStatus();
                    SendState();
                }
                break;
            case "update":
                switch ((string)msg["action"]!)
                {
                    case "apply": ApplyUpdate(); break;
                    case "check": _ = _updater.CheckAsync(); break;
                }
                break;
            case "appVolume":
                _audio.SetAppVolume((string)msg["key"]!, (float?)msg["volume"], (bool?)msg["mute"]);
                break;
            case "deviceSetting":
                _engine.SetDeviceSetting((byte)(int)msg["sub"]!, (int)msg["value"]!);
                break;
            case "sampleRate":
                var rateError = _audio.SetSampleRate((uint)msg["hz"]!);
                Result("sampleRate", rateError);
                break;
            case "renameEndpoint":
                var (endpoint, newName) = ((string)msg["endpoint"]!, (string)msg["name"]!);
                Task.Run(() =>
                {
                    var err = WindowsAudio.RenameEndpoint(endpoint, newName);
                    Dispatcher.BeginInvoke(() => Result("renameEndpoint", err));
                });
                break;
            case "appDevice":
                _audio.SetAppDevice((uint)msg["pid"]!, (string?)msg["endpoint"]);
                break;
            case "patch":
                _engine.Apply(p => ApplyPatch(p, msg["changes"]!.AsArray()));
                break;
        }
    }

    // Generic path patching keeps the bridge independent of the model's shape.
    static void ApplyPatch(Profile profile, JsonArray changes)
    {
        var root = JsonSerializer.SerializeToNode(profile, Engine.Json)!;
        foreach (var c in changes)
        {
            var parts = ((string)c!["path"]!).Split('.');
            var node = root;
            foreach (var part in parts[..^1])
                node = int.TryParse(part, out var i) ? node[i]! : node[part]!;
            var last = parts[^1];
            var value = c["value"]?.DeepClone();
            if (int.TryParse(last, out var idx)) node[idx] = value;
            else node[last] = value;
        }
        var updated = root.Deserialize<Profile>(Engine.Json)!;
        profile.Mixer = updated.Mixer;
        profile.Names = updated.Names;
        profile.AsioRoutes = updated.AsioRoutes;
        profile.VirtualDevices = updated.VirtualDevices;
        profile.VirtualRoutes = updated.VirtualRoutes;
        profile.Presets = updated.Presets;
        profile.Settings = updated.Settings;
        profile.ActivePreset = updated.ActivePreset;
    }

    void SendState()
    {
        if (!_ready) return;
        // Interface size: native WebView zoom keeps every coordinate consistent.
        // "100%" is drawn 10% larger than the page's own CSS size (what used to be 110%).
        var zoom = BaseZoom * Math.Clamp(_engine.Profile.Settings.UiScale, 0.6, 2);
        if (Math.Abs(Web.ZoomFactor - zoom) > 0.001) Web.ZoomFactor = zoom;
        var msg = new JsonObject
        {
            ["type"] = "state",
            ["profile"] = JsonSerializer.SerializeToNode(_engine.Profile, Engine.Json),
            ["status"] = new JsonObject
            {
                ["connected"] = _engine.Connected,
                ["controlCenterRunning"] = _ccRunning,
                ["monitorMixKnob"] = _engine.MonitorMixKnob,
                ["device"] = JsonSerializer.SerializeToNode(_engine.Device, Engine.Json),
                ["latest"] = _latest?.DeepClone(),
                ["startWithWindows"] = _startup,
                ["controlCenterAutostart"] = _ccAutostart,
                ["storedUpToDate"] = _engine.StoredUpToDate,
                ["virtualRouting"] = _virtualRouting?.DeepClone(),
                ["virtualRoutingBusy"] = _virtualRoutingBusy,
                ["update"] = UpdateStatus(),
                // Virtual devices/routes edited but not written to the driver yet.
                ["virtualPending"] = VirtualRouting.Signature(_engine.Profile.VirtualDevices, _engine.Profile.VirtualRoutes)
                                     != (_engine.Profile.VirtualApplied ?? VirtualRouting.Signature([], [])),
            },
        };
        Post(msg);
    }

    // Startup state is read off the UI thread (schtasks / registry), then pushed with the state.
    bool _startup, _ccAutostart;
    void RefreshStartup() => Task.Run(() =>
    {
        _startup = Startup.IsSet;
        _ccAutostart = Startup.ControlCenterAutostarts();
        Dispatcher.BeginInvoke(SendState);
    });

    // ---- presets with a Windows side --------------------------------------------------------
    /// <summary>Current app outputs (by exe), device volumes and default devices.</summary>
    JsonObject WindowsSnapshot()
    {
        var topo = _audio.Topology;
        var apps = new JsonObject();
        foreach (var s in topo["sessions"]!.AsArray())
            if ((string?)s!["exe"] is { } exe && (string?)s["flow"] != "capture" && !apps.ContainsKey(exe))
                apps[exe] = (string?)s["pinned"];
        var volumes = new JsonObject();
        var defaults = new JsonObject();
        foreach (var e in topo["endpoints"]!.AsArray())
        {
            var id = (string)e!["id"]!;
            volumes[id] = (float?)e["volume"];
            var flow = (string)e["flow"]!;
            if ((bool?)e["isDefault"] == true) defaults[flow] = id;
            if ((bool?)e["isDefaultComm"] == true) defaults[flow + "Comm"] = id;
        }
        return new JsonObject { ["apps"] = apps, ["volumes"] = volumes, ["defaults"] = defaults };
    }

    void LoadPreset(string name)
    {
        _engine.LoadPreset(name);
        if (_engine.Profile.Presets.FirstOrDefault(p => p.Name == name)?.Windows is not { } w) return;
        // Apps: every running session of a stored exe goes where the preset had it.
        if (w["apps"] is JsonObject apps)
            foreach (var s in _audio.Topology["sessions"]!.AsArray())
                if ((string?)s!["exe"] is { } exe && apps.TryGetPropertyValue(exe, out var target) && (string?)s["flow"] != "capture")
                    _audio.SetAppDevice((uint)s["pid"]!, (string?)target);
        if (w["volumes"] is JsonObject vols)
            foreach (var (id, v) in vols)
                if ((float?)v is float f) _audio.SetEndpointVolume(id, f, null);
        if (w["defaults"] is JsonObject defs)
            foreach (var (key, id) in defs)
                if ((string?)id is { } eid) _audio.SetDefaultDevice(eid, key.EndsWith("Comm"));
    }

    // ---- automatic presets: switch while an app runs ----------------------------------------
    // App -> the preset that was active before its rule fired (present = the app is running).
    readonly Dictionary<string, string?> _ruleReturn = new(StringComparer.OrdinalIgnoreCase);

    void CheckPresetRules(HashSet<string> running)
    {
        foreach (var r in _engine.Profile.Settings.PresetRules)
        {
            if (string.IsNullOrWhiteSpace(r.App)) continue;
            if (running.Contains(r.App))
            {
                if (_ruleReturn.TryAdd(r.App, _engine.Profile.ActivePreset)) LoadPreset(r.Preset);
            }
            else if (_ruleReturn.Remove(r.App, out var back) && r.Revert && back is not null && _engine.Profile.ActivePreset == r.Preset)
                LoadPreset(back);
        }
    }

    // ---- mini mode: a small window with the essentials ----------------------------------------
    Rect? _normalBounds;
    const double BaseZoom = 1.1;

    void SetMini(bool on)
    {
        if (on)
        {
            _normalBounds ??= new Rect(Left, Top, Width, Height);
            MinWidth = 330; MinHeight = 330;
            Width = 374; Height = 616;
            Topmost = _engine.Profile.Settings.MiniOnTop;
        }
        else if (_normalBounds is { } b)
        {
            Topmost = false;
            MinWidth = 1080; MinHeight = 700;
            Left = b.Left; Top = b.Top; Width = b.Width; Height = b.Height;
            _normalBounds = null;
        }
    }

    // Latest firmware / Control Center versions from Topping's public update manifest
    // (the same file Control Center checks).
    JsonObject? _latest;
    async Task CheckLatestAsync()
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var root = JsonNode.Parse(await http.GetStringAsync("https://www.topping.pro/images/software_update.json"))!;
            var e = root["E_Series"]!;
            _latest = new JsonObject
            {
                ["firmware"] = (string?)e["HardwareFirmwareVersion_E2x2_OTG"],
                ["firmwareUrl"] = (string?)e["FirmwareUrl_E2x2_OTG"],
            };
            SendState();
        }
        catch (Exception) { /* offline: versions just show without "latest" */ }
    }

    // ---- firmware update (explicit user action only; see FirmwareUpdater) --------------------
    static string? PickFirmwareFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "E2x2 OTG firmware|E2x2_OTG*.bin", Title = "E2x2 OTG firmware" };
        return dlg.ShowDialog() == true ? dlg.FileName : "";
    }

    async Task UpdateFirmwareAsync(string? file)
    {
        void Progress(string state, int pct = 0, string? message = null) => Dispatcher.BeginInvoke(() =>
            Reply("firmware", new JsonObject { ["state"] = state, ["progress"] = pct, ["message"] = message }));
        if (file == "") return; // picker cancelled
        try
        {
            if (file is null)
            {
                var url = (string?)_latest?["firmwareUrl"] ?? throw new InvalidOperationException("No firmware download available");
                Progress("downloading");
                file = await FirmwareUpdater.DownloadAsync(url);
            }
            await Task.Run(() => FirmwareUpdater.Flash(file, (s, p) => Progress(s.ToString(), p)));
            Progress("Finished", 100);
        }
        catch (Exception e)
        {
            Progress("Failed", 0, e.Message);
        }
    }

    /// <summary>Outcome of a slow or fallible operation (null / empty error = ok).</summary>
    void Result(string op, string? error) => Reply("result", new JsonObject { ["op"] = op, ["ok"] = string.IsNullOrEmpty(error), ["message"] = error });

    void Reply(string type, JsonObject body)
    {
        body["type"] = type;
        Post(body);
    }

    void SendWindows()
    {
        if (!_ready) return;
        Post(new JsonObject { ["type"] = "windows", ["topology"] = _audio.Topology.DeepClone() });
    }

    // ASIO auto-detect: a playback pair the driver hears while Windows plays nothing there.
    readonly int[] _asioEvidence = new int[4];

    void DetectAsioRoute(JsonObject peaks)
    {
        var topo = _audio.Topology;
        var hosts = topo["asio"]?["hosts"]?.AsArray();
        if (hosts is null || hosts.Count != 1) { Array.Clear(_asioEvidence); return; }
        var name = (string)hosts[0]!["name"]!;
        if (_engine.Profile.AsioRoutes.ContainsKey(name)) return;
        for (int pb = 0; pb < 4; pb++)
        {
            float drv = (float?)peaks[$"drv:pb{pb}"] ?? 0;
            var ep = topo["endpoints"]!.AsArray().FirstOrDefault(e => (string?)e!["e2x2"] == $"pb{pb}");
            float win = ep is null ? 0 : (float?)peaks[(string)ep["id"]!] ?? 0;
            _asioEvidence[pb] = drv > 0.003f && win < 0.0005f ? _asioEvidence[pb] + 1 : 0;
            if (_asioEvidence[pb] > 10) // ~0.3 s
            {
                _engine.Apply(p => p.AsioRoutes[name] = pb);
                Array.Clear(_asioEvidence);
                return;
            }
        }
    }

    // Only values that changed since the last send go to the page: idle meters cost nothing.
    readonly Dictionary<string, int> _sentMeters = [];
    readonly Dictionary<string, float> _sentPeaks = [];

    void SendMeters()
    {
        var m = new JsonObject();
        foreach (var (p, raw) in _engine.SnapshotMeters())
        {
            var k = p.ToString();
            int v = (int)Math.Round(raw / 5.0) * 5; // 0.5 dB steps: finer changes are not visible
            if (_sentMeters.TryGetValue(k, out var old) && old == v) continue;
            _sentMeters[k] = v;
            m[k] = v;
        }
        var all = _audio.Peaks;
        DetectAsioRoute(all);
        var peaks = new JsonObject();
        foreach (var (k, node) in all)
        {
            float v = (float?)node ?? 0;
            if (_sentPeaks.TryGetValue(k, out var old) && old == v) continue;
            _sentPeaks[k] = v;
            peaks[k] = v;
        }
        if (m.Count == 0 && peaks.Count == 0) return;
        var msg = new JsonObject { ["type"] = "meters", ["m"] = m, ["w"] = peaks };
        Post(msg);
    }

    // ---- theme (Settings → Theme): title bar and the background shown before the page paints ----
    bool? _dark;

    void OnUserPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category == Microsoft.Win32.UserPreferenceCategory.General) Dispatcher.BeginInvoke(ApplyTheme);
    }

    static bool WindowsAppsDark()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is not 1;
    }

    void ApplyTheme()
    {
        var theme = _engine.Profile.Settings.Theme;
        bool dark = theme == "dark" || (theme != "light" && WindowsAppsDark());
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0 || dark == _dark) return;
        _dark = dark;
        // Must match --bg in web/src/app.css.
        var bg = dark ? System.Drawing.Color.FromArgb(0x14, 0x14, 0x13) : System.Drawing.Color.FromArgb(0xF3, 0xF1, 0xEC);
        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(bg.R, bg.G, bg.B));
        Web.DefaultBackgroundColor = bg;
        int on = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
        int caption = bg.R | bg.G << 8 | bg.B << 16; // COLORREF (BGR)
        DwmSetWindowAttribute(hwnd, 35 /* DWMWA_CAPTION_COLOR */, ref caption, sizeof(int));
    }

    [DllImport("dwmapi")]
    static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);
}
