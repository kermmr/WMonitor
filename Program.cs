using System.Diagnostics;
using System.Text;
using WMonitor.Metrics;
using WMonitor.UI;

namespace WMonitor;

internal static class Program
{
    private const string MutexName = @"Global\WMonitor.SingleInstance.v1";

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--probe"))
        {
            Probe();
            return;
        }

        if (args.Contains("--icons"))
        {
            IconSheet();
            return;
        }

        if (args.Contains("--monitors"))
        {
            Monitors();
            return;
        }

        if (args.Contains("--history-dump"))
        {
            HistoryDump();
            return;
        }

        if (args.Contains("--alert-test"))
        {
            AlertTest();
            return;
        }

        // 单实例：不能用 `new Mutex(true, name, out createdNew)` 判断 —— 那看的是"对象是否存在"，
        // 只要有一个残留句柄（别的会话、崩溃后没关掉的宿主、调试时用 OpenExisting 探过）
        // 就会永远启动不了，且静默退出（用户看到的是"双击没反应"）。
        // 正确做法是抢持有权：WaitOne(0) 拿到才算首实例；AbandonedMutex 说明上一个实例异常退出，直接接管。
        using var mutex = new Mutex(false, MutexName);
        bool owned;
        try
        {
            owned = mutex.WaitOne(TimeSpan.Zero, false);
        }
        catch (AbandonedMutexException)
        {
            owned = true;
        }
        if (!owned)
        {
            MessageBox.Show("WMonitor 已经在运行（看托盘区图标）。", "WMonitor",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Application.ThreadException += (_, e) => LogCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);

        var ctx = new MonitorAppContext();
        if (args.Contains("--panel"))
        {
            var tp = new System.Windows.Forms.Timer { Interval = 900 };
            tp.Tick += (_, _) => { tp.Stop(); tp.Dispose(); ctx.OpenPanelPublic(); };
            tp.Start();
        }
        if (args.Contains("--settings"))
        {
            // 延迟到消息循环启动后，否则 ShowDialog 没有循环可用
            var t = new System.Windows.Forms.Timer { Interval = 700 };
            t.Tick += (_, _) => { t.Stop(); t.Dispose(); ctx.OpenSettingsPublic(); };
            t.Start();
        }
        if (args.Contains("--menu"))
        {
            // 诊断用：在屏幕右下角把右键菜单摆出来（截图验证自绘菜单，不必模拟鼠标）
            var m = new System.Windows.Forms.Timer { Interval = 900 };
            m.Tick += (_, _) =>
            {
                m.Stop(); m.Dispose();
                var wa = Screen.PrimaryScreen!.WorkingArea;
                int sub = int.TryParse(Environment.GetEnvironmentVariable("WMONITOR_MENU_SUB"), out int v) ? v : -1;
                ctx.ShowMenuPublic(new Point(wa.Right - 40, wa.Bottom - 20), sub);
            };
            m.Start();
        }
        Application.Run(ctx);
    }

