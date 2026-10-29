using System.Runtime.InteropServices;
using WMonitor.UI;

namespace WMonitor;

public sealed class SettingsForm : Form
{
    private readonly AppConfig _cfg;
    private Theme _th;

    public event Action? Changed;

    private readonly Segmented _pos;
    private readonly Segmented _strip;
    private readonly Segmented _tray;
    private readonly Segmented _theme;
    private readonly Segmented _rate;
    private readonly List<Chip> _chips = new();
    private readonly Chip _startup;
    private readonly Chip _onTop;
    private readonly Slider _opacity;
    private readonly Label _opacityText;
    private readonly NumericUpDown _offset;

    private static readonly int[] Rates = { 500, 1000, 2000, 5000 };

    public SettingsForm(AppConfig cfg, Theme th)
    {
        _cfg = cfg;
        _th = th;

        Text = "WMonitor 设置";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(400, 698);
        BackColor = th.PanelBgOpaque;
        ForeColor = th.Text;
        Font = new Font("Segoe UI", 9f);
        ShowInTaskbar = true;
        TopMost = true;
        ShowIcon = false;
        DoubleBuffered = true;

        int left = 24, width = 352, y = 18;

        Controls.Add(Header("WMonitor", "系统监视器设置", left, y, width));
        y += 52;

        Controls.Add(Caption("贴片位置", left, y));
        y += 20;
        _pos = new Segmented
        {
            Items = new[] { "最左", "图标左", "图标右", "托盘左", "自定义" },
            SelectedIndex = (int)cfg.Anchor,
            Th = th,
            Left = left, Top = y, Width = width
        };
        _pos.SelectionChanged += (_, _) =>
        {
            _cfg.Anchor = (StripAnchor)_pos.SelectedIndex;
            Apply();
        };
        Controls.Add(_pos);
        y += 44;

        Controls.Add(Caption("贴片风格", left, y));
        y += 20;
        _strip = new Segmented
        {
            Items = new[] { "数字条", "迷你柱", "四环" },
            SelectedIndex = (int)cfg.Strip,
            Th = th,
            Left = left, Top = y, Width = width
        };
        _strip.SelectionChanged += (_, _) => { _cfg.Strip = (StripStyle)_strip.SelectedIndex; Apply(); };
        Controls.Add(_strip);
        y += 44;

        Controls.Add(Caption("托盘图标", left, y));
        y += 20;
        _tray = new Segmented
        {
            Items = new[] { "双数字", "双环", "四段条", "单指标" },
            SelectedIndex = (int)cfg.Tray,
            Th = th,
            Left = left, Top = y, Width = width
        };
        _tray.SelectionChanged += (_, _) => { _cfg.Tray = (TrayStyle)_tray.SelectedIndex; Apply(); };
        Controls.Add(_tray);
        y += 44;

        Controls.Add(Caption("主题", left, y));
        y += 20;
        _theme = new Segmented
        {
            Items = new[] { "深色", "浅色", "跟随系统" },
            SelectedIndex = (int)cfg.Theme,
            Th = th,
            Left = left, Top = y, Width = width
        };
        _theme.SelectionChanged += (_, _) =>
        {
            _cfg.Theme = (ThemeMode)_theme.SelectedIndex;
            Apply();
            Recolor();
        };
        Controls.Add(_theme);
        y += 44;

        Controls.Add(Caption("刷新间隔", left, y));
        y += 20;
        int rateIdx = Array.IndexOf(Rates, cfg.RefreshMs);
        _rate = new Segmented
        {
            Items = new[] { "0.5 秒", "1 秒", "2 秒", "5 秒" },
            SelectedIndex = rateIdx < 0 ? 1 : rateIdx,
            Th = th,
            Left = left, Top = y, Width = width
        };
        _rate.SelectionChanged += (_, _) =>
        {
            _cfg.RefreshMs = Rates[Math.Clamp(_rate.SelectedIndex, 0, Rates.Length - 1)];
            Apply();
        };
        Controls.Add(_rate);
        y += 46;

        Controls.Add(Caption("显示项", left, y));
        y += 22;

        var flow = new FlowLayoutPanel
        {
            Left = left, Top = y, Width = width, Height = 76,
            BackColor = th.PanelBgOpaque, WrapContents = true, AutoScroll = false
        };
        AddChip(flow, "CPU", () => _cfg.ShowCpu, v => _cfg.ShowCpu = v);
        AddChip(flow, "内存", () => _cfg.ShowRam, v => _cfg.ShowRam = v);
        AddChip(flow, "GPU", () => _cfg.ShowGpu, v => _cfg.ShowGpu = v);
        AddChip(flow, "显存", () => _cfg.ShowVram, v => _cfg.ShowVram = v);
        AddChip(flow, "网速", () => _cfg.ShowNet, v => _cfg.ShowNet = v);
        AddChip(flow, "GPU 温度", () => _cfg.ShowTemp, v => _cfg.ShowTemp = v);
        Controls.Add(flow);
        y += 84;

        Controls.Add(new Label
        {
            Text = "不透明度", Left = left, Top = y + 7, Width = 70,
            ForeColor = th.Label, BackColor = th.PanelBgOpaque, Tag = "caption"
        });
        _opacity = new Slider
        {
            Left = left + 72, Top = y, Width = 200,
            Value = cfg.StripOpacity, Th = th
        };
        _opacityText = new Label
        {
            Text = cfg.StripOpacity + "%", Left = left + 282, Top = y + 7, Width = 60,
            ForeColor = th.Text, BackColor = th.PanelBgOpaque
        };
        _opacity.ValueChanged += (_, _) =>
        {
            _cfg.StripOpacity = _opacity.Value;
            _opacityText.Text = _opacity.Value + "%";
            Apply();
        };
        Controls.Add(_opacity);
        Controls.Add(_opacityText);
        y += 42;

        var line = new Panel { Left = left, Top = y, Width = width, Height = 1, BackColor = th.Line };
        Controls.Add(line);
        y += 14;

        _onTop = new Chip("始终置顶")
        {
            Checked = cfg.AlwaysOnTop,
            Th = th,
            Left = left, Top = y
        };
        _onTop.CheckedChanged += (_, _) => { _cfg.AlwaysOnTop = _onTop.Checked; Apply(); };
        Controls.Add(_onTop);

        _startup = new Chip("开机自动启动")
        {
            Checked = Startup.IsEnabled(),
            Th = th,
            Left = left + 104, Top = y
        };
        _startup.CheckedChanged += (_, _) => { Startup.Set(_startup.Checked); _cfg.StartWithWindows = _startup.Checked; Apply(); };
        Controls.Add(_startup);
        y += 34;

        var offLabel = new Label
        {
            Text = "贴片水平微调",
            Left = left, Top = y + 4, Width = 110,
            ForeColor = th.Label, BackColor = th.PanelBgOpaque, Tag = "caption"
        };
        Controls.Add(offLabel);

        _offset = new NumericUpDown
        {
            Left = left + 112, Top = y, Width = 74,
            Minimum = -200, Maximum = 200, Value = Math.Clamp(cfg.OffsetX, -200, 200),
            BackColor = th.IsDark ? Color.FromArgb(38, 42, 48) : Color.White,
            ForeColor = th.Text,
            BorderStyle = BorderStyle.FixedSingle
        };
        _offset.ValueChanged += (_, _) => { _cfg.OffsetX = (int)_offset.Value; Apply(); };
        Controls.Add(_offset);

        var hint = new Label
        {
            Text = "负数左移 · 正数右移",
            Left = left + 194, Top = y + 4, Width = 160,
            ForeColor = th.Label, BackColor = th.PanelBgOpaque, Tag = "caption"
        };
        Controls.Add(hint);

        y += 42;

        var done = new Button
        {
            Text = "完成",
            Left = left + width - 88, Top = y, Width = 88, Height = 32,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(255, 10, 132, 255),
            ForeColor = Color.White,
            DialogResult = DialogResult.OK
        };
        done.FlatAppearance.BorderSize = 0;
        Controls.Add(done);
        AcceptButton = done;

        var reset = new Button
        {
            Text = "恢复默认",
            Left = left + width - 88 - 96, Top = y, Width = 88, Height = 32,
            FlatStyle = FlatStyle.Flat,
            BackColor = th.IsDark ? Color.FromArgb(38, 42, 48) : Color.FromArgb(232, 234, 238),
            ForeColor = th.Text
        };
        reset.FlatAppearance.BorderSize = 0;
        reset.Click += (_, _) => ResetDefaults();
        Controls.Add(reset);
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

    private Label Header(string title, string sub, int x, int y, int w) => new()
    {
        Text = title,
        Left = x, Top = y, Width = w, Height = 30,
        Font = new Font("Segoe UI", 15f, FontStyle.Bold),
        ForeColor = _th.Text, BackColor = _th.PanelBgOpaque, Tag = "header"
    };

    private Label Caption(string text, int x, int y) => new()
    {
        Text = text,
        Left = x, Top = y, Width = 200, Height = 18,
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
        _strip.SelectedIndex = (int)_cfg.Strip;
        _tray.SelectedIndex = (int)_cfg.Tray;
        _theme.SelectedIndex = (int)_cfg.Theme;
        _rate.SelectedIndex = 1;
        _offset.Value = 0;
        for (int i = 0; i < _chips.Count; i++)
            _chips[i].Checked = i switch
            {
                0 => _cfg.ShowCpu, 1 => _cfg.ShowRam, 2 => _cfg.ShowGpu,
                3 => _cfg.ShowVram, 4 => _cfg.ShowNet, _ => _cfg.ShowTemp
            };
        _pos.Invalidate(); _strip.Invalidate(); _tray.Invalidate(); _theme.Invalidate(); _rate.Invalidate();
        foreach (var c in _chips) c.Invalidate();
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
        else if (c is FlowLayoutPanel f) { f.BackColor = _th.PanelBgOpaque; foreach (Control x in f.Controls) RecolorControl(x); }
        else if (c is Label l)
        {
            l.BackColor = _th.PanelBgOpaque;
            l.ForeColor = (l.Tag as string) == "caption" ? _th.Label : _th.Text;
        }
        else if (c is NumericUpDown n)
        {
            n.BackColor = _th.IsDark ? Color.FromArgb(38, 42, 48) : Color.White;
            n.ForeColor = _th.Text;
        }
        else if (c is CheckBox cb) { cb.BackColor = _th.PanelBgOpaque; cb.ForeColor = _th.Text; }
        else if (c is Panel p && p.Height <= 2) { p.BackColor = _th.Line; }
    }
}
