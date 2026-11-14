using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using WMonitor.Interop;

namespace WMonitor;

/// <summary>
/// UI 线程卡死看门狗（v0.30）。起因：v0.29 的 WM_PAINT 风暴事故（docs/05 §23）靠
/// 「线程 CPU 差分 → dump 抓栈 → GetUpdateRect 脏区」三件套人工定位 —— 本类把同一套
/// 诊断自动化：后台线程盯 UI 线程心跳（采样定时器，正是事故里被饿死的那个），
/// 停滞超阈值就把线程 CPU 快照 + 窗口脏区追加到 %APPDATA%\WMonitor\stall-report.log。
/// 事故再发生时现场数据已经落盘，不用临时装 dotnet-dump。
///
/// 纯诊断，不干预进程：永不杀进程、永不弹窗。看门狗自身所有异常全部吞掉（自我禁用）
/// —— 绝不能让保险丝比电器先烧。
/// </summary>
internal static class Watchdog
{
    private const int CheckMs = 5000;          // 巡检间隔
    // 心跳停滞阈值：默认 15s（1Hz 心跳连丢 15 拍）；WMONITOR_WATCHDOG_STALL=秒数 可覆盖（测试用）
    private static readonly double StallSeconds =
        double.TryParse(Environment.GetEnvironmentVariable("WMONITOR_WATCHDOG_STALL"),
            System.Globalization.CultureInfo.InvariantCulture, out double s) && s > 1 ? s : 15;
    private const double ReportEverySeconds = 60; // 持续卡死时每 60s 补一份快照

    private static long _beat;                 // UI 线程最近一次心跳（Stopwatch.GetTimestamp）
    private static System.Threading.Timer? _timer;
    private static readonly object Gate = new();
    private static List<IntPtr> _windows = new();   // 要查脏区的窗口句柄，随心跳刷新

    private static bool _inStall;
    private static DateTime _stallSince;
    private static DateTime _lastReport = DateTime.MinValue;
    private static DateTime _lastResume = DateTime.MinValue;
    private static Dictionary<int, double> _prevCpu = new();   // 上一份快照的线程 CPU（秒），算增量
    private static int _reporting;             // Interlocked 闸门，防快照重入

    private static int _uiTid;

    /// <summary>UI 线程调用（MonitorAppContext 构造）。uiThreadId 用于在快照里标出 UI 线程。</summary>
    public static void Start(int uiThreadId)
    {
        _uiTid = uiThreadId;
        _beat = Stopwatch.GetTimestamp();
        _timer = new System.Threading.Timer(_ => Check(), null, CheckMs, CheckMs);
        SystemEvents.PowerModeChanged += OnPowerModeChanged;   // 睡眠唤醒不算卡死
    }

