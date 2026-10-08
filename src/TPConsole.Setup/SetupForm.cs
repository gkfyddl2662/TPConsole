// The setup / uninstall window. Same look as the app's default dark theme (tokens from web/src/app.css).
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

sealed class Theme
{
    public Color Bg, Raised, Line, Text, Text2, Text3, Accent, Warn, Ok, OnText;

    // Dark, like the app's default theme.
    public static readonly Theme Current = new() { Bg = C(0x141413), Raised = C(0x22221f), Line = C(0x3a3934), Text = C(0xece9e2), Text2 = C(0xa8a399), Text3 = C(0x6f6b63), Accent = C(0xe0a24e), Warn = C(0xe5654b), Ok = C(0x6fae8a), OnText = C(0x141413) };

    static Color C(int rgb) => Color.FromArgb(rgb >> 16 & 0xff, rgb >> 8 & 0xff, rgb & 0xff);
    public static Color Mix(Color a, Color b, float t) =>
        Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

/// <summary>Flat rounded button: primary = filled with the text color (like the app's .btn), otherwise outlined.</summary>
sealed class FlatButton : Control, IButtonControl
{
    public bool Primary;
    bool _hover;
    public DialogResult DialogResult { get; set; }

    public FlatButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
    }

    public void NotifyDefault(bool value) { }
    public void PerformClick() { if (Enabled) OnClick(EventArgs.Empty); }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnKeyUp(KeyEventArgs e) { if (e.KeyCode is Keys.Space or Keys.Enter) PerformClick(); base.OnKeyUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var t = Theme.Current;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? t.Bg);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Theme.Round(r, 6 * DeviceDpi / 96f);
        Color fg;
        if (Primary)
        {
            var fill = !Enabled ? Theme.Mix(t.Bg, t.Text, 0.25f) : _hover ? Theme.Mix(t.Text, t.Accent, 0.35f) : t.Text;
            using var b = new SolidBrush(fill);
            g.FillPath(b, path);
            fg = t.OnText;
        }
        else
        {
            if (_hover && Enabled) { using var b = new SolidBrush(Theme.Mix(t.Bg, t.Text, 0.07f)); g.FillPath(b, path); }
            using var pen = new Pen(t.Line);
            g.DrawPath(pen, path);
            fg = Enabled ? t.Text2 : t.Text3;
        }
        if (Focused && ShowFocusCues)
        {
            using var pen = new Pen(t.Accent, 1.5f);
            using var ring = Theme.Round(new RectangleF(1.5f, 1.5f, Width - 3.5f, Height - 3.5f), 5 * DeviceDpi / 96f);
            g.DrawPath(pen, ring);
        }
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
}

/// <summary>Switch with a label, like the app's settings toggles.</summary>
sealed class Toggle : Control
{
    bool _checked;
    public bool Checked { get => _checked; set { _checked = value; Invalidate(); } }

    public Toggle()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
    }

    protected override void OnClick(EventArgs e) { if (Enabled) Checked = !Checked; base.OnClick(e); }
    protected override void OnKeyUp(KeyEventArgs e) { if (e.KeyCode == Keys.Space) OnClick(EventArgs.Empty); base.OnKeyUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var t = Theme.Current;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? t.Bg);
        float s = DeviceDpi / 96f, w = 30 * s, h = 16 * s, y = (Height - h) / 2;
        var track = new RectangleF(0.5f, y, w, h);
        using (var path = Theme.Round(track, h / 2))
        {
            var fill = Checked ? (Enabled ? t.Text : Theme.Mix(t.Bg, t.Text, 0.3f)) : Theme.Mix(t.Bg, t.Text, 0.14f);
            using var b = new SolidBrush(fill);
            g.FillPath(b, path);
            if (Focused && ShowFocusCues) { using var pen = new Pen(t.Accent, 1.5f); g.DrawPath(pen, path); }
        }
        float k = h - 4 * s, kx = Checked ? track.Right - k - 2 * s : track.X + 2 * s;
        using (var b = new SolidBrush(Checked ? t.Bg : t.Text2)) g.FillEllipse(b, kx, y + 2 * s, k, k);
        var text = new Rectangle((int)(w + 10 * s), 0, Width - (int)(w + 10 * s), Height);
        TextRenderer.DrawText(g, Text, Font, text, Enabled ? t.Text : t.Text3, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
}

/// <summary>Thin rounded progress bar.</summary>
sealed class Bar : Control
{
    int _value;
    public Color Fill = Theme.Current.Accent;
    public int Value { get => _value; set { _value = Math.Max(0, Math.Min(100, value)); Invalidate(); } }

    public Bar() => SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

