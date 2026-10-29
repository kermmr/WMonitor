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
        _tray.Icon = TrayIconRenderer.Render(_cfg.Tray, _last, _theme, TraySize);

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

        string key = $"{_cfg.Tray}|{(int)s.CpuPercent}|{(int)s.GpuPercent}|{_theme.IsDark}";
        if (key != _trayKey)
        {
            _trayKey = key;
            var old = _tray.Icon;
            _tray.Icon = TrayIconRenderer.Render(_cfg.Tray, s, _theme, TraySize);
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
        AddRadio(pos, "任务栏最左", StripAnchor.TaskbarLeft, v => _cfg.Anchor = (StripAnchor)v!);
        AddRadio(pos, "应用图标左边", StripAnchor.BeforeApps, v => _cfg.Anchor = (StripAnchor)v!);
        AddRadio(pos, "应用图标右边", StripAnchor.AfterApps, v => _cfg.Anchor = (StripAnchor)v!);
        AddRadio(pos, "托盘箭头左边", StripAnchor.BeforeTray, v => _cfg.Anchor = (StripAnchor)v!);
        AddRadio(pos, "自定义（拖动贴片）", StripAnchor.Free, v => _cfg.Anchor = (StripAnchor)v!);
        menu.Items.Add(pos);

        var strip = new ToolStripMenuItem("贴片风格");
        AddRadio(strip, "数字条", StripStyle.Numbers, v => _cfg.Strip = (StripStyle)v!);
        AddRadio(strip, "数字 + 迷你柱", StripStyle.Bars, v => _cfg.Strip = (StripStyle)v!);
        AddRadio(strip, "四环仪表", StripStyle.Rings, v => _cfg.Strip = (StripStyle)v!);
        menu.Items.Add(strip);

        var tray = new ToolStripMenuItem("托盘图标");
        AddRadio(tray, "双数字", TrayStyle.DualNumber, v => _cfg.Tray = (TrayStyle)v!);
        AddRadio(tray, "双环", TrayStyle.DualRing, v => _cfg.Tray = (TrayStyle)v!);
        AddRadio(tray, "四段条", TrayStyle.FourBar, v => _cfg.Tray = (TrayStyle)v!);
        AddRadio(tray, "单指标", TrayStyle.Single, v => _cfg.Tray = (TrayStyle)v!);
        menu.Items.Add(tray);

        var theme = new ToolStripMenuItem("主题");
        AddRadio(theme, "深色", ThemeMode.Dark, v => SetTheme((ThemeMode)v!));
        AddRadio(theme, "浅色", ThemeMode.Light, v => SetTheme((ThemeMode)v!));
        AddRadio(theme, "跟随系统", ThemeMode.System, v => SetTheme((ThemeMode)v!));
        menu.Items.Add(theme);

        menu.Items.Add(new ToolStripSeparator());

        var fields = new ToolStripMenuItem("显示项");
        AddCheck(fields, "CPU", () => _cfg.ShowCpu, v => _cfg.ShowCpu = v);
        AddCheck(fields, "内存", () => _cfg.ShowRam, v => _cfg.ShowRam = v);
        AddCheck(fields, "GPU", () => _cfg.ShowGpu, v => _cfg.ShowGpu = v);
        AddCheck(fields, "显存", () => _cfg.ShowVram, v => _cfg.ShowVram = v);
        AddCheck(fields, "网速", () => _cfg.ShowNet, v => _cfg.ShowNet = v);
        AddCheck(fields, "GPU 温度", () => _cfg.ShowTemp, v => _cfg.ShowTemp = v);
        menu.Items.Add(fields);

        menu.Items.Add(new ToolStripSeparator());

        var settings = new ToolStripMenuItem("设置…", null, (_, _) => OpenSettings());
        menu.Items.Add(settings);

        var pause = new ToolStripMenuItem("暂停刷新", null, (_, _) => { }) { CheckOnClick = true };
        pause.Click += (_, _) => _paused = pause.Checked;
        menu.Items.Add(pause);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => Quit()));

        menu.Opening += (_, _) => SyncMenu(menu);
        return menu;
    }

    private void AddRadio(ToolStripMenuItem parent, string text, object value, Action<object?> apply)
    {
        var item = new ToolStripMenuItem(text) { Tag = (value, apply), CheckOnClick = false };
        item.Click += (_, _) =>
        {
            apply(value);
            _cfg.Save();
            RefreshAll();
        };
        parent.DropDownItems.Add(item);
    }

    private void AddCheck(ToolStripMenuItem parent, string text, Func<bool> get, Action<bool> set)
    {
        var item = new ToolStripMenuItem(text) { CheckOnClick = true, Tag = (get, set) };
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
                if (it.Tag is ValueTuple<object, Action<object?>> t)
                {
                    it.Checked = t.Item1 switch
                    {
                        StripStyle ss => _cfg.Strip == ss,
                        TrayStyle ts => _cfg.Tray == ts,
                        ThemeMode tm => _cfg.Theme == tm,
                        StripAnchor sa => _cfg.Anchor == sa,
                        _ => it.Checked
                    };
                }
                else if (it.Tag is ValueTuple<Func<bool>, Action<bool>> cb)
                {
                    it.Checked = cb.Item1();
                }
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
        if (b.Width <= 0) return;
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
