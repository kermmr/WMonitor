using System.Text;
using WMonitor.Metrics;

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
        Directory.CreateDirectory(@"D:\Hermes\output\results");
        File.WriteAllText(@"D:\Hermes\output\results\sysmon-probe.txt", sb.ToString());
    }
}
