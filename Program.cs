using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WMonitor.Interop;
using WMonitor.Metrics;
using WMonitor.UI;

namespace WMonitor;

internal static class Program
{
    private const string MutexName = @"Global\WMonitor.SingleInstance.v1";

    /// <summary>第二实例唤醒事件:已运行的实例收到信号后打开资源面板（无弹窗）。</summary>
    public const string ActivateEventName = @"Local\WMonitor.ShowDashboard.v1";

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

        if (args.Contains("--dashboard-probe"))
        {
            DashboardProbe();
            return;
        }

        if (args.Contains("--dashboard-gpu-probe"))
        {
            DashboardGpuProbe();
            return;
        }

        if (args.Contains("--dashboard-icon-probe"))
        {
            DashboardIconProbe();
            return;
        }

        if (args.Contains("--dashboard-action-probe"))
        {
            DashboardActionProbe();
            return;
        }

        if (args.Contains("--dashboard-action-ui-probe"))
        {
            DashboardActionUiProbe();
            return;
        }

        if (args.Contains("--dashboard-chrome-probe"))
        {
            DashboardChromeProbe();
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
            // 已有实例在跑：不再弹「已经在运行」对话框——双击 exe 的预期是打开面板，不是看提示，
            // 且阻塞式弹框在快速多次启动时会叠好几层。唤醒首实例打开资源面板后静默退出；
            // 唤醒失败（首实例尚在初始化或正在退出）同样静默退出，托盘里本来就有图标。
            try
            {
                using var evt = EventWaitHandle.OpenExisting(ActivateEventName);
                evt.Set();
            }
            catch { }
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Application.ThreadException += (_, e) => LogCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);

