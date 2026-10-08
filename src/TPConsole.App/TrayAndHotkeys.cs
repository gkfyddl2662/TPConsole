using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using TPConsole.Core;
using Forms = System.Windows.Forms;

namespace TPConsole.App;

/// <summary>Notification-area icon: open, switch preset, quit.</summary>
public sealed class Tray : IDisposable
{
    readonly Forms.NotifyIcon _icon;
    readonly Engine _engine;

    public Tray(Engine engine, Action open, Action quit, Action<string> loadPreset)
    {
        _engine = engine;
        _icon = new Forms.NotifyIcon { Icon = MakeIcon(), Text = "TPConsole", Visible = true };
        _icon.DoubleClick += (_, _) => open();
        var menu = new Forms.ContextMenuStrip();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            menu.Items.Add("Open TPConsole", null, (_, _) => open());
            if (_engine.Profile.Presets.Count > 0)
            {
                menu.Items.Add(new Forms.ToolStripSeparator());
                foreach (var p in _engine.Profile.Presets)
                {
                    var item = new Forms.ToolStripMenuItem(p.Name) { Checked = p.Name == _engine.Profile.ActivePreset };
                    var name = p.Name;
                    item.Click += (_, _) => loadPreset(name);
                    menu.Items.Add(item);
                }
            }
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Quit", null, (_, _) => quit());
        };
        menu.Items.Add("…");
        _icon.ContextMenuStrip = menu;
    }

    /// <summary>The app icon (app.ico, embedded in the exe), at the tray's small size.</summary>
    static Icon MakeIcon()
    {
        using var s = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"))!.Stream;
        return new Icon(s, Forms.SystemInformation.SmallIconSize);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}

/// <summary>Global shortcuts (RegisterHotKey) bound to mixer actions.</summary>
public sealed class Hotkeys : IDisposable
{
    const int WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

    readonly nint _hwnd;
    readonly Engine _engine;
    readonly Action<string> _loadPreset;
    readonly HwndSource _source;
    readonly List<Hotkey> _active = [];
    string _signature = "";

    public Hotkeys(nint hwnd, Engine engine, Action<string> loadPreset)
    {
        _hwnd = hwnd;
        _engine = engine;
        _loadPreset = loadPreset;
        _source = HwndSource.FromHwnd(hwnd);
        _source.AddHook(Hook);
    }

    /// <summary>Re-registers when the configured shortcuts changed. Returns the ones Windows refused (in use).</summary>
    public List<Hotkey> Sync()
    {
        var wanted = _engine.Profile.Settings.Hotkeys;
        var sig = System.Text.Json.JsonSerializer.Serialize(wanted);
        if (sig == _signature) return [];
        _signature = sig;
        for (int i = 0; i < _active.Count; i++) UnregisterHotKey(_hwnd, i + 1);
        _active.Clear();
        var failed = new List<Hotkey>();
        foreach (var h in wanted)
        {
            if (h.Key == 0) continue;
            uint mods = MOD_NOREPEAT | (h.Ctrl ? MOD_CONTROL : 0) | (h.Alt ? MOD_ALT : 0) | (h.Shift ? MOD_SHIFT : 0) | (h.Win ? MOD_WIN : 0);
            _active.Add(h);
            if (!RegisterHotKey(_hwnd, _active.Count, mods, (uint)h.Key)) failed.Add(h);
        }
        return failed;
    }

    nint Hook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY) return 0;
        int i = (int)wParam - 1;
        if (i >= 0 && i < _active.Count) Run(_active[i].Action);
        handled = true;
        return 0;
    }

    void Run(string action)
    {
        var parts = action.Split(':', 2);
        var arg = parts.Length > 1 ? parts[1] : "";
        switch (parts[0])
        {
            case "muteInput" when int.TryParse(arg, out int idx):
                _engine.Apply(p =>
                {
                    var m = p.Mixer;
                    bool mute = !m.Inputs[idx].Mute;
                    m.Inputs[idx].Mute = mute;
                    if (idx is 1 or 3) m.Inputs[idx == 1 ? 3 : 1].Mute = mute; // Mobile IN is a stereo pair
                });
                break;
            case "muteOutput":
                _engine.Apply(p =>
                {
                    var o = arg switch
                    {
                        "out12" => p.Mixer.Out12, "mobileOut" => p.Mixer.MobileOut, "spdif" => p.Mixer.Spdif,
                        "loopback12" => p.Mixer.Loopback12, "loopback34" => p.Mixer.Loopback34, _ => p.Mixer.Loopback56,
                    };
                    o.MuteL = o.MuteR = !o.MuteL;
                });
                break;
            case "preset":
                if (arg.Length > 0) _loadPreset(arg);
                break;
            case "nextPreset":
                var list = _engine.Profile.Presets;
                if (list.Count == 0) break;
                int cur = list.FindIndex(x => x.Name == _engine.Profile.ActivePreset);
                _loadPreset(list[(cur + 1) % list.Count].Name);
                break;
        }
    }

    public void Dispose()
    {
        for (int i = 0; i < _active.Count; i++) UnregisterHotKey(_hwnd, i + 1);
        _source.RemoveHook(Hook);
    }

    [DllImport("user32", SetLastError = true)] static extern bool RegisterHotKey(nint hwnd, int id, uint mods, uint vk);
    [DllImport("user32")] static extern bool UnregisterHotKey(nint hwnd, int id);
}
