using System.Runtime.InteropServices;
using WMonitor.UI;

namespace WMonitor;

/// <summary>
/// 设置窗口。布局用「块」描述：每块若干控件 + 各自相对偏移 + 块高 + 显示条件，
/// 由 <see cref="LayoutBlocks"/> 顺序排版。托盘指标那几行会随样式显示/隐藏，
/// 所以不能再写死坐标累加。
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly AppConfig _cfg;
    private Theme _th;
    private readonly List<TaskbarInfo> _bars;

    public event Action? Changed;

    private Label _header = null!;
    private Segmented _pos = null!, _strip = null!, _tray = null!, _theme = null!, _rate = null!;
    private Segmented? _screen;
    private Segmented _trayP = null!, _trayS = null!, _trayT = null!, _tray4 = null!;
    private Chip _startup = null!, _onTop = null!, _showStrip = null!;
    private Slider _opacity = null!;
    private Label _opacityText = null!;
    private Stepper _offset = null!;
    private FlowLayoutPanel _flow = null!;
    private readonly List<Chip> _chips = new();

    private readonly List<Block> _blocks = new();
    private sealed record Block(Control[] Ctrls, int[] Dy, int Height, Func<bool> Show);

    private const int Pad = 24;
    private const int ContentW = 352;
    private const int FormW = 400;
    private const int CaptionH = 18;
    private const int CtrlH = 34;

    private static readonly int[] Rates = { 500, 1000, 2000, 5000 };
    private static readonly string[] MetricNames = { "CPU", "内存", "GPU", "显存", "网速", "温度" };

    public SettingsForm(AppConfig cfg, Theme th, List<TaskbarInfo> bars)
    {
        _cfg = cfg;
        _th = th;
        _bars = bars;

        Text = "WMonitor 设置";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = th.PanelBgOpaque;
        ForeColor = th.Text;
        Font = new Font("Segoe UI", 9f);
        ShowInTaskbar = true;
        TopMost = true;
        ShowIcon = false;
        DoubleBuffered = true;

        BuildUi();
        LayoutBlocks();
    }

    // ---------------- 构建 ----------------

    private void BuildUi()
    {
        _header = Header("WMonitor", "系统监视器设置");
        AddBlock(52, null, (_header, 0));

        // ---- 贴片 ----
        _pos = new Segmented
        {
            Items = new[] { "最左", "图标左", "图标右", "托盘左", "自定义" },
            SelectedIndex = (int)_cfg.Anchor,
            Th = _th, Left = Pad, Width = ContentW, Height = CtrlH
        };
        _pos.SelectionChanged += (_, _) => { _cfg.Anchor = (StripAnchor)_pos.SelectedIndex; Apply(); };
        AddCaptionBlock("贴片位置", _pos);

        // ---- 贴片所在屏幕（只有 1 屏时整行隐藏）----
        if (_bars.Count > 1)
        {
            var names = new List<string>();
            int shown = Math.Min(_bars.Count, 3);
            for (int i = 0; i < shown; i++) names.Add($"屏幕 {i + 1}");
            names.Add("全部");

            int sel = _cfg.ShowOnAllMonitors ? names.Count - 1 : 0;
            if (!_cfg.ShowOnAllMonitors)
                for (int i = 0; i < shown; i++)
                    if (_bars[i].Device == _cfg.StripMonitor) sel = i;

            _screen = new Segmented
            {
                Items = names.ToArray(), SelectedIndex = sel,
                Th = _th, Left = Pad, Width = ContentW, Height = CtrlH
            };
            _screen.SelectionChanged += (_, _) =>
            {
                int i = _screen!.SelectedIndex;
                if (i >= shown) _cfg.ShowOnAllMonitors = true;
                else { _cfg.ShowOnAllMonitors = false; _cfg.StripMonitor = _bars[i].Device; }
                Apply();
            };
            AddCaptionBlock("贴片所在屏幕", _screen);
        }

        _strip = new Segmented
        {
            Items = new[] { "数字条", "迷你柱", "四环" },
            SelectedIndex = (int)_cfg.Strip,
            Th = _th, Left = Pad, Width = ContentW, Height = CtrlH
        };
        _strip.SelectionChanged += (_, _) => { _cfg.Strip = (StripStyle)_strip.SelectedIndex; Apply(); };
        AddCaptionBlock("贴片风格", _strip);

        // ---- 托盘图标 ----
        _tray = new Segmented
        {
            Items = new[] { "双数字", "双环", "四段条", "单指标" },
            SelectedIndex = (int)_cfg.Tray,
            Th = _th, Left = Pad, Width = ContentW, Height = CtrlH
        };
        _tray.SelectionChanged += (_, _) =>
        {
            _cfg.Tray = (TrayStyle)_tray.SelectedIndex;
            LayoutBlocks();
            Apply();
        };
        AddCaptionBlock("托盘图标", _tray);

        _trayP = MetricSeg(_cfg.TrayPrimary, m => _cfg.TrayPrimary = m);
        AddInlineBlock("主指标", _trayP);
        _trayS = MetricSeg(_cfg.TraySecondary, m => _cfg.TraySecondary = m);
        AddInlineBlock("次指标", _trayS, () => _cfg.Tray != TrayStyle.Single);
        _trayT = MetricSeg(_cfg.TrayThird, m => _cfg.TrayThird = m);
        AddInlineBlock("第三指标", _trayT, () => _cfg.Tray == TrayStyle.FourBar);
        _tray4 = MetricSeg(_cfg.TrayFourth, m => _cfg.TrayFourth = m);
        AddInlineBlock("第四指标", _tray4, () => _cfg.Tray == TrayStyle.FourBar);

        // ---- 主题 / 刷新 ----
        _theme = new Segmented
        {
            Items = new[] { "深色", "浅色", "跟随系统" },
            SelectedIndex = (int)_cfg.Theme,
            Th = _th, Left = Pad, Width = ContentW, Height = CtrlH
        };
        _theme.SelectionChanged += (_, _) =>
        {
            _cfg.Theme = (ThemeMode)_theme.SelectedIndex;
            Apply();
            Recolor();
        };
        AddCaptionBlock("主题", _theme);

        int rateIdx = Array.IndexOf(Rates, _cfg.RefreshMs);
        _rate = new Segmented
        {
            Items = new[] { "0.5 秒", "1 秒", "2 秒", "5 秒" },
            SelectedIndex = rateIdx < 0 ? 1 : rateIdx,
            Th = _th, Left = Pad, Width = ContentW, Height = CtrlH
        };
        _rate.SelectionChanged += (_, _) =>
        {
            _cfg.RefreshMs = Rates[Math.Clamp(_rate.SelectedIndex, 0, Rates.Length - 1)];
            Apply();
        };
        AddCaptionBlock("刷新间隔", _rate);

        // ---- 贴片显示项 ----
        var capFields = Caption("贴片显示项");
        _flow = new FlowLayoutPanel
        {
            Left = Pad, Top = 0, Width = ContentW, Height = 72,
            BackColor = _th.PanelBgOpaque, WrapContents = true, AutoScroll = false
        };
        AddChip(_flow, "CPU", () => _cfg.ShowCpu, v => _cfg.ShowCpu = v);
        AddChip(_flow, "内存", () => _cfg.ShowRam, v => _cfg.ShowRam = v);
        AddChip(_flow, "GPU", () => _cfg.ShowGpu, v => _cfg.ShowGpu = v);
        AddChip(_flow, "显存", () => _cfg.ShowVram, v => _cfg.ShowVram = v);
        AddChip(_flow, "网速", () => _cfg.ShowNet, v => _cfg.ShowNet = v);
        AddChip(_flow, "GPU 温度", () => _cfg.ShowTemp, v => _cfg.ShowTemp = v);
        AddBlock(22 + 72 + 8, null, (capFields, 0), (_flow, 22));

        // ---- 不透明度 ----
        var capOpacity = Caption("不透明度");
        capOpacity.Width = 70;
        capOpacity.Height = 28;
        capOpacity.TextAlign = ContentAlignment.MiddleLeft;
        _opacity = new Slider { Left = Pad + 72, Top = 0, Width = 200, Value = _cfg.StripOpacity, Th = _th };
        _opacityText = new Label
        {
            Text = _cfg.StripOpacity + "%", Left = Pad + 282, Top = 0, Width = 60, Height = 28,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = _th.Text, BackColor = _th.PanelBgOpaque
        };
        _opacity.ValueChanged += (_, _) =>
        {
            _cfg.StripOpacity = _opacity.Value;
            _opacityText.Text = _opacity.Value + "%";
            Apply();
        };
        AddBlock(44, null, (capOpacity, 0), (_opacity, 0), (_opacityText, 0));

        // ---- 分隔线 ----
        var line = new Panel { Left = Pad, Top = 0, Width = ContentW, Height = 1, BackColor = _th.Line };
        AddBlock(16, null, (line, 0));

        // ---- 行为开关 ----
        _onTop = new Chip("始终置顶") { Checked = _cfg.AlwaysOnTop, Th = _th };
        _startup = new Chip("开机自动启动") { Checked = Startup.IsEnabled(), Th = _th };
        _showStrip = new Chip("显示贴片") { Checked = _cfg.ShowStrip, Th = _th };
        int cx = Pad;
        foreach (var c in new[] { _onTop, _startup, _showStrip }) { c.Left = cx; cx += c.Width + 8; }
        _onTop.CheckedChanged += (_, _) => { _cfg.AlwaysOnTop = _onTop.Checked; Apply(); };
        _startup.CheckedChanged += (_, _) => { Startup.Set(_startup.Checked); _cfg.StartWithWindows = _startup.Checked; Apply(); };
        _showStrip.CheckedChanged += (_, _) => { _cfg.ShowStrip = _showStrip.Checked; Apply(); };
        AddBlock(30 + 16, null, (_onTop, 0), (_startup, 0), (_showStrip, 0));

        // ---- 水平微调 ----
        var offLabel = Caption("贴片水平微调");
        offLabel.Width = 100;
        offLabel.Height = 30;
        offLabel.TextAlign = ContentAlignment.MiddleLeft;
        _offset = new Stepper
        {
            Left = Pad + 104, Top = 0, Width = 100, Height = 30,
            Th = _th, Minimum = -200, Maximum = 200
        };
        _offset.Value = Math.Clamp(_cfg.OffsetX, -200, 200);
        _offset.ValueChanged += (_, _) => { _cfg.OffsetX = _offset.Value; Apply(); };

        var hint = Caption("负数左移 · 正数右移");
        hint.Left = Pad + 236;
        hint.Width = 140;
        hint.Height = 30;
        hint.TextAlign = ContentAlignment.MiddleLeft;
        AddBlock(30 + 16, null, (offLabel, 0), (_offset, 0), (hint, 0));

        // ---- 按钮 ----
        var done = new Button
        {
            Text = "完成",
            Left = Pad + ContentW - 88, Top = 0, Width = 88, Height = 32,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(255, 10, 132, 255),
            ForeColor = Color.White,
            DialogResult = DialogResult.OK
        };
        done.FlatAppearance.BorderSize = 0;
        AcceptButton = done;

        var reset = new Button
        {
            Text = "恢复默认",
            Left = Pad + ContentW - 88 - 96, Top = 0, Width = 88, Height = 32,
            FlatStyle = FlatStyle.Flat,
            BackColor = _th.IsDark ? Color.FromArgb(38, 42, 48) : Color.FromArgb(232, 234, 238),
            ForeColor = _th.Text
        };
        reset.FlatAppearance.BorderSize = 0;
        reset.Click += (_, _) => ResetDefaults();
        AddBlock(32, null, (reset, 0), (done, 0));
    }

    private Segmented MetricSeg(TrayMetric cur, Action<TrayMetric> set)
    {
        var s = new Segmented
        {
            Items = MetricNames,
            SelectedIndex = (int)cur,
            Th = _th, Left = Pad + 70, Width = ContentW - 70, Height = 30
        };
        s.SelectionChanged += (_, _) =>
        {
            set((TrayMetric)Math.Clamp(s.SelectedIndex, 0, MetricNames.Length - 1));
            Apply();
        };
        return s;
    }

    private void AddBlock(int height, Func<bool>? show, params (Control C, int Dy)[] items)
    {
        foreach (var (c, _) in items) Controls.Add(c);
        _blocks.Add(new Block(
            items.Select(i => i.C).ToArray(),
            items.Select(i => i.Dy).ToArray(),
            height,
            show ?? (() => true)));
    }

    /// <summary>分组标题 + 控件（标题在上，控件在下）。</summary>
    private void AddCaptionBlock(string text, Control ctrl, Func<bool>? show = null)
    {
        var cap = Caption(text);
        AddBlock(20 + ctrl.Height + 12, show, (cap, 0), (ctrl, 20));
    }

    /// <summary>标题在左、控件在右的紧凑一行。</summary>
    private void AddInlineBlock(string text, Control ctrl, Func<bool>? show = null)
    {
        var cap = Caption(text);
        cap.Width = 64;
        cap.Height = 30;
        cap.TextAlign = ContentAlignment.MiddleLeft;
        AddBlock(ctrl.Height + 10, show, (cap, 0), (ctrl, 0));
    }

    private void LayoutBlocks()
    {
        int y = 18;
        foreach (var b in _blocks)
        {
            bool vis = b.Show();
            for (int i = 0; i < b.Ctrls.Length; i++)
            {
                b.Ctrls[i].Visible = vis;
                if (vis) b.Ctrls[i].Top = y + b.Dy[i];
            }
            if (vis) y += b.Height;
        }
        ClientSize = new Size(FormW, y + 14);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyTitleBarTheme();
    }

    private void ApplyTitleBarTheme()
    {
        if (!IsHandleCreated) return;
        int dark = _th.IsDark ? 1 : 0;
        if (DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)) != 0)
            DwmSetWindowAttribute(Handle, 19, ref dark, sizeof(int));
    }

    private void AddChip(FlowLayoutPanel flow, string text, Func<bool> get, Action<bool> set)
    {
        var chip = new Chip(text) { Checked = get(), Th = _th, Margin = new Padding(0, 0, 8, 8) };
        chip.CheckedChanged += (_, _) => { set(chip.Checked); Apply(); };
        _chips.Add(chip);
        flow.Controls.Add(chip);
    }

    private Label Header(string title, string sub) => new()
    {
        Text = title,
        Left = Pad, Top = 0, Width = ContentW, Height = 30,
        Font = new Font("Segoe UI", 15f, FontStyle.Bold),
        ForeColor = _th.Text, BackColor = _th.PanelBgOpaque, Tag = "header"
    };

    private Label Caption(string text) => new()
    {
        Text = text,
        Left = Pad, Top = 0, Width = 200, Height = CaptionH,
        Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
        ForeColor = _th.Label, BackColor = _th.PanelBgOpaque, Tag = "caption"
    };

    private void Apply()
    {
        _cfg.Save();
        Changed?.Invoke();
    }

    private void ResetDefaults()
    {
        var d = new AppConfig();
        _cfg.Strip = d.Strip; _cfg.Tray = d.Tray; _cfg.Theme = d.Theme; _cfg.RefreshMs = d.RefreshMs;
        _cfg.ShowCpu = d.ShowCpu; _cfg.ShowRam = d.ShowRam; _cfg.ShowGpu = d.ShowGpu;
        _cfg.ShowVram = d.ShowVram; _cfg.ShowNet = d.ShowNet; _cfg.ShowTemp = d.ShowTemp;
        _cfg.ShowStrip = d.ShowStrip;
        _cfg.TrayPrimary = d.TrayPrimary; _cfg.TraySecondary = d.TraySecondary;
        _cfg.TrayThird = d.TrayThird; _cfg.TrayFourth = d.TrayFourth;
        _cfg.OffsetX = d.OffsetX;

        _cfg.Anchor = d.Anchor;
        _cfg.AlwaysOnTop = d.AlwaysOnTop;
        _cfg.StripOpacity = d.StripOpacity;

        _opacity.Value = d.StripOpacity;
        _opacityText.Text = d.StripOpacity + "%";
        _opacity.Invalidate();
        _pos.SelectedIndex = (int)_cfg.Anchor;
        _onTop.Checked = _cfg.AlwaysOnTop;
        _onTop.Invalidate();
        _showStrip.Checked = _cfg.ShowStrip;
        _showStrip.Invalidate();
        _strip.SelectedIndex = (int)_cfg.Strip;
        _tray.SelectedIndex = (int)_cfg.Tray;
        _trayP.SelectedIndex = (int)_cfg.TrayPrimary;
        _trayS.SelectedIndex = (int)_cfg.TraySecondary;
        _trayT.SelectedIndex = (int)_cfg.TrayThird;
        _tray4.SelectedIndex = (int)_cfg.TrayFourth;
        _theme.SelectedIndex = (int)_cfg.Theme;
        _rate.SelectedIndex = 1;
        _offset.Value = 0;

        for (int i = 0; i < _chips.Count; i++)
            _chips[i].Checked = i switch
            {
                0 => _cfg.ShowCpu, 1 => _cfg.ShowRam, 2 => _cfg.ShowGpu,
                3 => _cfg.ShowVram, 4 => _cfg.ShowNet, _ => _cfg.ShowTemp
            };

        foreach (var c in new Control[] { _pos, _strip, _tray, _trayP, _trayS, _trayT, _tray4, _theme, _rate })
            c.Invalidate();
        foreach (var c in _chips) c.Invalidate();

        LayoutBlocks();
        Apply();
        Recolor();
    }

    private void Recolor()
    {
        _th = Theme.Resolve(_cfg.Theme);
        BackColor = _th.PanelBgOpaque;
        ForeColor = _th.Text;
        foreach (Control c in Controls) RecolorControl(c);
        ApplyTitleBarTheme();
    }

    private void RecolorControl(Control c)
    {
        if (c is Segmented s) { s.Th = _th; s.Invalidate(); }
        else if (c is Chip ch) { ch.Th = _th; ch.Invalidate(); }
        else if (c is Slider sl) { sl.Th = _th; sl.Invalidate(); }
        else if (c is Stepper st) { st.Th = _th; st.Invalidate(); }
        else if (c is FlowLayoutPanel f) { f.BackColor = _th.PanelBgOpaque; foreach (Control x in f.Controls) RecolorControl(x); }
        else if (c is Label l)
        {
            l.BackColor = _th.PanelBgOpaque;
            l.ForeColor = (l.Tag as string) == "caption" ? _th.Label : _th.Text;
        }
        else if (c is Button btn)
        {
            btn.ForeColor = btn.DialogResult == DialogResult.OK ? Color.White : _th.Text;
            if (btn.DialogResult != DialogResult.OK)
                btn.BackColor = _th.IsDark ? Color.FromArgb(38, 42, 48) : Color.FromArgb(232, 234, 238);
        }
        else if (c is Panel p && p.Height <= 2) { p.BackColor = _th.Line; }
    }
}