    public static void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }

    /// <summary>
    /// UI 线程每个采样周期打一次心跳，顺带刷新要监视的窗口句柄
    /// （贴片 / 详情面板 / 主面板生命周期都在变，UI 线程来收集最安全）。
    /// </summary>
    public static void Beat(IEnumerable<IntPtr> windows)
    {
        lock (Gate)
        {
            _windows.Clear();
            _windows.AddRange(windows);
        }
        Volatile.Write(ref _beat, Stopwatch.GetTimestamp());

        if (Volatile.Read(ref _inStall)) Recovered();
    }

    private static void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            _lastResume = DateTime.Now;
            Volatile.Write(ref _beat, Stopwatch.GetTimestamp());   // 睡了多久都不算卡死
        }
    }

    private static void Check()
    {
        try
        {
            double age = (Stopwatch.GetTimestamp() - Volatile.Read(ref _beat)) / (double)Stopwatch.Frequency;
            if (age < StallSeconds) return;

            var now = DateTime.Now;
            lock (Gate)
            {
                if (!_inStall)
                {
                    _inStall = true;
                    _stallSince = now;
                    _lastReport = DateTime.MinValue;
                }
                if ((now - _lastReport).TotalSeconds < ReportEverySeconds) return;
                _lastReport = now;
            }

            if (Interlocked.CompareExchange(ref _reporting, 1, 0) != 0) return;
            try { WriteReport(age, now); }
            finally { Volatile.Write(ref _reporting, 0); }
        }
        catch { /* 看门狗绝不抛 */ }
    }

    private static void Recovered()
    {
        Volatile.Write(ref _inStall, false);
        Append($"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss}  心跳恢复，本次卡死持续 " +
               $"{(DateTime.Now - _stallSince).TotalSeconds:0.#}s ====");
    }

    /// <summary>把卡死现场写成自包含的文本快照：线程 CPU（含增量）+ 窗口脏区 + 运行时概览。</summary>
    private static void WriteReport(double ageSeconds, DateTime now)
    {
        var sb = new System.Text.StringBuilder(2048);
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        string ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

        sb.AppendLine($"==== {now:yyyy-MM-dd HH:mm:ss}  UI 心跳停滞 {ageSeconds:0.#}s" +
                      $"（自 {_stallSince:HH:mm:ss}，进程已运行 {(now - p.StartTime).TotalHours:0.#}h）====");
        sb.AppendLine($"pid={p.Id} ver={ver} ui-thread=TID {_uiTid}" +
                      (_lastResume == DateTime.MinValue ? "" : $"  最近唤醒={_lastResume:MM-dd HH:mm:ss}"));
        sb.AppendLine("线程 CPU（Δ=距上一份快照的增量，Δ 大的线程就是当前热点；只列有增量的线程）：");

        double uiDelta = 0;
        var rows = new List<(int Tid, bool IsUi, string State, double Total, double Delta)>();
        foreach (ProcessThread t in p.Threads)
        {
            double total;
            try { total = t.TotalProcessorTime.TotalSeconds; } catch { continue; }
            double delta = total - (_prevCpu.TryGetValue(t.Id, out double prev) ? prev : total);
            _prevCpu[t.Id] = total;
            string state;
            try
            {
                state = t.ThreadState.ToString() +
                        (t.ThreadState == System.Diagnostics.ThreadState.Wait ? "/" + t.WaitReason : "");
            }
            catch { state = "?"; }
            if (delta > 0.05 || t.Id == _uiTid)
                rows.Add((t.Id, t.Id == _uiTid, state, total, delta));
            if (t.Id == _uiTid) uiDelta = delta;
        }
        foreach (var r in rows.OrderByDescending(r => r.Delta).Take(12))
            sb.AppendLine($"  TID {r.Tid,-6}{(r.IsUi ? " [UI]" : "     ")} State={r.State,-24} " +
                          $"CPU {r.Total,8:0.#}s  Δ {r.Delta,6:0.#}s" + (r.IsUi ? "   ← UI 线程" : ""));
        sb.AppendLine($"UI 线程本次增量 {uiDelta:0.##}s" +
                      (uiDelta > ageSeconds * 0.8 ? " ← 基本满载一核，与 docs/05 §23 消息风暴特征一致" : " ← 空转（等输入）"));

        lock (Gate)
        {
            sb.AppendLine("窗口（分层窗口 healthy 时 dirty 应为 False；True 且数值冻结 = WM_PAINT 风暴特征）：");
            foreach (var h in _windows)
            {
                if (h == IntPtr.Zero || !Native.IsWindowVisible(h)) continue;
                Native.GetWindowRect(h, out Native.RECT r);
                bool dirty = GetUpdateRect(h, out Native.RECT u, false);
                sb.AppendLine($"  hwnd 0x{h.ToInt64():X}  rect=({r.Left},{r.Top},{r.Right - r.Left},{r.Bottom - r.Top})  " +
                              (dirty ? $"dirty=({u.Left},{u.Top},{u.Right - u.Left},{u.Bottom - u.Top})" : "dirty=False"));
            }
        }

        sb.AppendLine($"gc heap={GC.GetTotalMemory(false) / 1048576.0:0.#}MB  " +
                      $"tp-threads={ThreadPool.ThreadCount}  tp-pending={ThreadPool.PendingWorkItemCount}");
        Append(sb.ToString());
    }

    private static void Append(string text)
    {
        try
        {
            Directory.CreateDirectory(AppConfig.Dir);
            File.AppendAllText(Path.Combine(AppConfig.Dir, "stall-report.log"), text);
        }
        catch { }
    }

    // fErase=false 只读更新区域，不清除 —— BeginPaint/ValidateRect 之外的线程读脏区安全。
    [DllImport("user32.dll")]
    private static extern bool GetUpdateRect(IntPtr hWnd, out Native.RECT lpRect, bool fErase);
}
