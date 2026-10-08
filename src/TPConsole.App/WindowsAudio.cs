using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using TPConsole.Core;

namespace TPConsole.App;

/// <summary>
/// Read-only view of Windows audio: active endpoints and the app sessions playing to each one.
/// COM objects live on one dedicated MTA thread; the UI gets JSON snapshots.
/// </summary>
public sealed class WindowsAudio : IDisposable
{
    readonly Thread _thread;
    readonly CancellationTokenSource _cts = new();
    readonly ConcurrentDictionary<string, string?> _icons = new();
    DriverClient? _driver;
    readonly List<uint> _asioHosts = [];
    int? _lastAsioInstance;
    volatile JsonObject? _asio;
    volatile JsonObject? _driverInfo;
    readonly BlockingCollection<Action> _work = new();
    DriverClient? _meterDriver;
    byte[][] _playbackIds = [];
    byte[][] _recordIds = [];
    volatile JsonObject _topology = new();
    volatile JsonObject _peaks = new();

    /// <summary>Raised on the worker thread when endpoints or sessions changed.</summary>
    public event Action? TopologyChanged;

    public JsonObject Topology => _topology;
    public JsonObject Peaks => _peaks;

    public WindowsAudio()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "WindowsAudio" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        _ = Task.Run(NotificationLoop);
        _ = Task.Run(StatsLoop);
        // ASIO state is one cheap IOCTL; driver notifications (streaming start/stop, ASIO buffer)
        // also trigger a check right away. The slow part (finding the host process) only runs
        // when a new ASIO session appears.
        _ = Task.Run(async () =>
        {
            int tick = 0;
            while (!_cts.IsCancellationRequested)
            {
                var asio = Asio();
                if (asio?.ToJsonString() != _asio?.ToJsonString())
                {
                    _asio = asio;
                    Poke();
                }
                if (tick++ % 10 == 0) _driverInfo = DriverInfo();
                // Slower while nobody looks; driver notifications still wake the topology right away.
                try { await Task.Delay(_metering ? 500 : 2000, _cts.Token); } catch (OperationCanceledException) { }
            }
        });
    }

    /// <summary>Read peaks only while someone looks at them (window shown).</summary>
    public bool Metering
    {
        get => _metering;
        set { _metering = value; _wake.Set(); }
    }
    volatile bool _metering;

    /// <summary>The profile's virtual devices, to recognise their Windows endpoints ("v:{id}").</summary>
    public Func<IReadOnlyList<TPConsole.Core.VirtualDevice>>? VirtualDevices { get; set; }

    /// <summary>A virtual device's endpoint carries the name it was applied with (DisplayName).</summary>
    string? VirtualRole(string desc, string flow)
    {
        var list = VirtualDevices?.Invoke();
        var d = list?.FirstOrDefault(x => (x.Kind == "playback") == (flow == "render") && VirtualRouting.Label(x) == desc);
        return d is null ? null : $"v:{d.Id}";
    }

    /// <summary>Milliseconds between peak reads while metering (Settings → Performance).</summary>
    public int MeterInterval { get; set; } = 33;

    readonly AutoResetEvent _wake = new(false);
    volatile bool _dirty = true, _devicesDirty = true;
    void Poke() { _dirty = true; _wake.Set(); }

    volatile bool _paused;
    /// <summary>Closes every handle on the E2x2's driver so Windows can restart it (see Engine.Pause).</summary>
    public void Pause(bool on)
    {
        _paused = on;
        if (!on) { Poke(); return; }
        try { _driver?.Dispose(); } catch (Exception) { }
        _driver = null;
        try { _meterDriver?.Dispose(); } catch (Exception) { }
        _meterDriver = null;
    }
    void Enqueue(Action job) { _work.Add(job); _wake.Set(); }

    // Endpoints and sessions are kept (not re-created every scan) so their change events stay registered.
    sealed record Endpoint(MMDevice Device, string Id, string Flow);
    sealed record ProcInfo(string Name, string? Path, bool Service);

    readonly Dictionary<string, Endpoint> _endpoints = [];
    readonly Dictionary<string, AudioSessionControl> _sessionMap = [];
    readonly Dictionary<uint, ProcInfo> _procs = [];

    /// <summary>Session started / stopped / closed / volume or name changed.</summary>
    sealed class SessionEvents(WindowsAudio owner) : IAudioSessionEventsHandler
    {
        public void OnVolumeChanged(float volume, bool isMuted) => owner.Poke();
        public void OnDisplayNameChanged(string displayName) => owner.Poke();
        public void OnIconPathChanged(string iconPath) { }
        public void OnChannelVolumeChanged(uint channelCount, IntPtr newVolumes, uint channelIndex) { }
        public void OnGroupingParamChanged(ref Guid groupingId) { }
        public void OnStateChanged(AudioSessionState state) => owner.Poke();
        public void OnSessionDisconnected(AudioSessionDisconnectReason reason) => owner.Poke();
    }

    static bool SameKey(PropertyKey a, PropertyKey b) => a.formatId == b.formatId && a.propertyId == b.propertyId;

    void Run()
    {
        using var enumerator = new MMDeviceEnumerator();
        // Endpoint added / removed / default changed / renamed (raised on the audio worker thread: just flag it).
        using var deviceEvents = enumerator.CreateNotificationClient(useSynchronizationContext: false);
        deviceEvents.DeviceAdded += (_, _) => { _devicesDirty = true; Poke(); };
        deviceEvents.DeviceRemoved += (_, _) => { _devicesDirty = true; Poke(); };
        deviceEvents.DeviceStateChanged += (_, _) => { _devicesDirty = true; Poke(); };
        deviceEvents.DefaultDeviceChanged += (_, _) => Poke();
        // Fires often for unrelated properties; only names matter here.
        deviceEvents.PropertyValueChanged += (_, e) =>
        {
            if (SameKey(e.PropertyKey, PropertyKeys.PKEY_Device_DeviceDesc) || SameKey(e.PropertyKey, PropertyKeys.PKEY_Device_FriendlyName)) Poke();
        };
        string lastJson = "";
        var sinceScan = Stopwatch.StartNew();

        while (!_cts.IsCancellationRequested)
        {
            while (_work.TryTake(out var job))
            {
                try { job(); } catch (Exception e) { Debug.WriteLine(e); }
                _dirty = true;
            }
            // Events drive rescans. The slow safety rescan catches what has no event
            // (per-app output preferences changed in Windows Settings).
            if (_dirty || sinceScan.ElapsedMilliseconds > 10_000)
            {
                _dirty = false;
                sinceScan.Restart();
                try
                {
                    if (_devicesDirty)
                    {
                        _devicesDirty = false;
                        RebuildEndpoints(enumerator);
                    }
                    var topo = Scan(enumerator);
                    var json = topo.ToJsonString();
                    if (json != lastJson)
                    {
                        lastJson = json;
                        _topology = topo;
                        TopologyChanged?.Invoke();
                    }
                }
                catch (COMException) { _devicesDirty = true; }
            }

            if (_metering)
            {
                var peaks = new JsonObject();
                foreach (var e in _endpoints.Values) peaks[e.Id] = Peak(() => e.Device.AudioMeterInformation.MasterPeakValue);
                foreach (var (key, c) in _sessionMap) peaks[key] = Peak(() => c.AudioMeterInformation.MasterPeakValue);
                DriverMeters(peaks);
                _peaks = peaks;
            }

            // Bursts of events (a volume drag) are folded into one rescan per 100 ms.
            int wait = _metering ? Math.Max(15, MeterInterval) : 10_000;
            WaitHandle.WaitAny([_wake, _cts.Token.WaitHandle], wait);
            if (_dirty && sinceScan.ElapsedMilliseconds < 100) _cts.Token.WaitHandle.WaitOne(100 - (int)sinceScan.ElapsedMilliseconds);
        }
        foreach (var c in _sessionMap.Values) c.Dispose();
        foreach (var e in _endpoints.Values) e.Device.Dispose();
    }

    /// <summary>Peaks are quantised so silence and steady levels produce no changes to send.</summary>
    static float Peak(Func<float> read)
    {
        try { var v = read(); return v < 0.0005f ? 0 : MathF.Round(v, 3); } catch (COMException) { return 0; }
    }

    void RebuildEndpoints(MMDeviceEnumerator enumerator)
    {
        foreach (var c in _sessionMap.Values) c.Dispose();
        _sessionMap.Clear();
        foreach (var e in _endpoints.Values) e.Device.Dispose();
        _endpoints.Clear();
        foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.All, NAudio.CoreAudioApi.DeviceState.Active))
        {
            var ep = new Endpoint(dev, dev.ID, dev.DataFlow == DataFlow.Render ? "render" : "capture");
            _endpoints[ep.Id] = ep;
            try { dev.AudioEndpointVolume.OnVolumeNotification += _ => Poke(); } catch (COMException) { }
            try { dev.AudioSessionManager.OnSessionCreated += (_, _) => Poke(); } catch (COMException) { }
        }
    }

    ProcInfo Proc(uint pid, string displayName)
    {
        if (_procs.TryGetValue(pid, out var known)) return known;
        var (name, path) = Describe(pid, displayName);
        bool service = path?.EndsWith("svchost.exe", StringComparison.OrdinalIgnoreCase) ?? false;
        return _procs[pid] = new ProcInfo(name, path, service);
    }

    JsonObject Scan(MMDeviceEnumerator enumerator)
    {
        string? Default(DataFlow f, Role r)
        {
            try { return enumerator.HasDefaultAudioEndpoint(f, r) ? enumerator.GetDefaultAudioEndpoint(f, r).ID : null; }
            catch (COMException) { return null; }
        }
        var defaults = new[]
        {
            Default(DataFlow.Render, Role.Multimedia), Default(DataFlow.Render, Role.Communications),
            Default(DataFlow.Capture, Role.Multimedia), Default(DataFlow.Capture, Role.Communications),
        };

        var epJson = new JsonArray();
        var sessJson = new JsonArray();
        var seen = new HashSet<string>();
        var seenPids = new HashSet<uint>();

        foreach (var ep in _endpoints.Values)
        {
            var dev = ep.Device;
            var flow = ep.Flow;
            string desc = Prop(dev, PropertyKeys.PKEY_Device_DeviceDesc) ?? dev.FriendlyName;
            string iface = dev.DeviceFriendlyName;
            float epVol = 1; bool epMute = false;
            try { epVol = dev.AudioEndpointVolume.MasterVolumeLevelScalar; epMute = dev.AudioEndpointVolume.Mute; } catch (COMException) { }
            epJson.Add(new JsonObject
            {
                ["volume"] = MathF.Round(epVol, 3),
                ["muted"] = epMute,
                ["id"] = ep.Id,
                ["flow"] = flow,
                ["name"] = desc,
                ["device"] = iface,
                ["isDefault"] = ep.Id == defaults[flow == "render" ? 0 : 2],
                ["isDefaultComm"] = ep.Id == defaults[flow == "render" ? 1 : 3],
                ["e2x2"] = iface.Contains("E2x2", StringComparison.OrdinalIgnoreCase) ? LearnRole(ep.Id, desc) ?? VirtualRole(desc, flow) : null,
            });

            SessionCollection list;
            try { dev.AudioSessionManager.RefreshSessions(); list = dev.AudioSessionManager.Sessions; } catch (COMException) { continue; }
            for (int i = 0; i < list.Count; i++)
            {
                var fresh = list[i];
                AudioSessionState state;
                try { state = fresh.State; } catch (COMException) { continue; }
                if (state == AudioSessionState.AudioSessionStateExpired) continue;
                var key = $"{ep.Id}|{fresh.GetSessionInstanceIdentifier}";
                if (!_sessionMap.TryGetValue(key, out var s))
                {
                    try { fresh.RegisterEventClient(new SessionEvents(this)); } catch (COMException) { }
                    s = _sessionMap[key] = fresh;
                }
                seen.Add(key);
                uint pid = s.GetProcessID;
                bool system = s.IsSystemSoundsSession || pid == 0;
                float vol = 1; bool muted = false;
                try { vol = s.SimpleAudioVolume.Volume; muted = s.SimpleAudioVolume.Mute; } catch (COMException) { }
                var info = system ? new ProcInfo("System sounds", null, false) : Proc(pid, s.DisplayName);
                if (!system) seenPids.Add(pid);
                string? pinned = null;
                if (!system && flow == "render")
                {
                    try { pinned = AudioPolicy.Get(pid); } catch (Exception) { }
                }
                sessJson.Add(new JsonObject
                {
                    ["key"] = key,
                    ["endpoint"] = ep.Id,
                    ["flow"] = flow,
                    ["pid"] = pid,
                    ["name"] = info.Name,
                    ["exe"] = info.Path is null ? null : System.IO.Path.GetFileNameWithoutExtension(info.Path),
                    ["active"] = state == AudioSessionState.AudioSessionStateActive,
                    ["icon"] = info.Path is null ? null : Icon(info.Path),
                    ["system"] = system,
                    // Windows components (svchost) recording/playing: shown, but marked.
                    ["service"] = info.Service,
                    ["volume"] = MathF.Round(vol, 3),
                    ["muted"] = muted,
                    // Endpoint the app is pinned to in Windows' per-app preferences; null = Windows default.
                    ["pinned"] = pinned,
                });
            }
        }
        foreach (var gone in _sessionMap.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _sessionMap[gone].Dispose();
            _sessionMap.Remove(gone);
        }
        foreach (var pid in _procs.Keys.Where(p => !seenPids.Contains(p)).ToList()) _procs.Remove(pid);
        return new JsonObject
        {
            ["endpoints"] = epJson, ["sessions"] = sessJson, ["asio"] = _asio?.DeepClone(), ["driver"] = _driverInfo?.DeepClone(),
        };
    }

    const string AsioModule = "toppingprousbaudioasio_x64.dll";

    /// <summary>
    /// ASIO streams bypass Windows audio, so ASIO hosts never appear as sessions. The driver says
    /// whether an ASIO client is attached; the host itself is the process that loaded the ASIO DLL.
    /// </summary>
    JsonObject? Asio()
    {
        int? instance = null;
        try
        {
            if (_paused) return _asio;
            _driver ??= DriverClient.Open();
            instance = _driver.ActiveAsioInstance();
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _driver?.Dispose();
            _driver = null;
        }
        if (instance is null)
        {
            _asioHosts.Clear();
            _lastAsioInstance = null;
            return null;
        }
        // Host gone (DAW closed but another keeps ASIO open) -> look again.
        if (_asioHosts.RemoveAll(pid => !IsAlive(pid)) > 0) _lastAsioInstance = null;
        // Scanning every process's modules takes ~1 s of CPU, so only do it when ASIO (re)starts.
        if (_asioHosts.Count == 0 && instance != _lastAsioInstance)
        {
            _lastAsioInstance = instance;
            foreach (var p in Process.GetProcesses())
                if (p.Id != Environment.ProcessId && HasModule((uint)p.Id, AsioModule)) _asioHosts.Add((uint)p.Id);
        }
        var hosts = new JsonArray();
        foreach (var pid in _asioHosts)
        {
            var (name, path) = Describe(pid, "");
            hosts.Add(new JsonObject
            {
                ["pid"] = pid,
                ["name"] = path is null ? name : System.IO.Path.GetFileNameWithoutExtension(path),
                ["icon"] = path is null ? null : Icon(path),
            });
        }
        return new JsonObject { ["hosts"] = hosts };
    }

    JsonObject? DriverInfo()
    {
        try
        {
            if (_driver is null || _paused) return _driverInfo;
            var (major, minor) = _driver.Version();
            var info = new JsonObject
            {
                ["version"] = $"{major}.{minor}",
                ["sampleRate"] = _driver.CurrentSampleRate(),
                ["sampleRates"] = new JsonArray(_driver.SupportedSampleRates().Select(r => (JsonNode)r).ToArray()),
            };
            if (_driver.ActiveAsioInstance() is int inst)
            {
                var a = _driver.AsioDetails(inst);
                info["asio"] = new JsonObject
                {
                    ["bufferSize"] = a.BufferSize, ["safeMode"] = a.SafeMode,
                    ["bufferSizes"] = new JsonArray(a.BufferSizes.Select(x => (JsonNode)x).ToArray()),
                };
            }
            if (_stats is not null) info["stats"] = _stats.DeepClone();
            info["events"] = new JsonArray(_events.ToArray().Select(e => (JsonNode)e).ToArray());
            return info;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    // ---- driver notifications: react immediately to rate / ASIO / streaming changes ----------
    readonly ConcurrentQueue<string> _events = new();

    void NotificationLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            if (_paused) { _cts.Token.WaitHandle.WaitOne(300); continue; }
            try
            {
                using var d = DriverClient.Open();
                using var ev = new AutoResetEvent(false);
                d.RegisterNotifications(0xFFFFFFFF, ev.SafeWaitHandle);
                while (!_cts.IsCancellationRequested && !_paused)
                {
                    if (!ev.WaitOne(1000)) continue;
                    while (d.ReadNotification() is { } n)
                    {
                        // Keep a short log for the diagnostics page (USB interrupt messages may reveal front-panel controls).
                        _events.Enqueue($"{DateTime.Now:HH:mm:ss} {n.Category:X}/{n.Id} {Convert.ToHexString(n.Data)}");
                        while (_events.Count > 12) _events.TryDequeue(out _);
                        _driverInfo = DriverInfo();
                        Poke();
                    }
                }
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                _cts.Token.WaitHandle.WaitOne(3000); // device unplugged; retry
            }
        }
    }

    // ---- stream health: totals plus the increase over the last minute ----------------------
    volatile JsonObject? _stats;
    readonly Queue<(DateTime At, long Dropouts, long Errors)> _statHistory = new();

    void StatsLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            if (_paused) { _cts.Token.WaitHandle.WaitOne(500); continue; }
            try
            {
                using var d = DriverClient.Open();
                var s = d.Statistics();
                var now = DateTime.UtcNow;
                _statHistory.Enqueue((now, s.Dropouts, s.UsbErrors));
                while (_statHistory.Count > 0 && now - _statHistory.Peek().At > TimeSpan.FromSeconds(65)) _statHistory.Dequeue();
                var first = _statHistory.Peek();
                _stats = new JsonObject
                {
                    ["dropouts"] = s.Dropouts, ["usbErrors"] = s.UsbErrors,
                    ["recentDropouts"] = s.Dropouts - first.Dropouts, ["recentUsbErrors"] = s.UsbErrors - first.Errors,
                };
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { _stats = null; }
            _cts.Token.WaitHandle.WaitOne(_metering ? 10_000 : 30_000);
        }
    }

    public void ResetStats()
    {
        try { using var d = DriverClient.Open(); d.Statistics(reset: true); _statHistory.Clear(); } catch (Exception) { }
    }

    public string? SetAsioBuffer(uint size, bool safeMode)
    {
        try
        {
            using var d = DriverClient.Open();
            if (d.ActiveAsioInstance() is not int inst) return "ASIO is not in use";
            var a = d.AsioDetails(inst);
            d.SetAsioBuffer(inst, a.Rate, size, safeMode);
            _driverInfo = DriverInfo();
            return null;
        }
        catch (Exception e) { return e.Message; }
    }

    /// <summary>Sets the device sample rate through the driver (streaming restarts).</summary>
    public string? SetSampleRate(uint hz)
    {
        try
        {
            using var d = DriverClient.Open();
            d.SetSampleRate(hz);
            return null;
        }
        catch (Exception e) { return e.Message; }
    }

    /// <summary>Windows device volume (0..1) / mute — the slider in the Windows sound settings.</summary>
    public void SetEndpointVolume(string id, float? volume, bool? mute) => Enqueue(() =>
    {
        if (!_endpoints.TryGetValue(id, out var ep)) return;
        var dev = ep.Device;
        if (volume is float v) dev.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(v, 0, 1);
        if (mute is bool m) dev.AudioEndpointVolume.Mute = m;
    });

    /// <summary>Renames a Windows audio endpoint (endpoint property store; needs admin). Null = done, else the error.</summary>
    public static string? RenameEndpoint(string id, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        try
        {
            using var en = new MMDeviceEnumerator();
            var dev = en.GetDevice(id);
            dev.GetPropertyInformation(StorageAccessMode.ReadWrite);
            dev.Properties.SetValue(PropertyKeys.PKEY_Device_DeviceDesc,
                new PropVariant { vt = (short)VarEnum.VT_LPWSTR, pointerValue = Marshal.StringToCoTaskMemUni(name) });
            dev.Properties.Commit();
            return null;
        }
        catch (Exception e) { return e.Message; }
    }

    /// <summary>Per-app volume (0..1) and mute through the app's Windows audio session.</summary>
    public void SetAppVolume(string key, float? volume, bool? mute) => Enqueue(() =>
    {
        if (!_sessionMap.TryGetValue(key, out var s)) return;
        if (volume is float v) s.SimpleAudioVolume.Volume = Math.Clamp(v, 0, 1);
        if (mute is bool m) s.SimpleAudioVolume.Mute = m;
    });

    static bool IsAlive(uint pid)
    {
        try { using var p = Process.GetProcessById((int)pid); return !p.HasExited; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    static bool HasModule(uint pid, string module)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            foreach (ProcessModule m in p.Modules)
                if (string.Equals(m.ModuleName, module, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        return false;
    }

    // Endpoint id -> E2x2 role, learned from the driver's default names and remembered, so an
    // endpoint renamed in Windows keeps its place in the graph.
    static readonly string RolesPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TPConsole", "endpoints.json");
    Dictionary<string, string>? _roles;

    string? LearnRole(string id, string desc)
    {
        _roles ??= File.Exists(RolesPath)
            ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(RolesPath)) ?? []
            : [];
        if (E2x2Role(desc) is { } byName)
        {
            if (_roles.GetValueOrDefault(id) != byName)
            {
                _roles[id] = byName;
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(RolesPath)!);
                File.WriteAllText(RolesPath, System.Text.Json.JsonSerializer.Serialize(_roles));
            }
            return byName;
        }
        return _roles.GetValueOrDefault(id);
    }

    /// <summary>Which part of the E2x2 an endpoint is, from the driver's default endpoint names.</summary>
    static string? E2x2Role(string desc) => desc switch
    {
        "Playback 1/2" => "pb0",
        "Playback 3/4" => "pb1",
        "Playback 5/6" => "pb2",
        "Playback 7/8" => "pb3",
        "Loopback 1/2" => "loopback12",
        "Loopback 3/4" => "loopback34",
        "Loopback 5/6" => "loopback56",
        "Analog 1/2" => "analog",
        "Mobile IN" => "mobileIn",
        _ => null,
    };

    static string? Prop(MMDevice dev, PropertyKey key)
    {
        try
        {
            var store = dev.Properties;
            return store.Contains(key) ? store[key].Value as string : null;
        }
        catch (COMException) { return null; }
    }

    static (string Name, string? Path) Describe(uint pid, string displayName)
    {
        var path = ProcessPath(pid);
        string? desc = null;
        if (path is not null)
        {
            try { desc = FileVersionInfo.GetVersionInfo(path).FileDescription; } catch (IOException) { }
        }
        // svchost sessions belong to Windows services; name them by the service(s) inside.
        if (path is not null && System.IO.Path.GetFileName(path).Equals("svchost.exe", StringComparison.OrdinalIgnoreCase)
            && ServicesOf(pid) is { Length: > 0 } services)
            return ($"Windows · {services}", path);
        // Session display names are often resource refs like "@%SystemRoot%\..."; skip those.
        var name = !string.IsNullOrWhiteSpace(displayName) && !displayName.StartsWith('@') ? displayName
            : !string.IsNullOrWhiteSpace(desc) ? desc
            : path is not null ? System.IO.Path.GetFileNameWithoutExtension(path)
            : $"PID {pid}";
        return (name, path);
    }

    static readonly ConcurrentDictionary<uint, string> _services = new();

    /// <summary>Display names of the Windows services hosted in a svchost process (cached per PID).</summary>
    static string ServicesOf(uint pid) => _services.GetOrAdd(pid, p =>
    {
        try
        {
            var line = Shell.Run("tasklist", $"/svc /fo csv /nh /fi \"PID eq {p}\"").Output.Trim();
            // "svchost.exe","4768","Audiosrv"  (service short names, comma separated)
            var names = line.Split("\",\"").LastOrDefault()?.Trim('"').Split(',', StringSplitOptions.TrimEntries) ?? [];
            return string.Join(", ", names.Where(n => n.Length > 0 && n != "N/A").Select(n =>
            {
                try { using var sc = new System.ServiceProcess.ServiceController(n); return sc.DisplayName; }
                catch (Exception) { return n; }
            }));
        }
        catch (Exception) { return ""; }
    });

    static string? ProcessPath(uint pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == 0) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Exe icon as a PNG data URL, cached per path.</summary>
    string? Icon(string path) => _icons.GetOrAdd(path, p =>
    {
        var info = new SHFILEINFO();
        if (SHGetFileInfo(p, 0, ref info, Marshal.SizeOf<SHFILEINFO>(), 0x100 /* SHGFI_ICON */) == 0 || info.hIcon == 0) return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(src));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
        }
        finally { DestroyIcon(info.hIcon); }
    });

    /// <summary>
    /// Driver-side playback meters ("drv:pb0".."drv:pb3", max of L/R). Unlike Windows endpoint meters
    /// these also see ASIO streams, which bypass Windows.
    /// </summary>
    void DriverMeters(JsonObject peaks)
    {
        try
        {
            if (_paused) return;
            if (_meterDriver is null)
            {
                _meterDriver = DriverClient.Open();
                _playbackIds = _meterDriver.ChannelIds(1);
                _recordIds = _meterDriver.ChannelIds(0);
                _meterDriver.EnablePeakMeters([.. _playbackIds, .. _recordIds]);
            }
            var p = _meterDriver.ReadPeakMeters(_playbackIds);
            for (int i = 0; i + 1 < p.Length; i += 2) peaks[$"drv:pb{i / 2}"] = MathF.Round(MathF.Max(p[i], p[i + 1]), 4);
            // Recording pairs in driver order: Analog 1/2, Mobile IN, Loopback 1/2, 3/4, 5/6 (order from the "rec" profile, unverified).
            var r = _meterDriver.ReadPeakMeters(_recordIds);
            for (int i = 0; i + 1 < r.Length; i += 2) peaks[$"drv:rec{i / 2}"] = MathF.Round(MathF.Max(r[i], r[i + 1]), 4);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _meterDriver?.Dispose();
            _meterDriver = null; // device unplugged or driver busy; retry next tick
        }
    }

    /// <summary>Pin an app's output to an endpoint (null = Windows default). Runs on the audio thread.</summary>
    public void SetAppDevice(uint pid, string? endpointId) => Enqueue(() => AudioPolicy.Set(pid, endpointId));

    /// <summary>Make an endpoint the Windows default (or default communications) device.</summary>
    public void SetDefaultDevice(string endpointId, bool communications) =>
        Enqueue(() => PolicyConfig.SetDefault(endpointId, communications));

    public void Dispose()
    {
        _cts.Cancel();
        _thread.Join(500);
        _driver?.Dispose();
        try { _meterDriver?.DisablePeakMeters([.. _playbackIds, .. _recordIds]); } catch (Exception) { }
        _meterDriver?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO
    {
        public nint hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32", CharSet = CharSet.Unicode)]
    static extern nint SHGetFileInfo(string path, uint attrs, ref SHFILEINFO info, int size, uint flags);
    [DllImport("user32")] static extern bool DestroyIcon(nint h);
    [DllImport("kernel32")] static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32")] static extern bool CloseHandle(nint h);
    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(nint h, int flags, StringBuilder name, ref int size);
}
