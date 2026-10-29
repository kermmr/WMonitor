using System.Runtime.InteropServices;
using WMonitor.Metrics;
using WMonitor.UI;

namespace WMonitor;

internal sealed class MonitorAppContext : ApplicationContext
{
    private readonly AppConfig _cfg;
    private readonly MetricSampler _sampler;
    private readonly TaskbarStrip _strip;
    private readonly DetailPanel _panel;
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _timer;

    private Theme _theme;
    private MetricsSnapshot _last = new();
    private volatile bool _paused;
    private string _trayKey = "";
    private int _tick;

    /// <summary>菜单项标签：单选项（勾选状态由委托算，避免一个枚举被多个子菜单共用时分不清）。</summary>
    private sealed record Radio(Func<bool> IsChecked, Action Apply);
    /// <summary>菜单项标签：开关项。</summary>
    private sealed record Toggle(Func<bool> Get, Action<bool> Set);

    public MonitorAppContext()
    {
        _cfg = AppConfig.Load();
        _theme = Theme.Resolve(_cfg.Theme);

        _sampler = new MetricSampler();
        _sampler.LockGpu(_cfg.LockedGpuLuid);

        _strip = new TaskbarStrip();
        _strip.Clicked += (_, _) => OpenPanel();
        _strip.Moved += pt =>
        {
            _cfg.Anchor = StripAnchor.Free;
            _cfg.FreeX = pt.X;
            _cfg.FreeY = pt.Y;
            _cfg.Save();
        };

        _panel = new DetailPanel();
        _panel.SettingsClicked += OpenSettings;

        _tray = new NotifyIcon
        {
            Visible = true,
            Text = "WMonitor",
            ContextMenuStrip = BuildMenu()
        };
        _tray.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) OpenSettings();
        };
        _tray.Icon = TrayIconRenderer.Render(_cfg, _last, _theme, TraySize);

        _timer = new System.Windows.Forms.Timer { Interval = Math.Clamp(_cfg.RefreshMs, 250, 10000) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
    }

    private static int TraySize => Math.Max(16, SystemInformation.SmallIconSize.Width);

    private void Tick()
    {
        if (_paused) return;
        MetricsSnapshot s;
        try { s = _sampler.Sample(); }
        catch { return; }
        _last = s;
        Apply(s);
    }

    private void Apply(MetricsSnapshot s)
    {
        if (_tick++ % 2 == 0 && _cfg.Theme == ThemeMode.System)
        {
            var t = Theme.Resolve(_cfg.Theme);
            if (t.IsDark != _theme.IsDark) { _theme = t; _trayKey = ""; }
        }

        _strip.Update(s, _cfg, _theme);
        _panel.PushHistory(s);
        if (_panel.Visible) _panel.Update(s, _cfg, _theme);

        string key = TrayIconRenderer.Key(_cfg, s, _theme);
        if (key != _trayKey)
        {
            _trayKey = key;
            var old = _tray.Icon;
            _tray.Icon = TrayIconRenderer.Render(_cfg, s, _theme, TraySize);
            old?.Dispose();
        }

        _tray.Text = $"CPU {s.CpuPercent:0}%  内存 {s.RamPercent:0}%" +
                     (s.HasGpu ? $"  GPU {s.GpuPercent:0}%" : "") +
                     (s.HasVramTotal ? $"  显存 {s.VramGb:0.0}/{s.VramTotalGb:0}G" : "");
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false };

        var pos = new ToolStripMenuItem("贴片位置");
        AddRadio(pos, "任务栏最左", () => _cfg.Anchor == StripAnchor.TaskbarLeft, () => _cfg.Anchor = StripAnchor.TaskbarLeft);
        AddRadio(pos, "应用图标左边", () => _cfg.Anchor == StripAnchor.BeforeApps, () => _cfg.Anchor = StripAnchor.BeforeApps);
        AddRadio(pos, "应用图标右边", () => _cfg.Anchor == StripAnchor.AfterApps, () => _cfg.Anchor = StripAnchor.AfterApps);
        AddRadio(pos, "托盘箭头左边", () => _cfg.Anchor == StripAnchor.BeforeTray, () => _cfg.Anchor = StripAnchor.BeforeTray);
        AddRadio(pos, "自定义（拖动贴片）", () => _cfg.Anchor == StripAnchor.Free, () => _cfg.Anchor = StripAnchor.Free);
        menu.Items.Add(pos);

        var strip = new ToolStripMenuItem("贴片风格");
        AddRadio(strip, "数字条", () => _cfg.Strip == StripStyle.Numbers, () => _cfg.Strip = StripStyle.Numbers);
        AddRadio(strip, "数字 + 迷你柱", () => _cfg.Strip == StripStyle.Bars, () => _cfg.Strip = StripStyle.Bars);
        AddRadio(strip, "四环仪表", () => _cfg.Strip == StripStyle.Rings, () => _cfg.Strip = StripStyle.Rings);
        menu.Items.Add(strip);

        var tray = new ToolStripMenuItem("托盘图标");
        AddRadio(tray, "双数字", () => _cfg.Tray == TrayStyle.DualNumber, () => _cfg.Tray = TrayStyle.DualNumber);
        AddRadio(tray, "双环", () => _cfg.Tray == TrayStyle.DualRing, () => _cfg.Tray = TrayStyle.DualRing);
        AddRadio(tray, "四段条", () => _cfg.Tray == TrayStyle.FourBar, () => _cfg.Tray = TrayStyle.FourBar);
        AddRadio(tray, "单指标", () => _cfg.Tray == TrayStyle.Single, () => _cfg.Tray = TrayStyle.Single);
        tray.DropDownItems.Add(new ToolStripSeparator());

        var primary = new ToolStripMenuItem("主指标");
        AddMetricRadios(primary, m => _cfg.TrayPrimary == m, m => _cfg.TrayPrimary = m);
        tray.DropDownItems.Add(primary);

        var secondary = new ToolStripMenuItem("次指标");
        AddMetricRadios(secondary, m => _cfg.TraySecondary == m, m => _cfg.TraySecondary = m);
        tray.DropDownItems.Add(secondary);
        menu.Items.Add(tray);

        var theme = new ToolStripMenuItem("主题");
        AddRadio(theme, "深色", () => _cfg.Theme == ThemeMode.Dark, () => SetTheme(ThemeMode.Dark));
        AddRadio(theme, "浅色", () => _cfg.Theme == ThemeMode.Light, () => SetTheme(ThemeMode.Light));
        AddRadio(theme, "跟随系统", () => _cfg.Theme == ThemeMode.System, () => SetTheme(ThemeMode.System));
        menu.Items.Add(theme);

        menu.Items.Add(new ToolStripSeparator());

        var fields = new ToolStripMenuItem("显示项");
        AddCheck(fields, "任务栏贴片", () => _cfg.ShowStrip, v => _cfg.ShowStrip = v);
        fields.DropDownItems.Add(new ToolStripSeparator());
        AddCheck(fields, "CPU", () => _cfg.ShowCpu, v => _cfg.ShowCpu = v);
        AddCheck(fields, "内存", () => _cfg.ShowRam, v => _cfg.ShowRam = v);
        AddCheck(fields, "GPU", () => _cfg.ShowGpu, v => _cfg.ShowGpu = v);
        AddCheck(fields, "显存", () => _cfg.ShowVram, v => _cfg.ShowVram = v);
        AddCheck(fields, "网速", () => _cfg.ShowNet, v => _cfg.ShowNet = v);
        AddCheck(fields, "GPU 温度", () => _cfg.ShowTemp, v => _cfg.ShowTemp = v);
        menu.Items.Add(fields);

        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(new ToolStripMenuItem("详情面板…", null, (_, _) => OpenPanel()));
        menu.Items.Add(new ToolStripMenuItem("设置…", null, (_, _) => OpenSettings()));

        var pause = new ToolStripMenuItem("暂停刷新", null, (_, _) => { }) { CheckOnClick = true };
        pause.Click += (_, _) => _paused = pause.Checked;
        menu.Items.Add(pause);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => Quit()));

        menu.Opening += (_, _) => SyncMenu(menu);
        return menu;
    }

    private static readonly (string Text, TrayMetric Metric)[] MetricMenu =
    {
        ("CPU", TrayMetric.Cpu),
        ("内存", TrayMetric.Ram),
        ("GPU", TrayMetric.Gpu),
        ("显存", TrayMetric.Vram),
        ("网速", TrayMetric.Net),
        ("GPU 温度", TrayMetric.Temp),
    };

    private void AddMetricRadios(ToolStripMenuItem parent, Func<TrayMetric, bool> isChecked, Action<TrayMetric> apply)
    {
        foreach (var (text, m) in MetricMenu)
            AddRadio(parent, text, () => isChecked(m), () => apply(m));
    }

    private void AddRadio(ToolStripMenuItem parent, string text, Func<bool> isChecked, Action apply)
    {
        var item = new ToolStripMenuItem(text) { Tag = new Radio(isChecked, apply), CheckOnClick = false };
        item.Click += (_, _) =>
        {
            apply();
            _cfg.Save();
            RefreshAll();
        };
        parent.DropDownItems.Add(item);
    }

    private void AddCheck(ToolStripMenuItem parent, string text, Func<bool> get, Action<bool> set)
    {
        var item = new ToolStripMenuItem(text) { CheckOnClick = true, Tag = new Toggle(get, set) };
        item.Click += (_, _) =>
        {
            set(item.Checked);
            _cfg.Save();
            RefreshAll();
        };
        parent.DropDownItems.Add(item);
    }

    private void SyncMenu(ContextMenuStrip menu)
    {
        foreach (ToolStripItem topItem in menu.Items)
        {
            if (topItem is not ToolStripMenuItem top || top.DropDownItems.Count == 0) continue;
            foreach (ToolStripItem sub in top.DropDownItems)
            {
                if (sub is not ToolStripMenuItem it) continue;
                if (it.Tag is Radio r) it.Checked = r.IsChecked();
                else if (it.Tag is Toggle t) it.Checked = t.Get();
            }
        }
    }

    private void SetTheme(ThemeMode mode)
    {
        _cfg.Theme = mode;
        _theme = Theme.Resolve(mode);
        _trayKey = "";
    }

    private void RefreshAll()
    {
        _timer.Interval = Math.Clamp(_cfg.RefreshMs, 250, 10000);
        _strip.Reanchor(_cfg);
        Apply(_last);
    }

    private void OpenPanel()
    {
        if (_panel.Visible) { _panel.Hide(); return; }

        var b = _strip.StripBounds;
        // 贴片被隐藏（或还没画出来）时没有锚点，退到鼠标位置
        if (!_cfg.ShowStrip || b.Width <= 0)
            b = new Rectangle(Cursor.Position, new Size(1, 1));

        _panel.OpenNear(b, _last, _cfg, _theme);
    }

    public void OpenSettingsPublic() => OpenSettings();
    public void OpenPanelPublic() => OpenPanel();

    private void OpenSettings()
    {
        using var f = new SettingsForm(_cfg, _theme);
        f.Changed += () =>
        {
            _theme = Theme.Resolve(_cfg.Theme);
            _trayKey = "";
            _timer.Interval = Math.Clamp(_cfg.RefreshMs, 250, 10000);
            Apply(_last);
        };
        f.ShowDialog();
        _cfg.Save();
        _theme = Theme.Resolve(_cfg.Theme);
        _trayKey = "";
        RefreshAll();
    }

    private void Quit()
    {
        _timer.Stop();
        _timer.Dispose();
        _tray.Visible = false;
        _tray.Icon = null;
        _tray.Dispose();
        _strip.Dispose();
        _panel.Dispose();
        _sampler.Dispose();
        _cfg.Save();
        ExitThread();
    }
}
