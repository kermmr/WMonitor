using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using WMonitor.Metrics;
using WMonitor.UI;

namespace WMonitor;

internal sealed class MonitorAppContext : ApplicationContext
{
    private readonly AppConfig _cfg;
    private readonly MetricSampler _sampler;
    private readonly DetailPanel _panel;
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _timer;

    /// <summary>历史存储。总开关关闭时为 null（零开销，连目录都不建）。</summary>
    private HistoryStore? _hist;

    /// <summary>建 <see cref="_hist"/> 时用的配置三元组，设置窗口关闭后据此判断要不要重建。</summary>
    private (bool On, bool Persist, int Hours) _histKey = (false, false, 0);

    private readonly AlertEngine _alerts = new();
    private readonly System.Windows.Forms.Timer _flashTimer;
    private Icon? _flashNormal, _flashAlert;
    private bool _flashing, _flashOn;
    private int _flashTicks;
    private DateTime _muteUntil = DateTime.MinValue;

    /// <summary>当前活着的贴片（主屏 1 片，或「全部屏幕」时每屏 1 片）。</summary>
    private List<TaskbarStrip> _strips = new();

    /// <summary>当前枚举到的全部任务栏（主屏在前）。</summary>
    private List<TaskbarInfo> _bars = new();

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

        // 面板先建：贴片的 Clicked 回调要引用它
        _panel = new DetailPanel();
        _panel.SettingsClicked += OpenSettings;

        _hist = NewHistory();
        _panel.History = _hist;

        _bars = TaskbarLocator.Enumerate();
        RebuildStrips();

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

        _flashTimer = new System.Windows.Forms.Timer { Interval = 400 };
        _flashTimer.Tick += (_, _) => FlashTick();

        _alerts.Configure(_cfg);

        _timer = new System.Windows.Forms.Timer { Interval = Math.Clamp(_cfg.RefreshMs, 250, 10000) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
    }

    private static int TraySize => Math.Max(16, SystemInformation.SmallIconSize.Width);

    // ---------------- 贴片生命周期 ----------------

    /// <summary>
    /// 按配置决定要哪几片贴片：单屏（<see cref="AppConfig.StripMonitor"/>）或全部屏幕。
    /// 同设备的实例复用，避免改配置时反复创建/销毁窗口。
    /// </summary>
    private void RebuildStrips()
    {
        if (_bars.Count == 0) return;

        var want = new List<TaskbarInfo>(4);
        if (_cfg.ShowOnAllMonitors) want.AddRange(_bars);
        else want.Add(TaskbarLocator.Find(_bars, _cfg.StripMonitor) ?? _bars[0]);

        var next = new List<TaskbarStrip>(want.Count);
        foreach (var bar in want)
        {
            TaskbarStrip? st = _strips.FirstOrDefault(s => s.Device == bar.Device);
            if (st == null)
            {
                st = new TaskbarStrip { Device = bar.Device };
                var self = st;                                   // 闭包捕获，别捕循环变量
                self.Clicked += (_, _) => OpenPanel(self);
                self.Moved += pt => OnStripMoved(self, pt);
            }
            st.Bar = bar;
            st.DpiScale = DpiOverride() ?? bar.DpiScale;
            next.Add(st);
        }

        foreach (var old in _strips)
            if (!next.Contains(old)) old.Dispose();

        _strips = next;
    }

