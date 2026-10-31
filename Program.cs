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

        using var mutex = new Mutex(true, MutexName, out bool first);
        if (!first) return;

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
                $"net={s.NetDownBps / 1024:F0}/{s.NetUpBps / 1024:F0} KB/s | {s.GpuName}");
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