        using var activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        var ctx = new MonitorAppContext();
        ctx.AttachActivateEvent(activateEvent);
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
        if (args.Contains("--dashboard"))
        {
            // 诊断/演示用：启动后直接打开资源面板（无视 DashboardEnabled 开关）
            var d = new System.Windows.Forms.Timer { Interval = 900 };
            d.Tick += (_, _) => { d.Stop(); d.Dispose(); ctx.OpenDashboardPublic(); };
            d.Start();
        }
        Application.Run(ctx);
    }

    /// <summary>
    /// 诊断用（plan §8）：不开 UI，主面板数据链路空跑 8 帧 —— 进程采样耗时、按应用聚合结果、
    /// CPU 前十名。结果写 D:/Hermes/output/results/wmonitor-dashboard-probe.txt。
    /// 首帧 CPU 全 0 属预期（差分需要上一帧）。
    /// </summary>
    private static void DashboardProbe()
    {
        var sb = new StringBuilder();
        using var global = new MetricSampler();
        using var hub = new AppMetricsHub();
        for (int i = 0; i < 8; i++)
        {
            var snap = hub.Combine(global.Sample());
            sb.AppendLine(
                $"t={i} procs={snap.ProcessCount} apps={snap.Apps.Count} " +
                $"distinctKeys={snap.Apps.Select(a => a.Key).Distinct().Count()} " +
                $"refDistinct={snap.Apps.Select(a => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(a)).Distinct().Count()} " +
                $"dupPids={snap.ProcessCount - snap.Processes.Select(p => p.Pid).Distinct().Count()} " +
                $"sampleMs={snap.ProcessMs:F2} " +
                $"gpuProcs={snap.Gpu.Rows.Count} gpuInstances={snap.Gpu.EngineInstances} gpuMs={snap.GpuMs:F2}" +
                (snap.Gpu.Error != null ? " gpuErr=" + snap.Gpu.Error : ""));
            sb.AppendLine(
                $"   系统: sys={snap.Processes.Count(p => p.IsSystem)} sysService={snap.Processes.Count(p => p.IsSystemService)} " +
                $"ownerKnown={snap.Processes.Count(p => p.OwnerSid != null)} parentKnown={snap.Processes.Count(p => p.ParentPid > 0)} " +
                $"fg={snap.Processes.Count(p => p.IsForeground)} mainWin={snap.Processes.Count(p => p.HasMainWindow)} " +
                $"limited={snap.Processes.Count(p => p.AccessLimited)} limitedNamed={snap.Processes.Count(p => p.AccessLimited && p.Name.Length > 0)} " +
                $"ntMs={hub.LastNtMs:F2} " +
                $"gpuLuids={snap.Gpu.Luids.Select(l => GpuProcessSampler.LuidBase(l.Luid)).Distinct(StringComparer.OrdinalIgnoreCase).Count()}");
            if (i == 4)
                foreach (var g in snap.Processes.Where(p => p.OwnerSid != null).GroupBy(p => p.OwnerSid).OrderByDescending(g2 => g2.Count()).Take(8))
                    sb.AppendLine($"   sid {g.Key} ×{g.Count()}  例: {string.Join("/", g.Take(3).Select(p => p.Name))}");

            var top = new List<AppMetric>(snap.Apps);
            top.Sort((a, b) => b.CpuPercent.CompareTo(a.CpuPercent));
            for (int k = 0; k < Math.Min(10, top.Count); k++)
            {
                var a = top[k];
                sb.AppendLine(
                    $"   {a.CpuPercent,5:F1}%  {a.WorkingSet64 / 1048576.0,9:F0}MB  {a.Name}" +
                    $"  (主 {a.Main.Pid}" + (a.Members.Count > 1 ? $" ×{a.Members.Count}" : "") + ")");
            }
            sb.AppendLine();
            Thread.Sleep(1000);
        }
        Directory.CreateDirectory(@"D:/Hermes/output/results");
        File.WriteAllText(@"D:/Hermes/output/results/wmonitor-dashboard-probe.txt", sb.ToString());
    }

    /// <summary>
    /// 诊断用（plan §8 / 阶段 2）：单独验证 GPU Engine / GPU Process Memory 解析 —— 8 帧 × 2 秒，
    /// 输出实例数、按 PID 聚合结果（util 滑动平均、专用/共享显存、引擎类型）、显存总和与全局采样器对照。
    /// 结果写 D:/Hermes/output/results/wmonitor-gpu-probe.txt。
    /// </summary>
    private static void DashboardGpuProbe()
    {
        var sb = new StringBuilder();
        using var gpu = new GpuProcessSampler();
        using var global = new MetricSampler();
        sb.AppendLine($"available={gpu.Available} sharedAvailable={!gpu.SharedUnavailable}");
        for (int i = 0; i < 8; i++)
        {
            var g = global.Sample();
            var snap = gpu.Sample();
            sb.AppendLine($"t={i} ms={snap.SampleMs:F2} instances={snap.EngineInstances} procs={snap.Rows.Count}" +
                          (snap.Error != null ? " err=" + snap.Error : "") +
                          $" | 全局对照 vram={g.VramUsedBytes / 1048576.0:F0}MB gpu={g.GpuPercent:F0}%");
            var rows = new List<GpuProcessMetric>(snap.Rows);
            rows.Sort((a, b) => (b.UtilPercent + b.DedicatedBytes / 1073741824.0).CompareTo(a.UtilPercent + a.DedicatedBytes / 1073741824.0));
            long dedSum = 0;
            double utilSum = 0;
            var dedByLuid = new SortedDictionary<string, long>();
            for (int k = 0; k < rows.Count; k++)
            {
                var r = rows[k];
                dedSum += r.DedicatedBytes;
                utilSum += r.UtilPercent;
                if (r.Luid != null)
                    dedByLuid[r.Luid] = dedByLuid.TryGetValue(r.Luid, out long v) ? v + r.DedicatedBytes : r.DedicatedBytes;
                if (k < 12)
                    sb.AppendLine($"   pid={r.Pid,-6} util={r.UtilPercent,5:F1}%  ded={r.DedicatedBytes / 1048576.0,7:F0}MB" +
                                  $"  shr={r.SharedBytes / 1048576.0,5:F0}MB  eng={(r.TopEngine ?? "-"),-10} luid={r.Luid ?? "-"}");
            }
            // 全局对照只统计单适配器；多卡机器按 LUID 分开比才有意义
            foreach (var (luid, bytes) in dedByLuid)
                sb.AppendLine($"   Σdedicated[{luid}]={bytes / 1048576.0:F0}MB");
            sb.AppendLine($"   Σdedicated(全部)={dedSum / 1048576.0:F0}MB  Σutil={utilSum:F1}%  (全局锁定卡 vram={g.VramUsedBytes / 1048576.0:F0}MB gpu={g.GpuPercent:F0}%)");
            sb.AppendLine();
            Thread.Sleep(2000);
        }
        Directory.CreateDirectory(@"D:/Hermes/output/results");
        File.WriteAllText(@"D:/Hermes/output/results/wmonitor-gpu-probe.txt", sb.ToString());
    }

    /// <summary>
    /// 诊断用（plan §8 / 阶段 3）：验证图标缓存三层（内存 LRU / 磁盘 PNG / SHDefExtractIcon）。
    /// 取运行中进程的前若干路径逐个 Get 两遍（首遍现抽或磁盘、次遍 LRU 命中），输出耗时与缓存目录状态。
    /// 结果写 D:/Hermes/output/results/wmonitor-icon-probe.txt。
    /// </summary>
    private static void DashboardIconProbe()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"dir={AppConfig.Dir}\\icon-cache exists={Directory.Exists(Path.Combine(AppConfig.Dir, "icon-cache"))}");

        // 取一帧真实进程路径（有窗口的优先，图标对它们最有意义）
        var paths = new List<string>();
        using (var global = new MetricSampler())
        using (var hub = new AppMetricsHub())
        {
            var snap = hub.Combine(global.Sample());
            foreach (var p in snap.Processes)
            {
                if (p.Path == null || paths.Contains(p.Path)) continue;
                if (p.HasVisibleWindow) paths.Insert(0, p.Path); else paths.Add(p.Path);
            }
            sb.AppendLine($"frame: procs={snap.ProcessCount} withPath={snap.Processes.Count(p => p.Path != null)}");
        }
        paths.RemoveRange(16, Math.Max(0, paths.Count - 16));

        using var cache = new IconCache();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var path in paths)
        {
            sw.Restart();
            var ic1 = cache.Get(path);                // 返回的 Image 归缓存所有，不得 Dispose
            int attempts = 1;
            while (ic1 == null && attempts < 6)
            {
                Thread.Sleep(60);                     // 跨过 50ms 抽取预算窗口再试（预算 2 个/窗口）
                ic1 = cache.Get(path);
                attempts++;
            }
            double ms1 = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            var ic2 = cache.Get(path);                // 成功后第二遍必须 LRU 命中（<0.1ms）
            double ms2 = sw.Elapsed.TotalMilliseconds;
            sb.AppendLine($"{(ic1 != null ? "OK  " : "MISS")} {ms1,7:F2}ms/{attempts}次 / hit {ms2,5:F2}ms  {Path.GetFileName(path)}  [{path}]");
            if (ic1 != null && ic2 == null) sb.AppendLine("   !! 第二遍应为 LRU 命中非空");
        }

        string dir = Path.Combine(AppConfig.Dir, "icon-cache");
        if (Directory.Exists(dir))
        {
            var files = new DirectoryInfo(dir).GetFiles();
            sb.AppendLine($"cache dir: {files.Length} files ({files.Count(f => f.Extension == ".png")} png / {files.Count(f => f.Extension == ".miss")} miss)");
        }
        Directory.CreateDirectory(@"D:/Hermes/output/results");
        File.WriteAllText(@"D:/Hermes/output/results/wmonitor-icon-probe.txt", sb.ToString());
    }

    /// <summary>起一个无窗口、无子进程的牺牲 ping，供动作验证用。</summary>
    private static Process SpawnSacrificial()
    {
        var psi = new ProcessStartInfo("ping.exe", "-n 600 127.0.0.1")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true
        };
        return Process.Start(psi)!;
    }

    /// <summary>轮询到期望的挂起状态（线程进入/离开 Suspended 等待有延迟），≤2s。</summary>
    private static bool WaitSuspended(int pid, bool want)
    {
        for (int i = 0; i < 20; i++)
        {
            if (ProcessActions.IsSuspended(pid) == want) return true;
            Thread.Sleep(100);
        }
        return ProcessActions.IsSuspended(pid) == want;
    }

    /// <summary>
    /// 诊断用（阶段 4）：管控动作端到端，无 UI。牺牲子进程依次 挂起 → 线程级验证 → 恢复 →
    /// 优先级改/读 → 结束 → 等退出；再测死 PID 与 pid=4（System）的错误映射。
    /// 结果写 D:/Hermes/output/results/wmonitor-action-probe.txt。
    /// </summary>
    private static void DashboardActionProbe()
    {
        var sb = new StringBuilder();
        var child = SpawnSacrificial();
        try
        {
            sb.AppendLine($"sacrificial pid={child.Id} name={child.ProcessName}");
            Thread.Sleep(600);   // 子进程线程就绪再挂

            var r1 = ProcessActions.Suspend(child.Id, child.ProcessName);
            bool suspended = WaitSuspended(child.Id, true);
            sb.AppendLine($"suspend -> ok={r1.Ok} {r1.Error} isSuspended={suspended} {(r1.Ok && suspended ? "PASS" : "FAIL")}");
            var r2 = ProcessActions.Resume(child.Id, child.ProcessName);
            bool resumed = WaitSuspended(child.Id, false);
            bool stillSuspended = ProcessActions.IsSuspended(child.Id);
            sb.AppendLine($"resume  -> ok={r2.Ok} {r2.Error} isSuspended={stillSuspended} {(r2.Ok && resumed && !stillSuspended ? "PASS" : "FAIL")}");

            uint? p0 = ProcessActions.GetPriority(child.Id);
            var r3 = ProcessActions.SetPriority(child.Id, Native.BELOW_NORMAL_PRIORITY_CLASS, child.ProcessName);
            uint? p1 = ProcessActions.GetPriority(child.Id);
            sb.AppendLine($"priority {Ptr(p0)} -> belowNormal ok={r3.Ok} read={Ptr(p1)} {(p1 == Native.BELOW_NORMAL_PRIORITY_CLASS ? "PASS" : "FAIL")}");
            var r3b = ProcessActions.SetPriority(child.Id, Native.NORMAL_PRIORITY_CLASS, child.ProcessName);
            sb.AppendLine($"restore normal ok={r3b.Ok}");

            var r4 = ProcessActions.Terminate(child.Id, child.ProcessName);
            child.WaitForExit(5000);
            sb.AppendLine($"terminate -> ok={r4.Ok} exited={child.HasExited} {(r4.Ok && child.HasExited ? "PASS" : "FAIL")}");

            var r5 = ProcessActions.Terminate(child.Id, child.ProcessName);   // 死 PID：预期「已退出」
            sb.AppendLine($"terminate(死PID) -> ok={r5.Ok} err={r5.Error} {(r5.Error?.Contains("已退出") == true ? "PASS" : "FAIL")}");

            var r6 = ProcessActions.Suspend(4, "System");   // 打不开句柄，预期「权限不足」，无副作用
            sb.AppendLine($"suspend(pid=4) -> ok={r6.Ok} err={r6.Error} {(r6.Error?.Contains("权限不足") == true ? "PASS" : "FAIL")}");

            sb.AppendLine($"log exists={File.Exists(ProcessActions.LogPath)}");
            if (File.Exists(ProcessActions.LogPath))
                foreach (string line in File.ReadLines(ProcessActions.LogPath).TakeLast(6))
                    sb.AppendLine("  log: " + line);
        }
        finally
        {
            try { if (!child.HasExited) child.Kill(entireProcessTree: true); } catch { }
            child.Dispose();
        }
        Directory.CreateDirectory(@"D:/Hermes/output/results");
        File.WriteAllText(@"D:/Hermes/output/results/wmonitor-action-probe.txt", sb.ToString());

        static string Ptr(uint? v) => v == null ? "—" : "0x" + v.Value.ToString("X");
    }

    /// <summary>
    /// 诊断用（阶段 4）：真面板 + 真对话框的动作 UI 验收。选中牺牲子进程行 →
    /// 挂起对话框（非模态展示，停留供截图）→ 程序化勾选 + 确认点击（与鼠标同代码路径）→
    /// ExecuteAction（与 OnDetailAction 同一执行层）→ 恢复 → 结束。全程不模拟鼠标。
    /// 截图在外部用 PrintWindow 按窗口标题抓（面板「WMonitor 资源面板」/ 对话框「挂起进程确认」）。
    /// 结束后恢复用户 config 的视图/过滤设置。结果写 wmonitor-action-ui-probe.txt。
    /// </summary>
    private static void DashboardActionUiProbe()
    {
        var sb = new StringBuilder();
        Process? child = null;
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            child = SpawnSacrificial();
            sb.AppendLine($"sacrificial pid={child.Id}");

            var cfg = AppConfig.Load();
            var (view0, bg0, sys0, rect0) = (cfg.DashboardDefaultView, cfg.DashboardShowBackground,
                cfg.DashboardShowSystemProcesses, cfg.DashboardWindowRect);
            cfg.DashboardDefaultView = DashboardView.Process;
            cfg.DashboardShowBackground = true;
            cfg.DashboardShowSystemProcesses = true;
            cfg.DashboardWindowRect = "";
            cfg.Save();

            using var global = new MetricSampler();
            var form = new MainDashboardForm(cfg, Theme.Resolve(cfg.Theme), () => global.Sample());

            form.Shown += async (_, _) =>
            {
                try
                {
                    for (int i = 0; i < 40 && !form.ProbeSelectPid(child!.Id); i++) await Task.Delay(250);
                    sb.AppendLine($"select child row = {form.ProbeSelectPid(child!.Id)}");
                    await Task.Delay(2500);   // 截图 1：面板 + 详情条动作按钮

                    // 挂起：对话框非模态展示（截图 2），勾选确认走真点击路径，执行走真执行层
                    var dlgS = form.ProbeOpenDialog(DashboardAction.Suspend);
                    dlgS.Show(form);
                    await Task.Delay(2500);
                    dlgS.ProbeSetConfirm();
                    dlgS.ProbeConfirmClick();
                    var r1 = form.ProbeExecuteConfirmed(dlgS, DashboardAction.Suspend);
                    sb.AppendLine($"suspend -> ok={r1.Ok} {r1.Error} isSuspended={WaitSuspended(child.Id, true)} {(r1.Ok ? "PASS" : "FAIL")}");
                    await Task.Delay(2500);   // 截图 3：详情条「恢复」按钮 + meta「已挂起」
                    dlgS.Close();
                    dlgS.Dispose();

                    var dlgR = form.ProbeOpenDialog(DashboardAction.Resume);
                    dlgR.ProbeSetConfirm();
                    dlgR.ProbeConfirmClick();
                    var r2 = form.ProbeExecuteConfirmed(dlgR, DashboardAction.Resume);
                    sb.AppendLine($"resume -> ok={r2.Ok} {r2.Error} isSuspended={WaitSuspended(child.Id, false)} {(r2.Ok ? "PASS" : "FAIL")}");
                    dlgR.Dispose();

                    var dlgT = form.ProbeOpenDialog(DashboardAction.Terminate);
                    dlgT.Show(form);
                    await Task.Delay(2500);   // 截图：结束任务确认（红钮 + 必勾确认框）
                    dlgT.ProbeSetConfirm();
                    dlgT.ProbeConfirmClick();
                    var r3 = form.ProbeExecuteConfirmed(dlgT, DashboardAction.Terminate);
                    child.WaitForExit(5000);
                    sb.AppendLine($"terminate -> ok={r3.Ok} exited={child.HasExited} {(r3.Ok && child.HasExited ? "PASS" : "FAIL")}");
                    dlgT.Dispose();

                    await Task.Delay(1500);
                }
                catch (Exception ex)
                {
                    sb.AppendLine("PROBE-FAIL " + ex);
                }
                finally
                {
                    form.Close();
                }
            };
            Application.Run(form);

            cfg.DashboardDefaultView = view0;           // 恢复用户的视图 / 过滤 / 窗口记忆
            cfg.DashboardShowBackground = bg0;
            cfg.DashboardShowSystemProcesses = sys0;
            cfg.DashboardWindowRect = rect0;
            cfg.Save();
        }
        finally
        {
            try { if (child is { HasExited: false }) child.Kill(entireProcessTree: true); } catch { }
            child?.Dispose();
            Directory.CreateDirectory(@"D:/Hermes/output/results");
            File.WriteAllText(@"D:/Hermes/output/results/wmonitor-action-ui-probe.txt", sb.ToString());
        }
    }

    /// <summary>
    /// 诊断用：标题栏窗口钮消息级点击回归（2026-11-05 修复「最大化/最小化/关闭无响应」）。
    /// 复现真实鼠标按下后的完整消息序列：WM_NCLBUTTONDOWN(HT*, 屏幕坐标) →
    /// WM_MOUSEMOVE / WM_LBUTTONUP（捕获后的客户区消息，坐标已是客户区）。
    /// 窗口故意摆在非 (0,0)：旧 bug 对客户区坐标又 PointToClient 一次（平移一个窗口原点），
    /// 只有窗口恰在屏幕原点时碰巧能用。结果写 D:/Hermes/output/results/wmonitor-chrome-probe.txt。
    /// </summary>
    private static void DashboardChromeProbe()
    {
        var sb = new StringBuilder();
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var cfg = AppConfig.Load();
            string rect0 = cfg.DashboardWindowRect;
            cfg.DashboardWindowRect = "";
            cfg.Save();

            using var global = new MetricSampler();
            var form = new MainDashboardForm(cfg, Theme.Resolve(cfg.Theme), () => global.Sample());
            bool closed = false;
            form.FormClosed += (_, _) => closed = true;

            form.Shown += async (_, _) =>
            {
                try
                {
                    await Task.Delay(300);
                    form.SetDesktopLocation(220, 140);   // 非原点：旧 bug 的必现场景
                    await Task.Delay(400);

                    // hover：post NC 消息（屏幕坐标）后看 ProbeHoverFired。本探针进程的
                    // SetCursorPos 被输入桌面拒绝（真实光标进不来），且真实光标不在窗口上
                    // 时系统立刻回发 WM_NCMOUSELEAVE 清掉 _hot —— 瞬态在外部看不见，
                    // 断言走 ChromeForm 内部旗标。
                    var (minR, maxR, closeR) = (form.ProbeButtonRect(ChromeForm.ChromeProbeBtn.Min),
                        form.ProbeButtonRect(ChromeForm.ChromeProbeBtn.Max),
                        form.ProbeButtonRect(ChromeForm.ChromeProbeBtn.Close));
                    Post(form, WM_NCMOUSEMOVE, 0, Lp(form.PointToScreen(Center(closeR))));
                    await Task.Delay(300);
                    sb.AppendLine($"hover close -> fired={form.ProbeHoverFired} last={form.ProbeHot} " +
                        $"{(form.ProbeHoverFired && form.ProbeHot == "None" ? "PASS" : "FAIL")}");
                    sb.AppendLine($"hover settle(LEAVE 清理) -> hot={form.ProbeHot} {(form.ProbeHot == "None" ? "PASS" : "FAIL")}");

                    // pressed 生命周期：按下 → 滑出(取消) → 滑回(恢复)
                    Post(form, WM_NCLBUTTONDOWN, HTMAXBUTTON, Lp(form.PointToScreen(Center(maxR))));
                    await Task.Delay(120);
                    sb.AppendLine($"press max -> press={form.ProbePress} inside={form.ProbePressInside} " +
                        $"{(form.ProbePress == "Max" && form.ProbePressInside ? "PASS" : "FAIL")}");
                    Post(form, WM_MOUSEMOVE, 0, Lp(new Point(60, 20)));       // 客户区坐标，滑出
                    await Task.Delay(120);
                    sb.AppendLine($"slide out -> inside={form.ProbePressInside} {(!form.ProbePressInside ? "PASS" : "FAIL")}");
                    Post(form, WM_MOUSEMOVE, 0, Lp(Center(maxR)));            // 滑回
                    await Task.Delay(120);
                    sb.AppendLine($"slide back -> inside={form.ProbePressInside} {(form.ProbePressInside ? "PASS" : "FAIL")}");
                    Post(form, WM_LBUTTONUP, 0, Lp(Center(maxR)));
                    await Task.Delay(400);
                    sb.AppendLine($"click max -> state={form.WindowState} press={form.ProbePress} " +
                        $"{(form.WindowState == FormWindowState.Maximized && form.ProbePress == "None" ? "PASS" : "FAIL")}");
                    await Task.Delay(600);   // 等最大化动画/布局

                    // 还原（再点一次最大化钮）
                    maxR = form.ProbeButtonRect(ChromeForm.ChromeProbeBtn.Max);
                    await ClickChrome(form, HTMAXBUTTON, Center(maxR));
                    await Task.Delay(400);
                    sb.AppendLine($"click restore -> state={form.WindowState} " +
                        $"{(form.WindowState == FormWindowState.Normal ? "PASS" : "FAIL")}");

                    // 最小化
                    minR = form.ProbeButtonRect(ChromeForm.ChromeProbeBtn.Min);
                    await ClickChrome(form, HTMINBUTTON, Center(minR));
                    await Task.Delay(500);
                    sb.AppendLine($"click min -> state={form.WindowState} " +
                        $"{(form.WindowState == FormWindowState.Minimized ? "PASS" : "FAIL")}");
                    form.WindowState = FormWindowState.Normal;
                    form.SetDesktopLocation(220, 140);
                    await Task.Delay(400);

                    // C20：右键标题栏 → 系统菜单拦截路径。探针模式只置旗标不真弹
                    // TrackPopupMenu（会阻塞消息循环）；真实弹菜单的观感留用户晨检。
                    form.ProbeInterceptSysMenu = true;
                    Post(form, WM_NCRBUTTONUP, HTCAPTION, Lp(form.PointToScreen(new Point(300, 20))));
                    await Task.Delay(200);
                    sb.AppendLine($"caption rclick -> sysmenu={form.ProbeSysMenuFired} {(form.ProbeSysMenuFired ? "PASS" : "FAIL")}");
                    form.ProbeResetSysMenu();
                    Post(form, WM_NCRBUTTONUP, 1 /*HTCLIENT*/, Lp(form.PointToScreen(new Point(300, 300))));
                    await Task.Delay(200);
                    sb.AppendLine($"client rclick -> sysmenu={form.ProbeSysMenuFired} {(!form.ProbeSysMenuFired ? "PASS" : "FAIL")}");
                    form.ProbeInterceptSysMenu = false;

                    // 设置窗（FixedSingle，只有关闭钮）走同一基类
                    var set = new SettingsForm(cfg, Theme.Resolve(cfg.Theme), new List<TaskbarInfo>());
                    set.Show(form);
                    await Task.Delay(500);
                    sb.AppendLine($"dwm corner pref (C21) -> main={DwmPref(form.Handle)} settings={DwmPref(set.Handle)} " +
                        $"{(DwmPref(form.Handle) == 2 && DwmPref(set.Handle) == 2 ? "PASS" : "FAIL")}");
                    var sc = set.ProbeButtonRect(ChromeForm.ChromeProbeBtn.Close);
                    await ClickChrome(set, HTCLOSE, Center(sc));
                    await Task.Delay(500);
                    sb.AppendLine($"settings close -> disposed={set.IsDisposed} {(set.IsDisposed ? "PASS" : "FAIL")}");

                    // C18/C19：焦点环与 Ghost 渲染。小探针窗 + DrawToBitmap 像素断言
                    //（探针进程没有真实鼠标/键盘，焦点用程序化 Focus()，与真实 Tab 同一条 GotFocus 路径）。
                    var pf = new Form
                    {
                        FormBorderStyle = FormBorderStyle.None,
                        ShowInTaskbar = false,
                        StartPosition = FormStartPosition.Manual,
                        Location = new Point(260, 180),
                        Size = new Size(420, 130),
                        BackColor = Theme.Dark.WindowBg
                    };
                    var seg = new Segmented { Items = new[] { "A", "B", "C" }, Th = Theme.Dark, Left = 16, Top = 16, Width = 300 };
                    var chip = new Chip("显示后台") { Th = Theme.Dark, Left = 16, Top = 62 };
                    var ghost = new BentoButton
                    {
                        Text = "低强调", Ghost = true, Th = Theme.Dark,
                        Left = 170, Top = 62, Width = 88, Height = 32, Shadow = 0
                    };
                    pf.Controls.AddRange(new Control[] { seg, chip, ghost });
                    pf.Show(form);
                    await Task.Delay(400);

                    static Bitmap Snap(Control c)
                    {
                        var b = new Bitmap(c.Width, c.Height);
                        c.DrawToBitmap(b, new Rectangle(0, 0, c.Width, c.Height));
                        return b;
                    }
                    static bool Diff(Bitmap a, Bitmap b)
                    {
                        if (a.Size != b.Size) return true;
                        for (int y = 0; y < a.Height; y++)
                            for (int x = 0; x < a.Width; x++)
                                if (a.GetPixel(x, y) != b.GetPixel(x, y)) return true;
                        return false;
                    }

                    seg.Focus();
                    await Task.Delay(150);
                    sb.AppendLine($"segmented focus -> focused={seg.Focused} {(seg.Focused ? "PASS" : "FAIL")}");
                    var seg1 = Snap(seg);           // 有焦点（焦点环）
                    chip.Focus();
                    await Task.Delay(150);
                    var seg0 = Snap(seg);           // 无焦点
                    sb.AppendLine($"segmented ring -> diff={Diff(seg0, seg1)} {(Diff(seg0, seg1) ? "PASS" : "FAIL")}");
                    sb.AppendLine($"chip focus -> focused={chip.Focused} {(chip.Focused ? "PASS" : "FAIL")}");
                    var chip1 = Snap(chip);
                    ghost.Focus();
                    await Task.Delay(150);
                    var chip0 = Snap(chip);
                    sb.AppendLine($"chip ring -> diff={Diff(chip0, chip1)} {(Diff(chip0, chip1) ? "PASS" : "FAIL")}");

                    // Ghost：常态无底色（采样点避开焦点环 2.5px 内缩带）、无投影、文字 Label 起步
                    var g0 = Snap(ghost);
                    var tl = g0.GetPixel(0, 0);
                    var bl = g0.GetPixel(g0.Width / 2, g0.Height - 1);
                    sb.AppendLine($"ghost transparent -> tl={tl} bl={bl} " +
                        $"{(tl == Theme.Dark.WindowBg && bl == Theme.Dark.WindowBg ? "PASS" : "FAIL")}");
                    seg0.Dispose(); seg1.Dispose(); chip0.Dispose(); chip1.Dispose(); g0.Dispose();
                    pf.Close();
                    pf.Dispose();

                    // 关闭主面板（收尾；Run 随之返回）
                    closeR = form.ProbeButtonRect(ChromeForm.ChromeProbeBtn.Close);
                    await ClickChrome(form, HTCLOSE, Center(closeR));
                }
                catch (Exception ex)
                {
                    sb.AppendLine("PROBE-FAIL " + ex);
                    form.Close();
                }
            };
            Application.Run(form);

            sb.AppendLine($"click close -> closed={closed} {(closed ? "PASS" : "FAIL")}");
            cfg.DashboardWindowRect = rect0;
            cfg.Save();
        }
        finally
        {
            Directory.CreateDirectory(@"D:/Hermes/output/results");
            File.WriteAllText(@"D:/Hermes/output/results/wmonitor-chrome-probe.txt", sb.ToString());
        }
    }

    private const int WM_NCMOUSEMOVE = 0x00A0;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int WM_NCRBUTTONUP = 0x00A5;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONUP = 0x0202;
    private const int HTCAPTION = 2;
    private const int HTMINBUTTON = 8, HTMAXBUTTON = 9, HTCLOSE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    /// <summary>C21：读回 DWM 圆角偏好（2 = ROUND）。这只是「偏好已生效」；
    /// 系统合成圆角本身 PrintWindow 截不到（合成发生在窗口表面之外，docs/14 P3 日志）。</summary>
    private static int DwmPref(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, out int v, 4) == 0 ? v : -1;

    private static void Post(Control c, int msg, IntPtr wParam, IntPtr lParam) =>
        PostMessage(c.Handle, msg, wParam, lParam);

    private static IntPtr Lp(Point p) => (IntPtr)(uint)(p.X & 0xFFFF | (p.Y & 0xFFFF) << 16);

    private static Point Center(Rectangle r) => new(r.Left + r.Width / 2, r.Top + r.Height / 2);

    /// <summary>一次完整点击：按下（屏幕坐标）→ 移动 → 抬起（客户区坐标）。</summary>
    private static async Task ClickChrome(Control c, int ht, Point clientCenter)
    {
        Post(c, WM_NCLBUTTONDOWN, ht, Lp(((Form)c).PointToScreen(clientCenter)));
        await Task.Delay(120);
        Post(c, WM_MOUSEMOVE, 0, Lp(clientCenter));
        await Task.Delay(60);
        Post(c, WM_LBUTTONUP, 0, Lp(clientCenter));
        await Task.Delay(200);
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
