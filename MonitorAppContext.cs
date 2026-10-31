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

    /// <summary>当前打开的托盘右键菜单（每次重开重建，勾选状态直接读配置）。</summary>
    private FlyoutMenu? _menu;

    /// <summary>左键点击去抖：双击托盘图标只算一次，别开一下又关掉。</summary>
    private DateTime _lastTrayClick = DateTime.MinValue;

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
            Text = "WMonitor"
        };
        // 左键 = 开/关详情面板；右键 = 自绘弹出菜单（不用 ContextMenuStrip，见 UI/FlyoutMenu.cs）
        _tray.MouseUp += OnTrayMouseUp;
        _tray.Icon = TrayIconRenderer.Render(_cfg, _last, _theme, TraySize);

        if (AppConfig.LastLoadError != null)
            _tray.ShowBalloonTip(8000, "WMonitor",
                "配置文件读取失败，本次使用默认设置；原文件已备份为 config.json.bad（详见 error.log）。",
                ToolTipIcon.Warning);

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

    // ---------------- 托盘交互 ----------------

    private void OnTrayMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            // 双击时 MouseUp 会来两次，去抖后只算一次点击
            var now = DateTime.UtcNow;
            if ((now - _lastTrayClick).TotalMilliseconds < 350) return;
            _lastTrayClick = now;

            _menu?.CloseAll();
            OpenPanel(null, atCursor: true);
            return;
        }

        if (e.Button == MouseButtons.Right) ShowTrayMenu();
    }

    /// <summary>右键：重建菜单模型后在光标处弹出（和系统菜单一样从托盘往上长）。</summary>
    private void ShowTrayMenu()
    {
        _menu?.Dispose();
        var m = new FlyoutMenu { Th = _theme, Build = BuildMenuRows };
        _menu = m;
        m.ShowMain(Cursor.Position);
    }

    /// <summary>
    /// 菜单模型：和原来的 ContextMenuStrip 一样的选项与分组，只是换成自绘样式。
    /// 每次打开/开关项变化时重建，勾选状态直接读配置，不做同步逻辑。
    /// </summary>
    private List<MenuRow> BuildMenuRows()
    {
        var list = new List<MenuRow>(24);

        // ---- 贴片位置 ----
        list.Add(new MenuRow
        {
            Kind = MenuRowKind.Submenu,
            Text = "贴片位置",
            Children = Radios(new[]
            {
                ("任务栏最左", StripAnchor.TaskbarLeft),
                ("应用图标左边", StripAnchor.BeforeApps),
                ("应用图标右边", StripAnchor.AfterApps),
                ("托盘箭头左边", StripAnchor.BeforeTray),
                ("自定义（拖动贴片）", StripAnchor.Free)
            }, () => _cfg.Anchor, v => _cfg.Anchor = v)
        });

        // ---- 贴片所在屏幕（只有多屏才有意义）----
        if (_bars.Count > 1)
        {
            var screens = new List<MenuRow>
            {
                new()
                {
                    Kind = MenuRowKind.Radio,
                    Text = "全部屏幕",
                    Checked = _cfg.ShowOnAllMonitors,
                    Click = () => { _cfg.ShowOnAllMonitors = true; Persist(); }
                },
                MenuRow.Sep()
            };
            for (int i = 0; i < _bars.Count; i++)
            {
                var bar = _bars[i];
                screens.Add(new MenuRow
                {
                    Kind = MenuRowKind.Radio,
                    Text = $"屏幕 {i + 1}（{bar.Rect.Width}×{bar.Rect.Height}）",
                    Checked = !_cfg.ShowOnAllMonitors && _cfg.StripMonitor == bar.Device,
                    Click = () => { _cfg.ShowOnAllMonitors = false; _cfg.StripMonitor = bar.Device; Persist(); }
                });
            }
            list.Add(new MenuRow { Kind = MenuRowKind.Submenu, Text = "贴片所在屏幕", Children = screens });
        }

        // ---- 贴片风格 ----
        list.Add(new MenuRow
        {
            Kind = MenuRowKind.Submenu,
            Text = "贴片风格",
            Children = Radios(new[]
            {
                ("数字条", StripStyle.Numbers),
                ("数字 + 迷你柱", StripStyle.Bars),
                ("四环仪表", StripStyle.Rings)
            }, () => _cfg.Strip, v => _cfg.Strip = v)
        });

        // ---- 托盘图标：样式 + 主 / 次指标 ----
        var trayChildren = Radios(new[]
        {
            ("双数字", TrayStyle.DualNumber),
            ("双环", TrayStyle.DualRing),
            ("四段条", TrayStyle.FourBar),
            ("单指标", TrayStyle.Single)
        }, () => _cfg.Tray, v => _cfg.Tray = v);

        trayChildren.Add(MenuRow.Sep());
        trayChildren.Add(MetricRow("主指标", () => _cfg.TrayCurrent.Primary, v => _cfg.TrayCurrent.Primary = v));
        trayChildren.Add(MetricRow("次指标", () => _cfg.TrayCurrent.Secondary, v => _cfg.TrayCurrent.Secondary = v));

        list.Add(new MenuRow { Kind = MenuRowKind.Submenu, Text = "托盘图标", Children = trayChildren });

        // ---- 主题 ----
        list.Add(new MenuRow
        {
            Kind = MenuRowKind.Submenu,
            Text = "主题",
            Children = Radios(new[]
            {
                ("深色", ThemeMode.Dark),
                ("浅色", ThemeMode.Light),
                ("跟随系统", ThemeMode.System)
            }, () => _cfg.Theme, SetTheme)
        });

        list.Add(MenuRow.Sep());

        // ---- 显示项 ----
        list.Add(new MenuRow
        {
            Kind = MenuRowKind.Submenu,
            Text = "显示项",
            Children = new List<MenuRow>
            {
                Toggle("任务栏贴片", () => _cfg.ShowStrip, v => _cfg.ShowStrip = v),
                MenuRow.Sep(),
                Toggle("CPU", () => _cfg.ShowCpu, v => _cfg.ShowCpu = v),
                Toggle("内存", () => _cfg.ShowRam, v => _cfg.ShowRam = v),
                Toggle("GPU", () => _cfg.ShowGpu, v => _cfg.ShowGpu = v),
                Toggle("显存", () => _cfg.ShowVram, v => _cfg.ShowVram = v),
                Toggle("网速", () => _cfg.ShowNet, v => _cfg.ShowNet = v),
                Toggle("GPU 温度", () => _cfg.ShowTemp, v => _cfg.ShowTemp = v),
                Toggle("磁盘 IO", () => _cfg.ShowDisk, v => _cfg.ShowDisk = v)
            }
        });

        // ---- 告警 ----
        list.Add(new MenuRow
        {
            Kind = MenuRowKind.Submenu,
            Text = "告警",
            Children = new List<MenuRow>
            {
                Toggle("启用告警", () => _cfg.AlertsEnabled, v => _cfg.AlertsEnabled = v),
                Toggle("静音 1 小时", () => DateTime.Now < _muteUntil,
                    v => _muteUntil = v ? DateTime.Now.AddHours(1) : DateTime.MinValue),
                MenuRow.Sep(),
                new MenuRow { Text = "告警设置…", Click = OpenSettings }
            }
        });

        list.Add(MenuRow.Sep());

        list.Add(new MenuRow { Text = "详情面板…", Click = () => OpenPanel(null) });
        list.Add(new MenuRow { Text = "设置…", Click = OpenSettings });
        list.Add(Toggle("暂停刷新", () => _paused, v => _paused = v));

        list.Add(MenuRow.Sep());
        list.Add(new MenuRow { Text = "退出", Danger = true, Click = Quit });

        return list;
    }

    /// <summary>一组单选项；值可以是枚举、字符串（设备名）等。</summary>
    private List<MenuRow> Radios<T>((string Text, T Value)[] items, Func<T> get, Action<T> set)
    {
        var cur = get();
        var rows = new List<MenuRow>(items.Length);
        foreach (var (text, value) in items)
        {
            var v = value;
            rows.Add(new MenuRow
            {
                Kind = MenuRowKind.Radio,
                Text = text,
                Checked = EqualityComparer<T>.Default.Equals(cur, v),
                Click = () => { set(v); Persist(); }
            });
        }
        return rows;
    }

    /// <summary>托盘图标能显示的指标（和 v0.15 的菜单一致，不含磁盘）。</summary>
    private static readonly (string Text, TrayMetric Metric)[] MetricMenu =
    {
        ("CPU", TrayMetric.Cpu),
        ("内存", TrayMetric.Ram),
        ("GPU", TrayMetric.Gpu),
        ("显存", TrayMetric.Vram),
        ("网速", TrayMetric.Net),
        ("GPU 温度", TrayMetric.Temp)
    };

    private MenuRow MetricRow(string text, Func<TrayMetric> get, Action<TrayMetric> set) => new()
    {
        Kind = MenuRowKind.Submenu,
        Text = text,
        Children = Radios(MetricMenu.Select(m => (m.Text, m.Metric)).ToArray(), get, set)
    };

    private MenuRow Toggle(string text, Func<bool> get, Action<bool> set) => new()
    {
        Kind = MenuRowKind.Toggle,
        Text = text,
        Checked = get(),
        Set = v => { set(v); Persist(); }
    };

    /// <summary>菜单改完统一落盘 + 立刻生效。</summary>
    private void Persist()
    {
        _cfg.Save();
        RefreshAll();
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

    private void OpenPanel(TaskbarStrip? src, bool atCursor = false)
    {
        if (_panel.Visible) { _panel.Hide(); return; }

        Rectangle b;
        if (atCursor)
        {
            // 从托盘图标打开：以光标为锚点（贴片可能被隐藏 / 在别的屏上）
            b = new Rectangle(Cursor.Position, new Size(1, 1));
        }
        else
        {
            var st = src ?? _strips.FirstOrDefault(s => s.Visible) ?? _strips.FirstOrDefault();
            b = st?.StripBounds ?? Rectangle.Empty;
            // 贴片被隐藏（或还没画出来）时没有锚点，退到鼠标位置
            if (!_cfg.ShowStrip || b.Width <= 0)
                b = new Rectangle(Cursor.Position, new Size(1, 1));
        }

        // 从托盘图标打开时给 900ms 失焦保护：点托盘本身会把焦点还给任务栏 / 溢出面板
        _panel.OpenNear(b, _last, _cfg, _theme, atCursor ? 900 : 350);
    }

    public void OpenSettingsPublic() => OpenSettings();
    public void OpenPanelPublic() => OpenPanel(null);

    /// <summary>诊断钩子（--menu）：在指定屏幕坐标弹出右键菜单；subIndex ≥ 0 时顺带展开该行子菜单。</summary>
    public void ShowMenuPublic(Point at, int subIndex = -1)
    {
        _menu?.Dispose();
        var m = new FlyoutMenu { Th = _theme, Build = BuildMenuRows };
        _menu = m;
        m.ShowMain(at);
        if (subIndex >= 0) m.DebugOpenSubmenu(subIndex);
    }

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
        _menu?.Dispose();
        _menu = null;
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
