using System.Runtime.InteropServices;
using WMonitor.Metrics;
using WMonitor.UI;

namespace WMonitor;

/// <summary>
/// 设置窗口。布局用「块」描述：每块若干控件 + 各自相对偏移 + 块高 + 显示条件，
/// 由 <see cref="LayoutBlocks"/> 顺序排版。
/// v0.10 起内容超过屏幕高度，全部控件放进 <see cref="_scroll"/>（AutoScroll），
/// 所以「加控件」只需 AddBlock，不用管窗口能不能装下。
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly AppConfig _cfg;
    private Theme _th;
    private readonly List<TaskbarInfo> _bars;

    public event Action? Changed;

    private readonly Panel _scroll = new();

    private Label _header = null!;
    private Segmented _pos = null!, _strip = null!, _tray = null!, _theme = null!, _rate = null!;
    private Segmented? _screen;
    private Segmented _trayP = null!, _trayS = null!, _trayT = null!, _tray4 = null!;
    private Chip _startup = null!, _onTop = null!, _showStrip = null!, _lockPos = null!, _clickThrough = null!;
    private Slider _opacity = null!;
    private Label _opacityText = null!;
    private Stepper _offset = null!;
    private FlowLayoutPanel _flow = null!, _behavior = null!;

    // 历史
    private Chip _histOn = null!, _histPersist = null!;
    private Segmented _retention = null!, _win = null!;
    private Label _histInfo = null!;
    private readonly List<int> _winValues = new() { 1, 5, 60 };
    private static readonly int[] RetentionHours = { 6, 24, 72 };
    private static readonly string[] RetentionNames = { "6 小时", "24 小时", "3 天" };

    // 告警
    private Chip _alertOn = null!;
    private readonly Chip[] _alertChips = new Chip[5];
    private readonly Stepper[] _alertThr = new Stepper[5];
    private readonly Stepper[] _alertDur = new Stepper[5];
    private readonly List<(Chip Chip, Func<AppConfig, bool> Dflt)> _alertActions = new();
    private Button _scriptBtn = null!;
    private Label _scriptPath = null!;

    /// <summary>显示项 chip + 它的默认值访问器（不再用下标映射，加 chip 不会错位）。</summary>
    private readonly List<(Chip Chip, Func<AppConfig, bool> Dflt)> _chips = new();

    private readonly List<Block> _blocks = new();
    private sealed record Block(Control[] Ctrls, int[] Dy, int Height, Func<bool> Show);

    private const int Pad = 24;
    private const int ContentW = 352;
    private const int FormW = 400;
    private const int CaptionH = 18;
    private const int CtrlH = 34;

    private static readonly int[] Rates = { 500, 1000, 2000, 5000 };
    private static readonly string[] MetricNames = { "CPU", "内存", "GPU", "显存", "网速", "温度", "磁盘" };
    private static readonly string[] AlertNames = { "CPU", "内存", "GPU", "显存", "温度" };

    public SettingsForm(AppConfig cfg, Theme th, List<TaskbarInfo> bars)
    {
        _cfg = cfg;
        _th = th;
        _bars = bars;

        NormalizeRules();

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

        _scroll.Dock = DockStyle.Fill;
        _scroll.AutoScroll = true;
        _scroll.BackColor = th.PanelBgOpaque;
        Controls.Add(_scroll);

        BuildUi();
        LayoutBlocks();
    }

    /// <summary>把配置里的规则按指标归位成 5 条，UI 才能按下标访问。</summary>
    private void NormalizeRules()
    {
        var defs = AlertRule.Defaults();
        var list = new AlertRule[5];
        for (int i = 0; i < 5; i++)
        {
            var m = (AlertMetric)i;
            var existing = _cfg.AlertRules?.FirstOrDefault(r => r != null && r.Metric == m);
            list[i] = existing?.Clone() ?? defs[i].Clone();
            list[i].Metric = m;
            list[i].DurationSec = Math.Clamp(list[i].DurationSec, 1, 300);
            list[i].Threshold = Math.Clamp(list[i].Threshold, 40, m == AlertMetric.GpuTemp ? 110 : 100);
        }
        _cfg.AlertRules = list;
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
            Left = Pad, Top = 0, Width = ContentW - 20, Height = 104,
            BackColor = _th.PanelBgOpaque, WrapContents = true, AutoScroll = false
        };
        AddChip(_flow, "CPU", () => _cfg.ShowCpu, v => _cfg.ShowCpu = v, d => d.ShowCpu);
        AddChip(_flow, "内存", () => _cfg.ShowRam, v => _cfg.ShowRam = v, d => d.ShowRam);
        AddChip(_flow, "GPU", () => _cfg.ShowGpu, v => _cfg.ShowGpu = v, d => d.ShowGpu);
        AddChip(_flow, "显存", () => _cfg.ShowVram, v => _cfg.ShowVram = v, d => d.ShowVram);
        AddChip(_flow, "网速", () => _cfg.ShowNet, v => _cfg.ShowNet = v, d => d.ShowNet);
        AddChip(_flow, "GPU 温度", () => _cfg.ShowTemp, v => _cfg.ShowTemp = v, d => d.ShowTemp);
        AddChip(_flow, "磁盘 IO", () => _cfg.ShowDisk, v => _cfg.ShowDisk = v, d => d.ShowDisk);
        AddBlock(22 + 104 + 8, null, (capFields, 0), (_flow, 22));

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

        AddBlock(16, null, (Separator(), 0));

        BuildHistorySection();

        AddBlock(16, null, (Separator(), 0));

        BuildAlertSection();

        AddBlock(16, null, (Separator(), 0));

        // ---- 行为开关（5 个 chip 一行放不下，改用流式两行）----
        _behavior = new FlowLayoutPanel
        {
            Left = Pad, Top = 0, Width = ContentW - 20, Height = 76,
            BackColor = _th.PanelBgOpaque, WrapContents = true, AutoScroll = false
        };
        _onTop = new Chip("始终置顶") { Checked = _cfg.AlwaysOnTop, Th = _th, Margin = new Padding(0, 0, 8, 8) };
        _startup = new Chip("开机自动启动") { Checked = Startup.IsEnabled(), Th = _th, Margin = new Padding(0, 0, 8, 8) };
        _showStrip = new Chip("显示贴片") { Checked = _cfg.ShowStrip, Th = _th, Margin = new Padding(0, 0, 8, 8) };
        _lockPos = new Chip("锁定位置") { Checked = _cfg.LockPosition, Th = _th, Margin = new Padding(0, 0, 8, 8) };
        _clickThrough = new Chip("点击穿透") { Checked = _cfg.ClickThrough, Th = _th, Margin = new Padding(0, 0, 8, 8) };

        _onTop.CheckedChanged += (_, _) => { _cfg.AlwaysOnTop = _onTop.Checked; Apply(); };
        _startup.CheckedChanged += (_, _) => { Startup.Set(_startup.Checked); _cfg.StartWithWindows = _startup.Checked; Apply(); };
        _showStrip.CheckedChanged += (_, _) => { _cfg.ShowStrip = _showStrip.Checked; Apply(); };
        _lockPos.CheckedChanged += (_, _) => { _cfg.LockPosition = _lockPos.Checked; Apply(); };
        _clickThrough.CheckedChanged += (_, _) => { _cfg.ClickThrough = _clickThrough.Checked; Apply(); };

        _behavior.Controls.AddRange(new Control[] { _onTop, _startup, _showStrip, _lockPos, _clickThrough });
        AddBlock(76 + 10, null, (_behavior, 0));

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
        AddBlock(32 + 8, null, (reset, 0), (done, 0));
    }

    // ---------------- 历史分区 ----------------

    private void BuildHistorySection()
    {
        var cap = Caption("历史记录");
        cap.Width = 120;

        var row = new FlowLayoutPanel
        {
            Left = Pad, Top = 0, Width = ContentW - 20, Height = 40,
            BackColor = _th.PanelBgOpaque, WrapContents = true, AutoScroll = false
        };
        _histOn = new Chip("记录历史") { Checked = _cfg.HistoryEnabled, Th = _th, Margin = new Padding(0, 0, 8, 8) };
        _histPersist = new Chip("跨重启保留") { Checked = _cfg.HistoryPersist, Th = _th, Margin = new Padding(0, 0, 8, 8) };

        _histOn.CheckedChanged += (_, _) =>
        {
            _cfg.HistoryEnabled = _histOn.Checked;
            Apply();
            LayoutBlocks();
        };
        _histPersist.CheckedChanged += (_, _) =>
        {
            _cfg.HistoryPersist = _histPersist.Checked;
            if (!_cfg.HistoryPersist && _cfg.ChartWindowMinutes > 60) _cfg.ChartWindowMinutes = 60;
            RefreshWindowSeg();
            Apply();
            LayoutBlocks();
        };
        row.Controls.AddRange(new Control[] { _histOn, _histPersist });
        AddBlock(22 + 40 + 6, null, (cap, 0), (row, 22));

        // 保留时长（只有开持久化才有意义）
        int ri = Array.IndexOf(RetentionHours, _cfg.HistoryRetentionHours);
        _retention = new Segmented
        {
            Items = RetentionNames,
            SelectedIndex = ri < 0 ? 1 : ri,
            Th = _th, Left = Pad, Width = ContentW, Height = CtrlH
        };
        _retention.SelectionChanged += (_, _) =>
        {
            _cfg.HistoryRetentionHours = RetentionHours[Math.Clamp(_retention.SelectedIndex, 0, 2)];
            Apply();
            RefreshHistoryInfo();
        };
        AddCaptionBlock("保留时长", _retention, () => _cfg.HistoryEnabled && _cfg.HistoryPersist);

        // 面板默认时间窗
        _win = new Segmented { Th = _th, Left = Pad, Width = ContentW, Height = CtrlH };
        _win.SelectionChanged += (_, _) =>
        {
            int i = Math.Clamp(_win.SelectedIndex, 0, _winValues.Count - 1);
            _cfg.ChartWindowMinutes = _winValues[i];
            Apply();
        };
        RefreshWindowSeg();
        AddCaptionBlock("面板默认时间窗", _win, () => _cfg.HistoryEnabled);

        _histInfo = Caption("");
        _histInfo.Width = ContentW;
        _histInfo.Height = 20;
        RefreshHistoryInfo();
        AddBlock(24, () => _cfg.HistoryEnabled && _cfg.HistoryPersist, (_histInfo, 0));
    }

    /// <summary>没开持久化时不给 24 小时档（没有数据源，不给死链）。</summary>
    private void RefreshWindowSeg()
    {
        _winValues.Clear();
        _winValues.AddRange(new[] { 1, 5, 60 });
        var names = new List<string> { "1 分钟", "5 分钟", "1 小时" };
        if (_cfg.HistoryPersist)
        {
            _winValues.Add(1440);
            names.Add("24 小时");
        }

        _win.Items = names.ToArray();
        int idx = _winValues.IndexOf(_cfg.ChartWindowMinutes);
        _win.SelectedIndex = idx < 0 ? 0 : idx;
        _win.Invalidate();
    }

    private void RefreshHistoryInfo()
    {
        try
        {
            string dir = HistoryStore.Dir;
            if (!Directory.Exists(dir)) { _histInfo.Text = "尚未记录"; return; }
            long bytes = 0;
            var files = new DirectoryInfo(dir).GetFiles("*.hist");
            foreach (var f in files) bytes += f.Length;
            _histInfo.Text = $"已记录 {bytes / 1024.0 / 1024:0.0} MB · {files.Length} 个日文件";
        }
        catch { _histInfo.Text = ""; }
    }

    // ---------------- 告警分区 ----------------

    private void BuildAlertSection()
    {
        var cap = Caption("告警");
        cap.Width = 120;

        _alertOn = new Chip("启用告警") { Checked = _cfg.AlertsEnabled, Th = _th, Margin = new Padding(0, 0, 8, 8) };
        _alertOn.CheckedChanged += (_, _) =>
        {
            _cfg.AlertsEnabled = _alertOn.Checked;
            Apply();
            LayoutBlocks();
        };

        var row = new FlowLayoutPanel
        {
            Left = Pad, Top = 0, Width = ContentW - 20, Height = 40,
            BackColor = _th.PanelBgOpaque, WrapContents = true, AutoScroll = false
        };
        row.Controls.Add(_alertOn);
        AddBlock(22 + 40 + 6, null, (cap, 0), (row, 22));

        for (int i = 0; i < 5; i++) AddAlertRow((AlertMetric)i, AlertNames[i]);

        // 动作
        var capAct = Caption("触发时");
        capAct.Width = 120;
        var acts = new FlowLayoutPanel
        {
            Left = Pad, Top = 0, Width = ContentW - 20, Height = 76,
            BackColor = _th.PanelBgOpaque, WrapContents = true, AutoScroll = false
        };
        AddActionChip(acts, "贴片变红", () => _cfg.AlertStrip, v => _cfg.AlertStrip = v, d => d.AlertStrip);
        AddActionChip(acts, "托盘闪烁", () => _cfg.AlertTrayFlash, v => _cfg.AlertTrayFlash = v, d => d.AlertTrayFlash);
        AddActionChip(acts, "系统通知", () => _cfg.AlertToast, v => _cfg.AlertToast = v, d => d.AlertToast);
        AddActionChip(acts, "运行脚本", () => _cfg.AlertScript, v => { _cfg.AlertScript = v; LayoutBlocks(); }, d => d.AlertScript);
        AddBlock(22 + 76 + 6, () => _cfg.AlertsEnabled, (capAct, 0), (acts, 22));

        // 脚本路径
        _scriptBtn = new Button
        {
            Text = "选择脚本…",
            Left = Pad, Top = 0, Width = 96, Height = 30,
            FlatStyle = FlatStyle.Flat,
            BackColor = _th.IsDark ? Color.FromArgb(38, 42, 48) : Color.FromArgb(232, 234, 238),
            ForeColor = _th.Text
        };
        _scriptBtn.FlatAppearance.BorderSize = 0;
        _scriptBtn.Click += (_, _) => PickScript();

        _scriptPath = new Label
        {
            Text = string.IsNullOrEmpty(_cfg.AlertScriptPath) ? "未选择" : Path.GetFileName(_cfg.AlertScriptPath),
            Left = Pad + 104, Top = 0, Width = ContentW - 104, Height = 30,
            TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
            ForeColor = _th.Label, BackColor = _th.PanelBgOpaque
        };
        AddBlock(30 + 10, () => _cfg.AlertsEnabled && _cfg.AlertScript, (_scriptBtn, 0), (_scriptPath, 0));
    }

    private void AddAlertRow(AlertMetric m, string name)
    {
        var rule = _cfg.AlertRules[(int)m];
        bool temp = m == AlertMetric.GpuTemp;

        var chip = new Chip(name)
        {
            Checked = rule.Enabled, Th = _th,
            Left = Pad, Width = 62, Height = 30
        };
        chip.CheckedChanged += (_, _) => { rule.Enabled = chip.Checked; Apply(); };
        _alertChips[(int)m] = chip;

        // Stepper 两侧按钮各 26 宽，所以总宽至少要 26*2 + 数字宽；76 会把 "90" 挤成两行
        var thr = new Stepper
        {
            Left = Pad + 66, Top = 0, Width = 90, Height = 30, Th = _th,
            Minimum = 40, Maximum = temp ? 110 : 100
        };
        thr.Value = (int)Math.Clamp(rule.Threshold, thr.Minimum, thr.Maximum);
        thr.ValueChanged += (_, _) => { rule.Threshold = thr.Value; Apply(); };
        _alertThr[(int)m] = thr;

        var unit = Caption(temp ? "°" : "%");
        unit.Left = Pad + 160; unit.Width = 14; unit.Height = 30;
        unit.TextAlign = ContentAlignment.MiddleLeft;

        var durCap = Caption("持续");
        durCap.Left = Pad + 178; durCap.Width = 40; durCap.Height = 30;
        durCap.TextAlign = ContentAlignment.MiddleLeft;

        var dur = new Stepper
        {
            Left = Pad + 222, Top = 0, Width = 84, Height = 30, Th = _th,
            Minimum = 1, Maximum = 300
        };
        dur.Value = Math.Clamp(rule.DurationSec, 1, 300);
        dur.ValueChanged += (_, _) => { rule.DurationSec = dur.Value; Apply(); };
        _alertDur[(int)m] = dur;

        var sec = Caption("秒");
        sec.Left = Pad + 310; sec.Width = 16; sec.Height = 30;
        sec.TextAlign = ContentAlignment.MiddleLeft;

        AddBlock(30 + 6, () => _cfg.AlertsEnabled,
            (chip, 0), (thr, 0), (unit, 0), (durCap, 0), (dur, 0), (sec, 0));
    }

    private void AddActionChip(FlowLayoutPanel flow, string text,
        Func<bool> get, Action<bool> set, Func<AppConfig, bool> dflt)
    {
        var chip = new Chip(text) { Checked = get(), Th = _th, Margin = new Padding(0, 0, 8, 8) };
        chip.CheckedChanged += (_, _) => { set(chip.Checked); Apply(); };
        _alertActions.Add((chip, dflt));
        flow.Controls.Add(chip);
    }

    private void PickScript()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "选择告警脚本",
            Filter = "脚本 (*.bat;*.cmd;*.ps1;*.exe)|*.bat;*.cmd;*.ps1;*.exe|所有文件 (*.*)|*.*"
        };
        if (!string.IsNullOrEmpty(_cfg.AlertScriptPath))
        {
            string? dir = Path.GetDirectoryName(_cfg.AlertScriptPath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) dlg.InitialDirectory = dir;
        }
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        _cfg.AlertScriptPath = dlg.FileName;
        _scriptPath.Text = Path.GetFileName(dlg.FileName);
        Apply();
    }

    // ---------------- 布局 ----------------

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

    private Panel Separator() => new()
    {
        Left = Pad, Top = 0, Width = ContentW, Height = 1, BackColor = _th.Line
    };

    private void AddBlock(int height, Func<bool>? show, params (Control C, int Dy)[] items)
    {
        foreach (var (c, _) in items) _scroll.Controls.Add(c);
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

        int contentH = y + 14;
        int maxH = Math.Max(420, Screen.PrimaryScreen!.WorkingArea.Height - 120);
        _scroll.AutoScrollMinSize = new Size(FormW - SystemInformation.VerticalScrollBarWidth - 6, contentH);
        ClientSize = new Size(FormW, Math.Min(contentH, maxH));
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

    private void AddChip(FlowLayoutPanel flow, string text,
        Func<bool> get, Action<bool> set, Func<AppConfig, bool> dflt)
    {
        var chip = new Chip(text) { Checked = get(), Th = _th, Margin = new Padding(0, 0, 8, 8) };
        chip.CheckedChanged += (_, _) => { set(chip.Checked); Apply(); };
        _chips.Add((chip, dflt));
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

    // ---------------- 恢复默认 ----------------

    private void ResetDefaults()
    {
        var d = new AppConfig();

        _cfg.Strip = d.Strip;
        _cfg.Tray = d.Tray;
        _cfg.Theme = d.Theme;
        _cfg.RefreshMs = d.RefreshMs;
        _cfg.ShowCpu = d.ShowCpu; _cfg.ShowRam = d.ShowRam; _cfg.ShowGpu = d.ShowGpu;
        _cfg.ShowVram = d.ShowVram; _cfg.ShowNet = d.ShowNet; _cfg.ShowTemp = d.ShowTemp;
        _cfg.ShowDisk = d.ShowDisk;
        _cfg.ShowStrip = d.ShowStrip;
        _cfg.TrayPrimary = d.TrayPrimary; _cfg.TraySecondary = d.TraySecondary;
        _cfg.TrayThird = d.TrayThird; _cfg.TrayFourth = d.TrayFourth;
        _cfg.Anchor = d.Anchor;
        _cfg.AlwaysOnTop = d.AlwaysOnTop;
        _cfg.StripOpacity = d.StripOpacity;
        _cfg.OffsetX = d.OffsetX;
        _cfg.LockPosition = d.LockPosition;
        _cfg.ClickThrough = d.ClickThrough;

        _cfg.HistoryEnabled = d.HistoryEnabled;
        _cfg.HistoryPersist = d.HistoryPersist;
        _cfg.HistoryRetentionHours = d.HistoryRetentionHours;
        _cfg.ChartWindowMinutes = d.ChartWindowMinutes;

        _cfg.AlertsEnabled = d.AlertsEnabled;
        _cfg.AlertRules = AlertRule.Defaults();
        _cfg.AlertStrip = d.AlertStrip;
        _cfg.AlertTrayFlash = d.AlertTrayFlash;
        _cfg.AlertToast = d.AlertToast;
        _cfg.AlertScript = d.AlertScript;

        RefreshControlsFromConfig();
        Apply();
        Recolor();
    }

    /// <summary>把所有控件状态从配置刷一遍。直接赋字段不触发事件，所以不会反过来写配置。</summary>
    private void RefreshControlsFromConfig()
    {
        _pos.SelectedIndex = (int)_cfg.Anchor;
        _strip.SelectedIndex = (int)_cfg.Strip;
        _tray.SelectedIndex = (int)_cfg.Tray;
        _trayP.SelectedIndex = (int)_cfg.TrayPrimary;
        _trayS.SelectedIndex = (int)_cfg.TraySecondary;
        _trayT.SelectedIndex = (int)_cfg.TrayThird;
        _tray4.SelectedIndex = (int)_cfg.TrayFourth;
        _theme.SelectedIndex = (int)_cfg.Theme;

        int ri = Array.IndexOf(Rates, _cfg.RefreshMs);
        _rate.SelectedIndex = ri < 0 ? 1 : ri;

        _opacity.Value = _cfg.StripOpacity;
        _opacityText.Text = _cfg.StripOpacity + "%";
        _offset.Value = _cfg.OffsetX;

        foreach (var (chip, dflt) in _chips) chip.Checked = dflt(_cfg);

        _onTop.Checked = _cfg.AlwaysOnTop;
        _showStrip.Checked = _cfg.ShowStrip;
        _lockPos.Checked = _cfg.LockPosition;
        _clickThrough.Checked = _cfg.ClickThrough;

        _histOn.Checked = _cfg.HistoryEnabled;
        _histPersist.Checked = _cfg.HistoryPersist;
        int hi = Array.IndexOf(RetentionHours, _cfg.HistoryRetentionHours);
        _retention.SelectedIndex = hi < 0 ? 1 : hi;
        RefreshWindowSeg();
        RefreshHistoryInfo();

        _alertOn.Checked = _cfg.AlertsEnabled;
        for (int i = 0; i < 5; i++)
        {
            var r = _cfg.AlertRules[i];
            _alertChips[i].Checked = r.Enabled;
            _alertThr[i].Value = (int)Math.Clamp(r.Threshold, _alertThr[i].Minimum, _alertThr[i].Maximum);
            _alertDur[i].Value = Math.Clamp(r.DurationSec, 1, 300);
        }
        foreach (var (chip, dflt) in _alertActions) chip.Checked = dflt(_cfg);

        InvalidateAll(_scroll);
        LayoutBlocks();
    }

    private static void InvalidateAll(Control root)
    {
        root.Invalidate();
        foreach (Control c in root.Controls) InvalidateAll(c);
    }

    private void Recolor()
    {
        _th = Theme.Resolve(_cfg.Theme);
        BackColor = _th.PanelBgOpaque;
        ForeColor = _th.Text;
        _scroll.BackColor = _th.PanelBgOpaque;
        RecolorControl(_scroll);
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
        else if (c is Panel p)
        {
            if (p.Height <= 2) p.BackColor = _th.Line;
            else if (ReferenceEquals(p, _scroll)) p.BackColor = _th.PanelBgOpaque;
        }
    }
}