    private static void LogCrash(Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(AppConfig.Dir);
            File.AppendAllText(Path.Combine(AppConfig.Dir, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\r\n");
        }
        catch { }
    }

    /// <summary>诊断用：枚举全部任务栏与各自 DPI，验证多屏识别（副屏贴片的前提）。</summary>
    private static void Monitors()
    {
        var all = TaskbarLocator.Enumerate();
        Directory.CreateDirectory(@"D:/Hermes/output/results");
        File.WriteAllText(@"D:/Hermes/output/results/wmonitor-monitors.txt",
            $"共 {all.Count} 块屏\r\n{TaskbarLocator.Describe(all)}\r\n");
    }

    /// <summary>
    /// 诊断用：离线跑告警状态机（不碰 UI）。伪造 CPU 曲线验证 武装→触发→解除→再触发，
    /// 以及阈值抖动不重复触发。等真实温度触发要跑游戏，这个 50 步模拟 0.1 秒出结果。
    /// </summary>
    private static void AlertTest()
    {
        var sb = new StringBuilder();
        var cfg = new AppConfig { AlertsEnabled = true };
        foreach (var r in cfg.AlertRules) r.Enabled = false;

        var cpu = cfg.AlertRules[(int)AlertMetric.Cpu];
        cpu.Enabled = true;
        cpu.Threshold = 90;
        cpu.DurationSec = 5;
        cpu.CooldownSec = 10;

        var eng = new AlertEngine();
        eng.Configure(cfg);

        long t0 = Stopwatch.GetTimestamp();
        var fired = new List<int>();

        for (int t = 0; t <= 50; t++)
        {
            double v = t switch
            {
                <= 10 => 95,          // 越线，持续 5 秒后触发（t=5）
                <= 19 => 80,          // 落到恢复线（85）以下 → 解除
                <= 30 => 95,          // 再次越线 → t=25 再触发（冷却已过）
                _ => (t % 2 == 0) ? 89 : 91,   // 阈值抖动 → 不应再触发
            };

            var view = eng.Evaluate(new MetricsSnapshot { CpuPercent = v }, t0 + (long)t * Stopwatch.Frequency);
            foreach (var e in view.Events)
            {
                fired.Add(t);
                sb.AppendLine($"t={t,2}s FIRE {e.Metric} {e.Value:0} >= {e.Threshold:0}");
            }
            if (t % 5 == 0) sb.AppendLine($"t={t,2}s v={v,3:0} firing={view.IsFiring(AlertMetric.Cpu)}");
        }

        string got = string.Join(",", fired);
        sb.AppendLine($"events at: {got}");
        sb.AppendLine(got == "5,25" ? "PASS" : "FAIL (expected 5,25)");

        // 静音：事件被压住，但 Firing 状态照常维护（贴片仍要红）
        eng.Reset();
        eng.Muted = true;
        bool anyEvent = false, stillFiring = false;
        for (int t = 0; t <= 20; t++)
        {
            var v = eng.Evaluate(new MetricsSnapshot { CpuPercent = 95 }, t0 + (long)(100 + t) * Stopwatch.Frequency);
            if (v.Events.Count > 0) anyEvent = true;
            stillFiring = v.IsFiring(AlertMetric.Cpu);
        }
        sb.AppendLine($"mute: events={(anyEvent ? "有(FAIL)" : "无(PASS)")} firing={(stillFiring ? "仍红(PASS)" : "灭(FAIL)")}");

        // 总开关关闭：完全不参与
        cfg.AlertsEnabled = false;
        var off = new AlertEngine();
        off.Configure(cfg);
        bool offFired = false;
        for (int t = 0; t <= 20; t++)
            if (off.Evaluate(new MetricsSnapshot { CpuPercent = 99 }, t0 + (long)(200 + t) * Stopwatch.Frequency).Events.Count > 0)
                offFired = true;
        sb.AppendLine($"disabled: events={(offFired ? "有(FAIL)" : "无(PASS)")}");

        Directory.CreateDirectory(@"D:/Hermes/output/results");
        File.WriteAllText(@"D:/Hermes/output/results/wmonitor-alert-test.txt", sb.ToString());
    }

    /// <summary>诊断用：打印历史存储状态与最近 1 分钟的抽稀结果。</summary>
    private static void HistoryDump()
    {
        var cfg = AppConfig.Load();
        using var h = new HistoryStore(cfg.HistoryEnabled, cfg.HistoryPersist, cfg.HistoryRetentionHours);

        var sb = new StringBuilder();
        sb.AppendLine($"dir={HistoryStore.Dir} exists={Directory.Exists(HistoryStore.Dir)}");
        if (Directory.Exists(HistoryStore.Dir))
            foreach (string f in Directory.GetFiles(HistoryStore.Dir))
                sb.AppendLine($"  file {Path.GetFileName(f)} {new FileInfo(f).Length}B");
        sb.AppendLine($"enabled={h.Enabled} persist={h.CanPersist} retention={cfg.HistoryRetentionHours}h window={cfg.ChartWindowMinutes}m");
        sb.AppendLine($"ring count={h.Count} oldest={h.OldestTs} todayFile={h.FileBytes()} bytes");
        foreach (HistoryMetric m in Enum.GetValues<HistoryMetric>())
        {
            var s = h.Query(m, TimeSpan.FromMinutes(1), 10);
            sb.AppendLine($"{m,-5} 1m : " + string.Join(", ", s.Values.Select(v => v.ToString("0.0"))));
            var d24 = h.Query(m, TimeSpan.FromHours(24), 12);
            sb.AppendLine($"{m,-5} 24h: " + string.Join(", ", d24.Values.Select(v => v.ToString("0.0"))));
        }

        Directory.CreateDirectory(@"D:/Hermes/output/results");
        File.WriteAllText(@"D:/Hermes/output/results/wmonitor-history.txt", sb.ToString());
    }

    /// <summary>诊断用：采样 8 次写文件，验证各计数器读数是否合理。</summary>
    private static void Probe()
    {
        var sb = new StringBuilder();
        using var sampler = new MetricSampler();
        for (int i = 0; i < 8; i++)
        {
            var s = sampler.Sample();
            sb.AppendLine(
                $"t={i} cpu={s.CpuPercent:F1}% {s.CpuGhz:F2}GHz | " +
                $"ram={s.RamPercent:F1}% {s.RamUsedGb:F1}/{s.RamTotalGb:F1}GB | " +
                $"gpu={s.GpuPercent:F1}% | vram={s.VramGb:F2}/{s.VramTotalGb:F2}GB | " +
                $"temp={(double.IsNaN(s.GpuTempC) ? "n/a" : s.GpuTempC.ToString("F0") + "C")} | " +
                $"power={(double.IsNaN(s.GpuPowerW) ? "n/a" : s.GpuPowerW.ToString("F0") + "W")} | " +
                $"net={s.NetDownBps / 1024:F0}/{s.NetUpBps / 1024:F0} KB/s | " +
                $"disk={s.DiskReadBps / 1024:F0}/{s.DiskWriteBps / 1024:F0} KB/s | {s.GpuName}");
            Thread.Sleep(1000);
        }
        Directory.CreateDirectory(@"D:/Hermes/output/results");
        File.WriteAllText(@"D:/Hermes/output/results/sysmon-probe.txt", sb.ToString());
    }
    /// <summary>
    /// 诊断用：把 4 种托盘图标渲染成对比图（真实 16px + 8 倍放大），
    /// 用来判断小尺寸下数字是否看得清。输出 wmonitor-icons.png。
    /// </summary>
    private static void IconSheet()
    {
        var normal = new MetricsSnapshot
        {
            CpuPercent = 54, GpuPercent = 17, NetDownBps = 3.4 * 1024 * 1024, GpuTempC = 62,
            RamTotalBytes = 64UL << 30, RamUsedBytes = (ulong)(39.0 * (1UL << 30)),
            VramTotalBytes = 12UL << 30, VramUsedBytes = (ulong)(5.0 * (1UL << 30)),
            HasGpu = true, HasVramTotal = true, HasTemp = true
        };
        var extreme = new MetricsSnapshot
        {
            CpuPercent = 100, GpuPercent = 9, NetDownBps = 12.7 * 1024 * 1024, GpuTempC = 87,
            RamTotalBytes = 64UL << 30, RamUsedBytes = (ulong)(5.1 * (1UL << 30)),
            VramTotalBytes = 12UL << 30, VramUsedBytes = 12UL << 30,
            HasGpu = true, HasVramTotal = true, HasTemp = true
        };
        var styles = new[] { TrayStyle.DualNumber, TrayStyle.DualRing, TrayStyle.FourBar, TrayStyle.Single };
        const int zoom = 8, icon = 16, cellW = 460, cellH = 170;

        using var sheet = new Bitmap(cellW * 2 + 20, cellH * styles.Length + 20);
        using (var g = Graphics.FromImage(sheet))
        {
            g.Clear(Color.FromArgb(0x2E, 0x2E, 0x2E));
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            using var label = new Font("Segoe UI", 10f);
            using var white = new SolidBrush(Color.White);

            for (int i = 0; i < styles.Length; i++)
            {
                var cfg = new AppConfig { Tray = styles[i] };
                int y = 20 + i * cellH;
                g.DrawString(styles[i].ToString(), label, white, 20, y);
                int row = y + 22;

                for (int k = 0; k < 2; k++)
                {
                    var s = k == 0 ? normal : extreme;
                    int x = 20 + k * (cellW / 2);
                    using var dark = TrayIconRenderer.Render(cfg, s, Theme.Dark, icon);
                    using var light = TrayIconRenderer.Render(cfg, s, Theme.Light, icon);
                    using var bmp = dark.ToBitmap();

                    using (var bg = new SolidBrush(Color.FromArgb(0x20, 0x20, 0x20)))
                        g.FillRectangle(bg, x, row, 34, 34);
                    g.DrawIcon(dark, new Rectangle(x + 9, row + 9, icon, icon));

                    using (var bg2 = new SolidBrush(Color.FromArgb(0xF3, 0xF3, 0xF3)))
                        g.FillRectangle(bg2, x + 40, row, 34, 34);
                    g.DrawIcon(light, new Rectangle(x + 49, row + 9, icon, icon));

                    var prev = g.InterpolationMode;
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                    g.DrawImage(bmp, new Rectangle(x + 84, row, icon * zoom, icon * zoom));
                    g.InterpolationMode = prev;
                }
            }
        }

        Directory.CreateDirectory(@"D:/Hermes/output/results");
        sheet.Save(@"D:/Hermes/output/results/wmonitor-icons.png");
    }
}