    protected override void OnPaint(PaintEventArgs e)
    {
        var t = Theme.Current;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? t.Bg);
        var r = new RectangleF(0, 0, Width - 1, Height - 1);
        using (var path = Theme.Round(r, r.Height / 2))
        using (var b = new SolidBrush(Theme.Mix(t.Bg, t.Text, 0.12f))) g.FillPath(b, path);
        if (_value == 0) return;
        var f = new RectangleF(0, 0, Math.Max(r.Height, r.Width * _value / 100f), r.Height);
        using (var path = Theme.Round(f, r.Height / 2))
        using (var b = new SolidBrush(Fill)) g.FillPath(b, path);
    }
}

/// <summary>Rounded box (install path, warning).</summary>
sealed class Card : Panel
{
    public Color Border;

    public Card() => SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Current.Bg);
        using var path = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 8 * DeviceDpi / 96f);
        using var b = new SolidBrush(BackColor);
        using var pen = new Pen(Border);
        g.FillPath(b, path);
        g.DrawPath(pen, path);
    }
}

sealed class SetupForm : Form
{
    const int W = 520, Pad = 28; // layout in 96-dpi units; AutoScaleMode.Dpi scales it
    readonly Theme _t = Theme.Current;
    readonly bool _uninstall;
    readonly Stream? _payload;
    readonly Icon? _bigIcon;
    readonly Toggle _option = new();
    readonly Label _status = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
    readonly Bar _bar = new() { Visible = false };
    readonly FlatButton _go = new() { Primary = true };
    readonly FlatButton _cancel = new() { Text = "취소" };
    bool _busy, _done;

    public SetupForm(bool uninstall, Stream? payload)
    {
        _uninstall = uninstall;
        _payload = payload;
        using (var ico = typeof(SetupForm).Assembly.GetManifestResourceStream("app.ico"))
            if (ico != null) { Icon = new Icon(ico); ico.Position = 0; _bigIcon = new Icon(ico, 64, 64); }

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = uninstall ? "TPConsole 제거" : "TPConsole 설치";
        Font = new Font("Malgun Gothic", 9.25f); // Korean UI; renders cleaner than Segoe UI's fallback
        BackColor = _t.Bg;
        ForeColor = _t.Text;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;

        int y = 104; // below the header painted in OnPaint
        if (uninstall)
        {
            y = Add(Text2("프로그램 파일과 바로가기, Windows 시작 시 실행 설정을 지웁니다.\nE2x2 장치 설정은 그대로 남습니다."), y) + 16;
        }
        else
        {
            y = Add(new Label { Text = "설치 위치", ForeColor = _t.Text3, Font = new Font(Font.FontFamily, 8.25f), AutoSize = true }, y) + 6;
            var path = new Card { BackColor = _t.Raised, Border = Theme.Mix(_t.Bg, _t.Line, 0.6f), Height = 38 };
            // Segoe UI: Malgun Gothic draws the backslash as a won sign.
            path.Controls.Add(new Label { Text = Program.InstallDir, Font = new Font("Segoe UI", 9.75f), ForeColor = _t.Text, BackColor = _t.Raised, AutoSize = false, Location = new Point(12, 0), Size = new Size(W - 2 * Pad - 24, 38), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true });
            y = Add(path, y) + 8;
            y = Add(Text2("이전 버전이 있으면 바꿔 씁니다. 앱 설정은 그대로 유지됩니다."), y) + 16;
        }

        string? warning = null;
        if (uninstall && Program.ServiceExists("tusbaudiodsp_mixer"))
            warning = "가상 라우팅이 아직 E2x2에 설치되어 있어 제거할 수 없습니다.\nTPConsole의 설정 → 가상 라우팅에서 먼저 삭제하세요.";
        else if (!uninstall && !Program.ServiceExists("ToppingProUsbAudio"))
            warning = "TOPPING USB 오디오 드라이버가 없습니다.\nTOPPING Professional Control Center(드라이버 5.74 포함)를 먼저 설치하세요.";
        if (warning != null)
        {
            var fill = Theme.Mix(_t.Bg, _t.Warn, 0.12f);
            var card = new Card { BackColor = fill, Border = Theme.Mix(_t.Bg, _t.Warn, 0.45f) };
            var msg = new Label { Text = warning, ForeColor = _t.Text, BackColor = fill, AutoSize = true, MaximumSize = new Size(W - 2 * Pad - 28, 0), Location = new Point(14, 11) };
            card.Controls.Add(msg);
            card.Height = msg.PreferredHeight + 22;
            y = Add(card, y) + 16;
        }

        _option.Text = uninstall ? "앱 설정과 백업도 삭제" : "바탕 화면에 바로가기 만들기";
        _option.Height = 26;
        y = Add(_option, y) + 24;

        // Progress line, then status text (left) and the buttons (right) on one row.
        _bar.Height = 6;
        y = Add(_bar, y) + 16;
        _go.Text = uninstall ? "제거" : "설치";
        _go.Enabled = warning == null || !uninstall;
        foreach (var b in new[] { _go, _cancel }) b.Size = new Size(96, 36);
        _go.Location = new Point(W - Pad - 96, y);
        _cancel.Location = new Point(W - Pad - 96 - 8 - 96, y);
        _status.ForeColor = _t.Text2;
        _status.Location = new Point(Pad, y - 4);
        _status.Size = new Size(W - 2 * Pad - 2 * 96 - 20, 44); // up to two lines
        Controls.Add(_status);
        Controls.Add(_cancel);
        Controls.Add(_go);
        ClientSize = new Size(W, y + 36 + Pad);

        _go.Click += async (_, _) => await Go();
        _cancel.Click += (_, _) => Close();
        AcceptButton = _go;
        CancelButton = _cancel;
        FormClosing += (_, e) => { if (_busy) e.Cancel = true; };
        FormClosed += (_, _) => { if (_uninstall) Program.DeleteSelfLater(); };
        ResumeLayout(false);
        ActiveControl = _go;
    }