    /// <summary>调试钩子：WMONITOR_DPI_SCALE=1.5 可在这台 96 DPI 机器上预览 150% 的排版。</summary>
    private static float? DpiOverride()
    {
        string? s = Environment.GetEnvironmentVariable("WMONITOR_DPI_SCALE");
        if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && v > 0)
            return v;
        return null;
    }

    /// <summary>显示器拓扑变了（Explorer 重启 / 插拔屏）才重建，30 秒查一次。</summary>
    private bool MonitorsChanged()
    {
        var now = TaskbarLocator.Enumerate();
        if (now.Count != _bars.Count) return true;
        for (int i = 0; i < now.Count; i++)
            if (now[i].Hwnd != _bars[i].Hwnd || now[i].Device != _bars[i].Device) return true;
        return false;
    }

    private void OnStripMoved(TaskbarStrip src, Point pt)
    {
        // 多贴片模式下只有一个 FreeX/FreeY，不做持久化（见 docs/08 §3.5）
        if (_cfg.ShowOnAllMonitors) return;
        _cfg.Anchor = StripAnchor.Free;
        _cfg.FreeX = pt.X;
        _cfg.FreeY = pt.Y;
        _cfg.Save();
    }

    /// <summary>按配置新建历史存储；总开关关闭时返回 null。</summary>
    private HistoryStore? NewHistory()
    {
        _histKey = (_cfg.HistoryEnabled, _cfg.HistoryPersist, _cfg.HistoryRetentionHours);
        return _cfg.HistoryEnabled
            ? new HistoryStore(true, _cfg.HistoryPersist, _cfg.HistoryRetentionHours)
            : null;
    }

    /// <summary>设置里改了历史三项之一才重建，避免无谓地丢掉内存环。</summary>
    private void RefreshHistoryIfChanged()
    {
        var want = (_cfg.HistoryEnabled, _cfg.HistoryPersist, _cfg.HistoryRetentionHours);
        if (want == _histKey) return;
        _hist?.Dispose();
        _hist = NewHistory();
        _panel.History = _hist;
    }

    // ---------------- 告警动作 ----------------

    private void DispatchAlert(AlertView alert)
    {
        foreach (var e in alert.Events)
        {
            LogAlert($"fired {e.Metric} value={e.Value:0.#} threshold={e.Threshold:0.#}");

            if (_cfg.AlertToast)
                _tray.ShowBalloonTip(5000, "WMonitor 告警",
                    $"{AlertEngine.Name(e.Metric)} {e.Value:0.#}{AlertEngine.Unit(e.Metric)} " +
                    $"超过 {e.Threshold:0.#}{AlertEngine.Unit(e.Metric)}",
                    ToolTipIcon.Warning);

            if (_cfg.AlertScript) RunAlertScript(e);
        }

        if (_cfg.AlertTrayFlash) StartFlash();
    }

    private void StartFlash()
    {
        if (_flashing) return;

        _flashNormal = TrayIconRenderer.Render(_cfg, _last, _theme, TraySize);
        _flashAlert = TrayIconRenderer.Render(_cfg, _last, _theme, TraySize, alert: true);
        _flashOn = true;
        _flashTicks = 0;
        _flashing = true;
        _tray.Icon = _flashAlert;
        _flashTimer.Start();
    }

    private void FlashTick()
    {
        _flashOn = !_flashOn;
        _tray.Icon = _flashOn ? _flashAlert : _flashNormal;

        if (++_flashTicks < 8) return;

        _flashTimer.Stop();
        _flashing = false;
        _trayKey = "";
        Apply(_last);                    // 重建正常图标（Apply 会 Dispose 当前那个）

        var a = _flashAlert;
        var n = _flashNormal;
        _flashAlert = _flashNormal = null;
        if (a != null && !ReferenceEquals(a, _tray.Icon)) a.Dispose();
        if (n != null && !ReferenceEquals(n, _tray.Icon)) n.Dispose();
    }

    private void StopFlash()
    {
        _flashTimer.Stop();
        _flashing = false;
        var a = _flashAlert;
        var n = _flashNormal;
        _flashAlert = _flashNormal = null;
        if (a != null && !ReferenceEquals(a, _tray.Icon)) a.Dispose();
        if (n != null && !ReferenceEquals(n, _tray.Icon)) n.Dispose();
    }

    /// <summary>
    /// 只执行用户在设置里显式选中的文件；参数走环境变量，不拼命令行。
    /// fire-and-forget：UI 线程绝不能等脚本（30 秒超时会卡死贴片刷新）。
    /// </summary>
    private void RunAlertScript(AlertEvent e)
    {
        string? path = _cfg.AlertScriptPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(path);
            psi.Environment["WMONITOR_METRIC"] = e.Metric.ToString();
            psi.Environment["WMONITOR_VALUE"] = e.Value.ToString("0.#", CultureInfo.InvariantCulture);
            psi.Environment["WMONITOR_THRESHOLD"] = e.Threshold.ToString("0.#", CultureInfo.InvariantCulture);
            Process.Start(psi);
            LogAlert($"script started: {Path.GetFileName(path)}");
        }
        catch (Exception ex) { LogAlert($"script failed: {ex.Message}"); }
    }

    private static void LogAlert(string msg)
    {
        try
        {
            Directory.CreateDirectory(AppConfig.Dir);
            File.AppendAllText(Path.Combine(AppConfig.Dir, "alert.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\r\n");
        }
        catch { }
    }

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

        long now = Stopwatch.GetTimestamp();

        _alerts.Muted = DateTime.Now < _muteUntil;
        var alert = _alerts.Evaluate(s, now);

        foreach (var st in _strips) st.Update(s, _cfg, _theme, alert);

        _hist?.Push(s, now);
        if (alert.Events.Count > 0) DispatchAlert(alert);

        _panel.PushHistory(s);
        if (_panel.Visible) _panel.Update(s, _cfg, _theme);

        // 30 秒复查一次显示器拓扑，Explorer 重启后能自动重挂
        if (_tick % 30 == 0 && MonitorsChanged())
        {
            _bars = TaskbarLocator.Enumerate();
            RebuildStrips();
        }

        // 闪烁期间由 FlashTick 独占托盘图标，否则会和 key 逻辑打架
        if (!_flashing)
        {
            string key = TrayIconRenderer.Key(_cfg, s, _theme);
            if (key != _trayKey)
            {
                _trayKey = key;
                var old = _tray.Icon;
                _tray.Icon = TrayIconRenderer.Render(_cfg, s, _theme, TraySize);
                old?.Dispose();
            }
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

        // 只有一块屏时整个子菜单没意义
        if (_bars.Count > 1)
        {
            var screen = new ToolStripMenuItem("贴片所在屏幕");
            AddRadio(screen, "全部屏幕", () => _cfg.ShowOnAllMonitors,
                () => { _cfg.ShowOnAllMonitors = true; });
            screen.DropDownItems.Add(new ToolStripSeparator());
            for (int i = 0; i < _bars.Count; i++)
            {
                var bar = _bars[i];
                AddRadio(screen, $"屏幕 {i + 1}（{bar.Rect.Width}×{bar.Rect.Height}）",
                    () => !_cfg.ShowOnAllMonitors && _cfg.StripMonitor == bar.Device,
                    () => { _cfg.ShowOnAllMonitors = false; _cfg.StripMonitor = bar.Device; });
            }
            menu.Items.Add(screen);
        }

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
        AddCheck(fields, "磁盘 IO", () => _cfg.ShowDisk, v => _cfg.ShowDisk = v);
        menu.Items.Add(fields);

        var alertMenu = new ToolStripMenuItem("告警");
        AddCheck(alertMenu, "启用告警", () => _cfg.AlertsEnabled, v => _cfg.AlertsEnabled = v);
        AddCheck(alertMenu, "静音 1 小时", () => DateTime.Now < _muteUntil,
            v => _muteUntil = v ? DateTime.Now.AddHours(1) : DateTime.MinValue);
        alertMenu.DropDownItems.Add(new ToolStripSeparator());
        alertMenu.DropDownItems.Add(new ToolStripMenuItem("告警设置…", null, (_, _) => OpenSettings()));
        menu.Items.Add(alertMenu);

        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(new ToolStripMenuItem("详情面板…", null, (_, _) => OpenPanel(null)));
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
        _bars = TaskbarLocator.Enumerate();
        RebuildStrips();
        foreach (var st in _strips) st.Reanchor(_cfg);
        _alerts.Configure(_cfg);
        Apply(_last);
    }

    private void OpenPanel(TaskbarStrip? src)
    {
        if (_panel.Visible) { _panel.Hide(); return; }

        var b = (src ?? _strips.FirstOrDefault())?.StripBounds ?? Rectangle.Empty;
        // 贴片被隐藏（或还没画出来）时没有锚点，退到鼠标位置
        if (!_cfg.ShowStrip || b.Width <= 0)
            b = new Rectangle(Cursor.Position, new Size(1, 1));

        _panel.OpenNear(b, _last, _cfg, _theme);
    }

    public void OpenSettingsPublic() => OpenSettings();
    public void OpenPanelPublic() => OpenPanel(null);

    private void OpenSettings()
    {
        using var f = new SettingsForm(_cfg, _theme, _bars);
        f.Changed += () =>
        {
            _theme = Theme.Resolve(_cfg.Theme);
            _trayKey = "";
            _timer.Interval = Math.Clamp(_cfg.RefreshMs, 250, 10000);
            RefreshHistoryIfChanged();      // 开关历史要立刻生效，不等关窗
            _alerts.Configure(_cfg);
            Apply(_last);
        };
        f.ShowDialog();
        _cfg.Save();
        _theme = Theme.Resolve(_cfg.Theme);
        _trayKey = "";
        RefreshHistoryIfChanged();
        _alerts.Configure(_cfg);
        _alerts.Reset();          // 阈值可能变了，旧的武装计时不能沿用
        RefreshAll();
    }

    private void Quit()
    {
        _timer.Stop();
        _timer.Dispose();
        StopFlash();
        _tray.Visible = false;
        _tray.Icon = null;
        _tray.Dispose();
        foreach (var st in _strips) st.Dispose();
        _strips.Clear();
        _panel.Dispose();
        _hist?.Dispose();
        _sampler.Dispose();
        _cfg.Save();
        ExitThread();
    }
}
