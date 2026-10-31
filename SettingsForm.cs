using System.Runtime.InteropServices;
using WMonitor.Metrics;
using WMonitor.UI;

namespace WMonitor;

/// <summary>
/// 设置窗口（v0.11）：宽面板 + Bento 卡片网格，替掉原来 400 宽的单列长滚动。
///
/// 网格 4 列 × 3 行，卡片按「跨列数」拼：
/// <code>
/// ┌──────────────────────┬──────────┬──────────┐
/// │ 贴片 (跨 2)          │ 外观     │ 显示项   │
/// ├──────────────────────┼──────────┼──────────┤
/// │ 托盘图标 (跨 2)      │ 历史记录 │ 行为     │
/// ├──────────────────────┴──────────┴──────────┤
/// │ 告警 (跨 4)                                │
/// └────────────────────────────────────────────┘
/// </code>
///
/// 每张卡片自己维护「行」列表（行高 + 显示条件 + 单元格坐标），<see cref="LayoutCards"/>
/// 统一排版：一行的高度 = 该行最高卡片的内容高度。条件控件出现/消失（多屏、四段条、
/// 关历史、开告警…）时窗口自动伸缩，正常情况下全程不出现滚动条。
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly AppConfig _cfg;
    private Theme _th;
    private readonly List<TaskbarInfo> _bars;

    public event Action? Changed;

    // ---------------- 框架 ----------------
    private readonly Panel _header = new();
    private readonly Panel _footer = new();
    private readonly Panel _host = new();
    private Label _brand = null!, _tagline = null!, _ver = null!, _footNote = null!;
    private Panel _divider = null!;
    private BentoButton _done = null!, _reset = null!;

    // ---------------- 贴片 ----------------
    private Segmented _pos = null!, _strip = null!, _screen = null!;
    private Stepper _offset = null!;

    // ---------------- 外观 ----------------
    private Segmented _theme = null!, _rate = null!;
    private Slider _opacity = null!;
    private Label _opacityText = null!;

    // ---------------- 显示项 ----------------
    private readonly List<(Chip Chip, Func<AppConfig, bool> Dflt)> _chips = new();

    // ---------------- 托盘 ----------------
    private Segmented _tray = null!, _trayP = null!, _trayS = null!, _trayT = null!, _tray4 = null!;

    // ---------------- 历史 ----------------
    private Chip _histOn = null!, _histPersist = null!;
    private Segmented _retention = null!, _win = null!;
    private Label _histInfo = null!;
    private readonly List<int> _winValues = new() { 1, 5, 60 };
    private static readonly int[] RetentionHours = { 6, 24, 72 };
    private static readonly string[] RetentionNames = { "6 小时", "24 小时", "3 天" };

    // ---------------- 行为 ----------------
    private Chip _showStrip = null!, _lockPos = null!, _clickThrough = null!, _onTop = null!, _startup = null!;

    // ---------------- 告警 ----------------
    private Chip _alertOn = null!;
    private readonly Chip[] _alertChips = new Chip[5];
    private readonly Stepper[] _alertThr = new Stepper[5];
    private readonly Stepper[] _alertDur = new Stepper[5];
    private readonly List<(Chip Chip, Func<AppConfig, bool> Dflt)> _alertActions = new();
    private BentoButton _scriptBtn = null!;
    private Label _scriptPath = null!;

    // ---------------- 网格模型 ----------------
    /// <summary>单元格：控件 + 相对卡片内容原点的位置尺寸 + 独立显示条件（空 = 跟行）。</summary>
    private sealed record Cell(Control C, int X, int Y, int W, int H, Func<bool>? Show = null, Func<int>? XOf = null);

    /// <summary>一行：行高（含行间距）+ 显示条件 + 若干单元格。</summary>
    private sealed record Row(int H, Func<bool>? Show, Cell[] Cells);

    private sealed class CardSpec
    {
        public BentoCard Panel = null!;
        public int Row, Col, Span;
        public readonly List<Row> Rows = new();
        public readonly List<bool> RowOn = new();
        public int ContentH;
        public int NatH;
    }

    private readonly List<CardSpec> _cards = new();

    private const int Pad = 24;
    private const int Gap = 14;
    private const int FormW = 1238;
    private const int ColW = 287;          // (1238 - 24*2 - 14*3) / 4
    private const int InnerW = ColW - 36;  // 卡片左右各 18 内边距
    private const int HeadH = 64;
    private const int FootH = 56;
    private const int CtrlH = 34;
    private const int ChipH = 30;
    private const int ChipGap = 8;

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
        BackColor = th.WindowBg;
        ForeColor = th.Text;
        Font = new Font("Segoe UI", 9f);
        ShowInTaskbar = true;
        TopMost = true;
        ShowIcon = false;
        DoubleBuffered = true;

        BuildChrome();
        BuildCards();
        LayoutCards();
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

    // ================= 框架（标题栏 / 底栏 / 卡片容器） =================

    private void BuildChrome()
    {
        _header.BackColor = _th.WindowBg;
        _footer.BackColor = _th.WindowBg;
        _host.BackColor = _th.WindowBg;
        _host.AutoScroll = true;

        _brand = new Label
        {
            Text = "WMonitor",
            Left = Pad, Top = 11, Width = 220, Height = 30,
            Font = new Font("Segoe UI", 15f, FontStyle.Bold),
            ForeColor = _th.Text, BackColor = _th.WindowBg, Tag = "brand"
        };
        _tagline = new Label
        {
            Text = "系统监视器设置",
            Left = Pad + 2, Top = 40, Width = 280, Height = 18,
            Font = new Font("Segoe UI", 9f),
            ForeColor = _th.Label, BackColor = _th.WindowBg, Tag = "caption"
        };
        _ver = new Label
        {
            Text = AppVersion(),
            Left = FormW - Pad - 140, Top = 22, Width = 140, Height = 18,
            TextAlign = ContentAlignment.MiddleRight,
            Font = new Font("Segoe UI", 9f),
            ForeColor = _th.Label, BackColor = _th.WindowBg, Tag = "caption"
        };

        _header.Controls.AddRange(new Control[] { _brand, _tagline, _ver });

        _footNote = new Label
        {
            Text = "改动即时保存并生效",
            Left = Pad, Top = 19, Width = 320, Height = 20,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 9f),
            ForeColor = _th.Label, BackColor = _th.WindowBg, Tag = "caption"
        };

        _done = new BentoButton
        {
            Text = "完成",
            Width = 100, Height = 44,
            Primary = true, Th = _th,
            DialogResult = DialogResult.OK
        };
        AcceptButton = _done;

        _reset = new BentoButton
        {
            Text = "恢复默认",
            Width = 100, Height = 44,
            Th = _th
        };
        _reset.Click += (_, _) => ResetDefaults();

        _divider = new Panel { Height = 1, BackColor = _th.Line };

        _footer.Controls.AddRange(new Control[] { _footNote, _reset, _done });
        Controls.AddRange(new Control[] { _host, _header, _footer, _divider });
    }

    private static string AppVersion()
    {
        var v = typeof(SettingsForm).Assembly.GetName().Version;
        return v is null ? "" : $"v{v.Major}.{v.Minor:00}";
    }

    // ================= 卡片模型 =================

    private CardSpec NewCard(int row, int col, int span, string title, string note, Color accent)
    {
        var p = new BentoCard
        {
            Title = title,
            Note = note,
            Accent = accent,
            Th = _th,
            Outside = _th.WindowBg,
            BackColor = _th.CardBg
        };
        _host.Controls.Add(p);

        var s = new CardSpec { Panel = p, Row = row, Col = col, Span = span };
        _cards.Add(s);
        return s;
    }

    private static void AddRow(CardSpec s, int h, Func<bool>? show, params Cell[] cells)
    {
        foreach (var c in cells)
            if (c.C.Parent is null) s.Panel.Controls.Add(c.C);
        s.Rows.Add(new Row(h, show, cells));
    }

    // ================= 小控件工厂 =================

    /// <summary>字段标题（在控件上方，粗体小字）。</summary>
    private Label Cap(string text, int w) => new()
    {
        Text = text, Width = w, Height = 16, AutoSize = false,
        Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
        ForeColor = _th.Label, BackColor = _th.CardBg, Tag = "caption"
    };

    /// <summary>行内标题（在控件左侧，垂直居中）。</summary>
    private Label Inline(string text, int w) => new()
    {
        Text = text, Width = w, Height = 30, AutoSize = false,
        TextAlign = ContentAlignment.MiddleLeft,
        Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
        ForeColor = _th.Label, BackColor = _th.CardBg, Tag = "caption"
    };

    /// <summary>说明 / 单位小字。</summary>
    private Label Small(string text, int w) => new()
    {
        Text = text, Width = w, Height = 18, AutoSize = false,
        TextAlign = ContentAlignment.MiddleLeft,
        Font = new Font("Segoe UI", 8.5f),
        ForeColor = _th.Label, BackColor = _th.CardBg, Tag = "caption"
    };

    private Chip Toggle(string text, bool on, int w, Action<bool> set)
    {
        var c = new Chip(text) { Checked = on, Th = _th, Width = w, Height = ChipH };
        c.CheckedChanged += (_, _) => { set(c.Checked); Apply(); };
        return c;
    }

    // ================= 建卡 =================

    private void BuildCards()
    {
        BuildStripCard();      // 行0 列0 跨2
        BuildLookCard();       // 行0 列2
        BuildItemsCard();      // 行0 列3
        BuildTrayCard();       // 行1 列0 跨2
        BuildHistoryCard();    // 行1 列2
        BuildBehaviorCard();   // 行1 列3
        BuildAlertCard();      // 行2 列0 跨4
    }

    // ---- 贴片 ----
    private void BuildStripCard()
    {
        var card = NewCard(0, 0, 2, "贴片", "任务栏上的指标贴片", _th.Cpu);
        int half = ColW;
        int right = half + Gap;

        var capPos = Cap("位置", half);
        _pos = new Segmented
        {
            Items = new[] { "最左", "图标左", "图标右", "托盘左", "自定义" },
            SelectedIndex = (int)_cfg.Anchor,
            Th = _th, Height = CtrlH
        };
        _pos.SelectionChanged += (_, _) => { _cfg.Anchor = (StripAnchor)_pos.SelectedIndex; Apply(); };

        var capStyle = Cap("风格", half);
        _strip = new Segmented
        {
            Items = new[] { "数字条", "迷你柱", "四环" },
            SelectedIndex = (int)_cfg.Strip,
            Th = _th, Height = CtrlH
        };
        _strip.SelectionChanged += (_, _) => { _cfg.Strip = (StripStyle)_strip.SelectedIndex; Apply(); };

        AddRow(card, 62, null,
            new Cell(capPos, 0, 0, half, 16), new Cell(_pos, 0, 22, half, CtrlH),
            new Cell(capStyle, right, 0, half, 16), new Cell(_strip, right, 22, half, CtrlH));

        // 所在屏幕（只有多屏才有意义）
        var capScreen = Cap("所在屏幕", half);
        _screen = new Segmented { Items = Array.Empty<string>(), Th = _th, Height = CtrlH };
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

            _screen.Items = names.ToArray();
            _screen.SelectedIndex = sel;
            _screen.SelectionChanged += (_, _) =>
            {
                int i = _screen.SelectedIndex;
                if (i >= shown) _cfg.ShowOnAllMonitors = true;
                else { _cfg.ShowOnAllMonitors = false; _cfg.StripMonitor = _bars[i].Device; }
                Apply();
            };
        }

        // 水平微调：和「所在屏幕」共用一行；单屏时自动挪到左半，不留空洞
        var capOff = Inline("水平微调", 66);
        _offset = new Stepper { Width = 84, Height = 30, Th = _th, Minimum = -200, Maximum = 200 };
        _offset.Value = Math.Clamp(_cfg.OffsetX, -200, 200);
        _offset.ValueChanged += (_, _) => { _cfg.OffsetX = _offset.Value; Apply(); };
        var hint = Small("负 左移 · 正 右移", 150);
        int dx() => _bars.Count > 1 ? 0 : -right;   // 单屏时整组左移半个卡片宽

        AddRow(card, 62, null,
            new Cell(capScreen, 0, 0, half, 16, () => _bars.Count > 1),
            new Cell(_screen, 0, 22, half, CtrlH, () => _bars.Count > 1),
            new Cell(capOff, right, 0, 66, 16, null, () => right + dx()),
            new Cell(_offset, right + 4, 22, 84, 30, null, () => right + 4 + dx()),
            new Cell(hint, right + 96, 26, 150, 20, null, () => right + 96 + dx()));

        AddRow(card, 22, null,
            new Cell(Small("拖动贴片可直接移动 · 锁定后固定", ColW * 2 + Gap - 36), 0, 2, ColW * 2 + Gap - 36, 18));
    }

    // ---- 外观 ----
    private void BuildLookCard()
    {
        var card = NewCard(0, 2, 1, "外观", "", _th.Ram);
        int w = InnerW;

        var capTheme = Cap("主题", w);
        _theme = new Segmented
        {
            Items = new[] { "深色", "浅色", "跟随系统" },
            SelectedIndex = (int)_cfg.Theme,
            Th = _th, Height = CtrlH
        };
        _theme.SelectionChanged += (_, _) =>
        {
            _cfg.Theme = (ThemeMode)_theme.SelectedIndex;
            Apply();
            Recolor();
        };
        AddRow(card, 62, null, new Cell(capTheme, 0, 0, w, 16), new Cell(_theme, 0, 22, w, CtrlH));

        int rateIdx = Array.IndexOf(Rates, _cfg.RefreshMs);
        var capRate = Cap("刷新间隔", w);
        _rate = new Segmented
        {
            Items = new[] { "0.5 秒", "1 秒", "2 秒", "5 秒" },
            SelectedIndex = rateIdx < 0 ? 1 : rateIdx,
            Th = _th, Height = CtrlH
        };
        _rate.SelectionChanged += (_, _) =>
        {
            _cfg.RefreshMs = Rates[Math.Clamp(_rate.SelectedIndex, 0, Rates.Length - 1)];
            Apply();
        };
        AddRow(card, 62, null, new Cell(capRate, 0, 0, w, 16), new Cell(_rate, 0, 22, w, CtrlH));

        var capOp = Inline("不透明度", 84);
        _opacity = new Slider { Width = 118, Height = 26, Th = _th, Value = _cfg.StripOpacity };
        _opacityText = new Label
        {
            Text = _cfg.StripOpacity + "%", Width = 52, Height = 26,
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = _th.Text, BackColor = _th.CardBg
        };
        _opacity.ValueChanged += (_, _) =>
        {
            _cfg.StripOpacity = _opacity.Value;
            _opacityText.Text = _opacity.Value + "%";
            Apply();
        };
        AddRow(card, 48, null,
            new Cell(capOp, 0, 8, 84, 26),
            new Cell(_opacity, 88, 8, 104, 26),
            new Cell(_opacityText, 198, 8, 46, 26));
    }

    // ---- 显示项 ----
    private void BuildItemsCard()
    {
        var card = NewCard(0, 3, 1, "显示项", "贴片上显示什么", _th.Vram);
        var defs = new (string Text, Func<bool> Get, Action<bool> Set, Func<AppConfig, bool> Dflt)[]
        {
            ("CPU", () => _cfg.ShowCpu, v => _cfg.ShowCpu = v, d => d.ShowCpu),
            ("内存", () => _cfg.ShowRam, v => _cfg.ShowRam = v, d => d.ShowRam),
            ("GPU", () => _cfg.ShowGpu, v => _cfg.ShowGpu = v, d => d.ShowGpu),
            ("显存", () => _cfg.ShowVram, v => _cfg.ShowVram = v, d => d.ShowVram),
            ("网速", () => _cfg.ShowNet, v => _cfg.ShowNet = v, d => d.ShowNet),
            ("GPU 温度", () => _cfg.ShowTemp, v => _cfg.ShowTemp = v, d => d.ShowTemp),
            ("磁盘 IO", () => _cfg.ShowDisk, v => _cfg.ShowDisk = v, d => d.ShowDisk),
        };

        const int w = 120;
        const int gapX = 11;
        var made = new List<Chip>();
        foreach (var d in defs)
        {
            var chip = new Chip(d.Text) { Checked = d.Get(), Th = _th, Width = w, Height = ChipH };
            chip.CheckedChanged += (_, _) => { d.Set(chip.Checked); Apply(); };
            _chips.Add((chip, d.Dflt));
            made.Add(chip);
        }

        for (int i = 0; i < made.Count; i += 2)
        {
            var cells = new List<Cell> { new(made[i], 0, 0, w, ChipH) };
            if (i + 1 < made.Count) cells.Add(new Cell(made[i + 1], w + gapX, 0, w, ChipH));
            AddRow(card, ChipH + ChipGap, null, cells.ToArray());
        }
    }

    // ---- 托盘图标 ----
    private void BuildTrayCard()
    {
        var card = NewCard(1, 0, 2, "托盘图标", "每种样式各存一套指标", _th.Gpu);
        int w = ColW * 2 + Gap - 36;

        var capStyle = Cap("图标样式", w);
        _tray = new Segmented
        {
            Items = new[] { "双数字", "双环", "四段条", "单指标" },
            SelectedIndex = (int)_cfg.Tray,
            Th = _th, Height = CtrlH
        };
        _tray.SelectionChanged += (_, _) =>
        {
            _cfg.Tray = (TrayStyle)_tray.SelectedIndex;
            RefreshTraySlots();     // 换成这套样式自己存的指标，不是上一套的
            LayoutCards();
            Apply();
        };
        AddRow(card, 62, null, new Cell(capStyle, 0, 0, w, 16), new Cell(_tray, 0, 22, w, CtrlH));

        // 每行都读写「当前样式」的槽位：切样式时 RefreshTraySlots 把值换过去
        _trayP = MetricSeg(() => _cfg.TrayCurrent.Primary, m => _cfg.TrayCurrent.Primary = m);
        AddInlineRow(card, "主指标", _trayP, w);
        _trayS = MetricSeg(() => _cfg.TrayCurrent.Secondary, m => _cfg.TrayCurrent.Secondary = m);
        AddInlineRow(card, "次指标", _trayS, w, () => _cfg.Tray != TrayStyle.Single);
        _trayT = MetricSeg(() => _cfg.TrayCurrent.Third, m => _cfg.TrayCurrent.Third = m);
        AddInlineRow(card, "第三指标", _trayT, w, () => _cfg.Tray == TrayStyle.FourBar);
        _tray4 = MetricSeg(() => _cfg.TrayCurrent.Fourth, m => _cfg.TrayCurrent.Fourth = m);
        AddInlineRow(card, "第四指标", _tray4, w, () => _cfg.Tray == TrayStyle.FourBar);
    }

    /// <summary>把四个指标分段控件切到「当前样式」存的那一套（切样式后必须调）。</summary>
    private void RefreshTraySlots()
    {
        var c = _cfg.TrayCurrent;
        _trayP.SelectedIndex = (int)c.Primary;
        _trayS.SelectedIndex = (int)c.Secondary;
        _trayT.SelectedIndex = (int)c.Third;
        _tray4.SelectedIndex = (int)c.Fourth;
        _trayP.Invalidate();
        _trayS.Invalidate();
        _trayT.Invalidate();
        _tray4.Invalidate();
    }

    private void AddInlineRow(CardSpec card, string caption, Control ctrl, int w, Func<bool>? show = null)
    {
        var cap = Inline(caption, 76);
        AddRow(card, 42, show,
            new Cell(cap, 0, 2, 76, 30),
            new Cell(ctrl, 80, 0, w - 80, CtrlH));
    }

    private Segmented MetricSeg(Func<TrayMetric> get, Action<TrayMetric> set)
    {
        var s = new Segmented
        {
            Items = MetricNames,
            SelectedIndex = (int)get(),
            Th = _th, Height = CtrlH
        };
        s.SelectionChanged += (_, _) =>
        {
            set((TrayMetric)Math.Clamp(s.SelectedIndex, 0, MetricNames.Length - 1));
            Apply();
        };
        return s;
    }

    // ---- 历史 ----
    private void BuildHistoryCard()
    {
        var card = NewCard(1, 2, 1, "历史记录", "", _th.Net);
        int w = InnerW;

        _histOn = new Chip("记录历史") { Checked = _cfg.HistoryEnabled, Th = _th, Width = 120, Height = ChipH };
        _histPersist = new Chip("跨重启保留") { Checked = _cfg.HistoryPersist, Th = _th, Width = 120, Height = ChipH };
        _histOn.CheckedChanged += (_, _) =>
        {
            _cfg.HistoryEnabled = _histOn.Checked;
            Apply();
            LayoutCards();
        };
        _histPersist.CheckedChanged += (_, _) =>
        {
            _cfg.HistoryPersist = _histPersist.Checked;
            if (!_cfg.HistoryPersist && _cfg.ChartWindowMinutes > 60) _cfg.ChartWindowMinutes = 60;
            RefreshWindowSeg();
            Apply();
            LayoutCards();
        };
        AddRow(card, ChipH + ChipGap, null,
            new Cell(_histOn, 0, 0, 120, ChipH),
            new Cell(_histPersist, 131, 0, 120, ChipH));

        // 保留时长（只有开持久化才有意义）
        int ri = Array.IndexOf(RetentionHours, _cfg.HistoryRetentionHours);
        var capRet = Cap("保留时长", w);
        _retention = new Segmented
        {
            Items = RetentionNames,
            SelectedIndex = ri < 0 ? 1 : ri,
            Th = _th, Height = CtrlH
        };
        _retention.SelectionChanged += (_, _) =>
        {
            _cfg.HistoryRetentionHours = RetentionHours[Math.Clamp(_retention.SelectedIndex, 0, 2)];
            Apply();
            RefreshHistoryInfo();
        };
        AddRow(card, 62, () => _cfg.HistoryEnabled && _cfg.HistoryPersist,
            new Cell(capRet, 0, 0, w, 16), new Cell(_retention, 0, 22, w, CtrlH));

        // 面板默认时间窗
        var capWin = Cap("面板默认时间窗", w);
        _win = new Segmented { Th = _th, Height = CtrlH };
        _win.SelectionChanged += (_, _) =>
        {
            int i = Math.Clamp(_win.SelectedIndex, 0, _winValues.Count - 1);
            _cfg.ChartWindowMinutes = _winValues[i];
            Apply();
        };
        RefreshWindowSeg();
        AddRow(card, 62, () => _cfg.HistoryEnabled,
            new Cell(capWin, 0, 0, w, 16), new Cell(_win, 0, 22, w, CtrlH));

        _histInfo = Small("", w);
        _histInfo.Height = 20;
        RefreshHistoryInfo();
        AddRow(card, 24, null, new Cell(_histInfo, 0, 0, w, 20));

        AddRow(card, 22, () => !_cfg.HistoryEnabled,
            new Cell(Small("关闭后不再采样，面板无曲线", w), 0, 0, w, 18));
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

    // ---- 行为 ----
    private void BuildBehaviorCard()
    {
        var card = NewCard(1, 3, 1, "行为", "", _th.Disk);
        int w = InnerW;

        _showStrip = Toggle("显示贴片", _cfg.ShowStrip, w, v => _cfg.ShowStrip = v);
        _lockPos = Toggle("锁定位置", _cfg.LockPosition, w, v => _cfg.LockPosition = v);
        _clickThrough = Toggle("点击穿透", _cfg.ClickThrough, w, v => _cfg.ClickThrough = v);
        _onTop = Toggle("始终置顶", _cfg.AlwaysOnTop, w, v => _cfg.AlwaysOnTop = v);
        _startup = Toggle("开机自动启动", Startup.IsEnabled(), w, v =>
        {
            Startup.Set(v);
            _cfg.StartWithWindows = v;
        });

        foreach (var c in new[] { _showStrip, _lockPos, _clickThrough, _onTop, _startup })
            AddRow(card, ChipH + ChipGap, null, new Cell(c, 0, 0, w, ChipH));
    }

    // ---- 告警 ----
    private void BuildAlertCard()
    {
        var card = NewCard(2, 0, 4, "告警", "超过阈值时提醒", _th.Alert);
        int w = ColW * 4 + Gap * 3 - 36;

        _alertOn = new Chip("启用告警") { Checked = _cfg.AlertsEnabled, Th = _th, Width = 104, Height = ChipH };
        _alertOn.CheckedChanged += (_, _) =>
        {
            _cfg.AlertsEnabled = _alertOn.Checked;
            Apply();
            LayoutCards();
        };

        var top = new List<Cell> { new(_alertOn, 0, 0, 104, ChipH) };

        var acts = new (string Text, Func<bool> Get, Action<bool> Set, Func<AppConfig, bool> Dflt)[]
        {
            ("贴片变红", () => _cfg.AlertStrip, v => _cfg.AlertStrip = v, d => d.AlertStrip),
            ("托盘闪烁", () => _cfg.AlertTrayFlash, v => _cfg.AlertTrayFlash = v, d => d.AlertTrayFlash),
            ("系统通知", () => _cfg.AlertToast, v => _cfg.AlertToast = v, d => d.AlertToast),
            ("运行脚本", () => _cfg.AlertScript, v => { _cfg.AlertScript = v; LayoutCards(); }, d => d.AlertScript),
        };

        int x = 116;
        foreach (var a in acts)
        {
            var chip = new Chip(a.Text) { Checked = a.Get(), Th = _th, Width = 100, Height = ChipH };
            chip.CheckedChanged += (_, _) => { a.Set(chip.Checked); Apply(); };
            _alertActions.Add((chip, a.Dflt));
            top.Add(new Cell(chip, x, 0, 100, ChipH, () => _cfg.AlertsEnabled));
            x += 108;
        }

        _scriptBtn = new BentoButton
        {
            Text = "选择脚本…", Width = 104, Height = 38,
            Radius = 9, Shadow = 8, Th = _th
        };
        _scriptBtn.Click += (_, _) => PickScript();

        _scriptPath = new Label
        {
            Text = string.IsNullOrEmpty(_cfg.AlertScriptPath) ? "未选择" : Path.GetFileName(_cfg.AlertScriptPath),
            Width = 300, Height = 26,
            TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
            ForeColor = _th.Label, BackColor = _th.CardBg
        };

        // 关着的时候给一句说明，别让这张卡空着
        var offHint = Small("开启后可设置 CPU / 内存 / GPU / 显存 / 温度的阈值与持续时间", 460);
        offHint.Height = 30;
        top.Add(new Cell(offHint, 116, 0, 460, 30, () => !_cfg.AlertsEnabled));

        top.Add(new Cell(_scriptBtn, x + 12, 0, 104, 38, () => _cfg.AlertsEnabled && _cfg.AlertScript));
        top.Add(new Cell(_scriptPath, x + 124, 5, 300, 26, () => _cfg.AlertsEnabled && _cfg.AlertScript));

        AddRow(card, 44, null, top.ToArray());

        // 5 条规则排成 5 列：上面指标名，下面「阈值 %」+「持续 秒」
        int cellW = (w - 4 * 12) / 5;
        var ruleCells = new List<Cell>();
        for (int i = 0; i < 5; i++)
        {
            var m = (AlertMetric)i;
            var rule = _cfg.AlertRules[i];
            bool temp = m == AlertMetric.GpuTemp;
            int cx = i * (cellW + 12);

            var chip = new Chip(AlertNames[i]) { Checked = rule.Enabled, Th = _th, Width = cellW, Height = 28 };
            chip.CheckedChanged += (_, _) => { rule.Enabled = chip.Checked; Apply(); };
            _alertChips[i] = chip;

            var thr = new Stepper
            {
                Width = 78, Height = 30, Th = _th,
                Minimum = 40, Maximum = temp ? 110 : 100
            };
            thr.Value = (int)Math.Clamp(rule.Threshold, thr.Minimum, thr.Maximum);
            thr.ValueChanged += (_, _) => { rule.Threshold = thr.Value; Apply(); };
            _alertThr[i] = thr;

            var unit = Small(temp ? "°" : "%", 16);
            unit.Height = 30;
            unit.TextAlign = ContentAlignment.MiddleLeft;

            var dur = new Stepper
            {
                Width = 78, Height = 30, Th = _th,
                Minimum = 1, Maximum = 300
            };
            dur.Value = Math.Clamp(rule.DurationSec, 1, 300);
            dur.ValueChanged += (_, _) => { rule.DurationSec = dur.Value; Apply(); };
            _alertDur[i] = dur;

            var sec = Small("秒", 16);
            sec.Height = 30;
            sec.TextAlign = ContentAlignment.MiddleLeft;

            ruleCells.Add(new Cell(chip, cx, 0, cellW, 28, () => _cfg.AlertsEnabled));
            ruleCells.Add(new Cell(thr, cx, 34, 78, 30, () => _cfg.AlertsEnabled));
            ruleCells.Add(new Cell(unit, cx + 80, 34, 16, 30, () => _cfg.AlertsEnabled));
            ruleCells.Add(new Cell(dur, cx + 98, 34, 78, 30, () => _cfg.AlertsEnabled));
            ruleCells.Add(new Cell(sec, cx + 178, 34, 16, 30, () => _cfg.AlertsEnabled));
        }

        AddRow(card, 72, () => _cfg.AlertsEnabled, ruleCells.ToArray());
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

    // ================= 排版 =================

    private void LayoutCards()
    {
        // 1) 可见性 + 自然内容高度
        foreach (var s in _cards)
        {
            s.RowOn.Clear();
            s.NatH = 0;
            foreach (var r in s.Rows)
            {
                bool on = r.Show?.Invoke() ?? true;
                s.RowOn.Add(on);
                if (on) s.NatH += r.H;
            }
            s.ContentH = s.Panel.Head + s.NatH + s.Panel.PadBottom;
        }

        // 2) 网格行高 = 该行最高卡片
        int rowCount = 0;
        foreach (var s in _cards) rowCount = Math.Max(rowCount, s.Row + 1);
        var rowH = new int[rowCount];
        foreach (var s in _cards) rowH[s.Row] = Math.Max(rowH[s.Row], s.ContentH);

        // 3) 摆卡片：矮的卡片把多出来的高度均摊到各行，避免底部一大块空白
        int top = 6;
        for (int r = 0; r < rowCount; r++)
        {
            foreach (var s in _cards)
            {
                if (s.Row != r) continue;
                int x = Pad + s.Col * (ColW + Gap);
                int w = s.Span * ColW + (s.Span - 1) * Gap;
                s.Panel.SetBounds(x, top, w, rowH[r]);

                int visRows = s.RowOn.Count(b => b);
                int lastOn = s.RowOn.LastIndexOf(true);
                // 多出来的高度：行间最多各让 28px，剩下的留在底部（内容贴顶，不飘）
                int extra = visRows > 1
                    ? Math.Min(28, Math.Max(0, rowH[r] - s.ContentH) / (visRows - 1))
                    : 0;

                int y = s.Panel.Head;
                for (int i = 0; i < s.Rows.Count; i++)
                {
                    var row = s.Rows[i];
                    bool rowOn = s.RowOn[i];
                    foreach (var cell in row.Cells)
                    {
                        bool on = rowOn && (cell.Show?.Invoke() ?? true);
                        cell.C.Visible = on;
                        if (on)
                            cell.C.Bounds = new Rectangle(
                                s.Panel.PadX + (cell.XOf?.Invoke() ?? cell.X), y + cell.Y, cell.W, cell.H);
                    }
                    if (rowOn) y += row.H + (i == lastOn ? 0 : extra);
                }
            }
            top += rowH[r] + Gap;
        }

        int contentH = top - Gap + 6;
        _host.AutoScrollMinSize = new Size(FormW, contentH);

        int maxH = Math.Max(360, Screen.PrimaryScreen!.WorkingArea.Height - 140 - HeadH - FootH);
        int hostH = Math.Min(contentH, maxH);
        _host.SetBounds(0, HeadH, FormW, hostH);

        _header.SetBounds(0, 0, FormW, HeadH);
        _footer.SetBounds(0, HeadH + hostH, FormW, FootH);
        _divider.SetBounds(Pad, HeadH + hostH, FormW - Pad * 2, 1);

        _done.Left = FormW - Pad - _done.Width;
        _done.Top = 6;
        _reset.Left = _done.Left - 10 - _reset.Width;
        _reset.Top = 6;
        _ver.Left = FormW - Pad - _ver.Width;
        _ver.Top = 22;
        _footNote.Top = 19;

        ClientSize = new Size(FormW, HeadH + hostH + FootH);
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

    private void Apply()
    {
        _cfg.Save();
        Changed?.Invoke();
    }

    // ================= 恢复默认 =================

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
        _cfg.TrayDualNumber = d.TrayDualNumber.Clone();
        _cfg.TrayDualRing = d.TrayDualRing.Clone();
        _cfg.TrayFourBar = d.TrayFourBar.Clone();
        _cfg.TraySingle = d.TraySingle.Clone();
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
        RefreshTraySlots();
        _theme.SelectedIndex = (int)_cfg.Theme;

        int ri = Array.IndexOf(Rates, _cfg.RefreshMs);
        _rate.SelectedIndex = ri < 0 ? 1 : ri;

        _opacity.Value = _cfg.StripOpacity;
        _opacityText.Text = _cfg.StripOpacity + "%";
        _offset.Value = _cfg.OffsetX;

        foreach (var (chip, dflt) in _chips) chip.Checked = dflt(_cfg);

        _showStrip.Checked = _cfg.ShowStrip;
        _lockPos.Checked = _cfg.LockPosition;
        _clickThrough.Checked = _cfg.ClickThrough;
        _onTop.Checked = _cfg.AlwaysOnTop;

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

        InvalidateAll(_host);
        LayoutCards();
    }

    private static void InvalidateAll(Control root)
    {
        root.Invalidate();
        foreach (Control c in root.Controls) InvalidateAll(c);
    }

    private void Recolor()
    {
        _th = Theme.Resolve(_cfg.Theme);
        BackColor = _th.WindowBg;
        ForeColor = _th.Text;
        _header.BackColor = _th.WindowBg;
        _footer.BackColor = _th.WindowBg;
        _host.BackColor = _th.WindowBg;
        _divider.BackColor = _th.Line;

        RecolorControl(this);
        ApplyTitleBarTheme();
        InvalidateAll(this);
    }

    private void RecolorControl(Control c)
    {
        if (c is BentoCard bc)
        {
            bc.Th = _th;
            bc.Outside = _th.WindowBg;
            bc.BackColor = _th.CardBg;
            bc.Invalidate();
        }
        else if (c is Segmented s) { s.Th = _th; s.Invalidate(); }
        else if (c is Chip ch) { ch.Th = _th; ch.Invalidate(); }
        else if (c is Slider sl) { sl.Th = _th; sl.Invalidate(); }
        else if (c is Stepper st) { st.Th = _th; st.Invalidate(); }
        else if (c is Label l)
        {
            l.BackColor = l.Parent is BentoCard ? _th.CardBg : _th.WindowBg;
            l.ForeColor = (l.Tag as string) == "brand" ? _th.Text : _th.Label;
        }
        else if (c is BentoButton bb) { bb.Th = _th; bb.Invalidate(); }

        foreach (Control x in c.Controls) RecolorControl(x);
    }
}