    Label Text2(string text) => new() { Text = text, ForeColor = _t.Text2, AutoSize = true, MaximumSize = new Size(W - 2 * Pad, 0) };

    int Add(Control c, int y)
    {
        c.Location = new Point(Pad, y);
        if (!(c is Label { AutoSize: true })) c.Width = W - 2 * Pad;
        Controls.Add(c);
        return y + (c is Label { AutoSize: true } l ? l.PreferredHeight : c.Height);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int dark = 1;
        DwmSetWindowAttribute(Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
        int caption = _t.Bg.R | _t.Bg.G << 8 | _t.Bg.B << 16;
        DwmSetWindowAttribute(Handle, 35 /* DWMWA_CAPTION_COLOR */, ref caption, sizeof(int));
    }

    // Header: big icon, name, one line under it.
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        float s = DeviceDpi / 96f;
        if (_bigIcon != null) g.DrawIcon(_bigIcon, new Rectangle((int)(Pad * s), (int)(26 * s), (int)(52 * s), (int)(52 * s)));
        int x = (int)((Pad + 68) * s);
        using (var title = new Font("Segoe UI Semibold", 17f))
            TextRenderer.DrawText(g, "TPConsole", title, new Point(x, (int)(24 * s)), _t.Text, TextFormatFlags.NoPadding);
        var sub = _uninstall ? "제거" : $"버전 {Program.Version}  ·  TOPPING E2x2 OTG 제어";
        TextRenderer.DrawText(g, sub, Font, new Point(x, (int)(58 * s)), _t.Text2, TextFormatFlags.NoPadding);
        using var pen = new Pen(Theme.Mix(_t.Bg, _t.Line, 0.6f));
        g.DrawLine(pen, Pad * s, 90 * s, (W - Pad) * s, 90 * s);
    }

    void Status(string text, Color color) { _status.Text = text; _status.ForeColor = color; }

    async Task Go()
    {
        if (_done)
        {
            if (!_uninstall) Process.Start(new ProcessStartInfo(Path.Combine(Program.InstallDir, Program.AppExe)) { UseShellExecute = true });
            Close();
            return;
        }
        if (Program.AppRunning())
        {
            Status("TPConsole이 실행 중입니다. 트레이 아이콘 → Quit으로 종료한 뒤 다시 누르세요.", _t.Warn);
            return;
        }

        _busy = true;
        _go.Enabled = _cancel.Enabled = _option.Enabled = false;
        _bar.Visible = true;
        _bar.Fill = _t.Accent;
        var progress = new Progress<(int Percent, string Text)>(p => { _bar.Value = p.Percent; Status(p.Text, _t.Text2); });
        bool opt = _option.Checked;
        try
        {
            if (_uninstall) await Task.Run(() => Program.Uninstall(opt, progress));
            else await Task.Run(() => Program.Install(_payload!, opt, progress));
        }
        catch (Exception ex)
        {
            _busy = false;
            Status("실패: " + ex.Message, _t.Warn);
            _bar.Fill = _t.Warn;
            _bar.Invalidate();
            _go.Enabled = _cancel.Enabled = _option.Enabled = true;
            return;
        }

        _busy = false;
        _done = true;
        _bar.Fill = _t.Ok;
        _bar.Value = 100;
        Status(_uninstall
            ? opt ? "제거했습니다." : "제거했습니다. 앱 설정은 남겨 두었습니다."
            : "설치했습니다. 시작 메뉴에서도 실행할 수 있습니다.", _t.Ok);
        _cancel.Enabled = true;
        _cancel.Text = "닫기";
        _go.Text = _uninstall ? "닫기" : "실행";
        _go.Enabled = true;
        if (_uninstall) _cancel.Visible = false;
        _go.Focus();
    }

    [DllImport("dwmapi")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
