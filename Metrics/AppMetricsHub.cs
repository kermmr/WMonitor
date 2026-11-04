using System.Diagnostics;

namespace WMonitor.Metrics;

/// <summary>按应用聚合后的一行。Members 与 Main 引用的是本帧 Processes 里的对象。</summary>
public sealed class AppMetric
{
    public string Key = "";               // 聚合键：完整路径；无路径回退 "\0" + 进程名（同名不同路径不合并）
    public string Name = "";              // 显示名：FileDescription > ProductName > 进程名
    public string? Path;
    public ProcessMetric Main = null!;    // 代表进程：前台 > 有可见窗口 > CPU 最高
    public List<ProcessMetric> Members = new();
    public double CpuPercent;
    public long WorkingSet64;
    public long PrivateMemory64;

    // GPU 指标（阶段 2）：成员求和；GpuTopEngine 取 util 最高成员的引擎类型
    public double GpuPercent;
    public long GpuDedicatedBytes;
    public long GpuSharedBytes;
    public string? GpuTopEngine;

    public bool HasVisibleWindow;
    public bool HasMainWindow;            // 组里有进程带应用主窗（GW_OWNER 无 / WS_EX_APPWINDOW）
    public bool IsForeground;
    public bool IsSystem;
    public bool AccessLimited;
}

/// <summary>
/// 主面板一帧的完整快照。所有集合在下一帧 <see cref="AppMetricsHub.Combine"/> 后失效（UI 线程序列消费，无竞态）。
/// </summary>
public sealed class AppMetricsSnapshot
{
    public MetricsSnapshot Global = new();    // 引用主循环最新全局快照（只读）
    public List<ProcessMetric> Processes = new();
    public List<AppMetric> Apps = new();
    public DateTime At;
    public double ProcessMs;
    public int ProcessCount;

    /// <summary>最近一次 GPU 子采样结果（0.5Hz，两次采样之间沿用旧值；Rows 只读）。</summary>
    public GpuProcessSnapshot Gpu = new();
    public double GpuMs;
}

/// <summary>
/// 主面板的数据中枢：调度 ProcessSampler、缓存版本信息、按应用聚合、标注系统进程。
///
/// 生命周期（plan §4.3）：主面板打开时创建、关闭时 Dispose，不常驻。
/// 采样在 UI 线程（主面板自己的 WinForms Timer 驱动），与全局采样器只交换
/// MetricsSnapshot 引用，不共享 PDH query（plan §10.6）。
/// </summary>
public sealed class AppMetricsHub : IDisposable
{
    /// <summary>系统进程名称黑名单 —— 阶段 3 起只是兜底：主判定是 token owner SID（ProcessSampler.
    /// IsSystemServiceSid，S-1-5-18/19/20 与每服务 / 窗口管理器虚拟账户），覆盖打不开 token 的
    /// 内核项（System / Registry / Memory Compression，名称与镜像名均来自 NtQuery）。
    /// 只列常驻且从不该出现在默认列表里的核心项；服务宿主类进程平时被「显示后台」过滤，不在此列。</summary>
    private static readonly HashSet<string> SystemNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "registry", "memory compression", "secure system",
        "csrss", "smss", "wininit", "winlogon", "services", "lsass",
        "svchost", "dwm", "fontdrvhost", "audiodg", "spoolsv",
        "wudfhost", "wmiprvse", "securityhealthservice", "sgrmbroker"
    };

    private readonly ProcessSampler _proc = new();
    private readonly GpuProcessSampler _gpu = new();
    private readonly VersionInfoCache _ver = new();
    private readonly int _gpuIntervalMs;

    private long _gpuNextAt;              // Environment.TickCount64，下次 GPU 子采样时间

    // GPU 子采样结果按 PID 的索引：只在重新采样时重建（0.5Hz），合并进 rows 每帧查表
    private GpuProcessSnapshot _gpuSnap = new();
    private Dictionary<int, GpuProcessMetric> _gpuByPid = new();

    // 本帧的组：key → AppMetric。每组每帧新建（~几百个小对象/秒，GC 可忽略），
    // 不做跨帧对象复用 —— 复用版本踩过 Members 只增不清、同对象重复入列表两个坑。
    private readonly Dictionary<string, AppMetric> _frame = new(StringComparer.Ordinal);
    private readonly List<AppMetric> _apps = new(64);
    private readonly AppMetricsSnapshot _snap = new();

    public double LastSampleMs => _proc.LastSampleMs;

    /// <summary>最近一次 NtQuerySystemInformation 刷新耗时（诊断用；未刷新的帧为 0）。</summary>
    public double LastNtMs => _proc.LastNtMs;
    public int ProcessCount => _proc.Count;

    /// <summary>GPU 采样器（probe / 状态行读降级状态用）。</summary>
    public GpuProcessSampler Gpu => _gpu;

    /// <param name="gpuIntervalMs">GPU 子采样间隔（DashboardGpuRefreshMs，plan §3 建议 ≥2 倍主间隔）。</param>
    public AppMetricsHub(int gpuIntervalMs = 2000) =>
        _gpuIntervalMs = Math.Clamp(gpuIntervalMs, 500, 30_000);

    /// <param name="global">主循环最新全局快照（直接引用，勿改）。可空（probe 场景）。</param>
    public AppMetricsSnapshot Combine(MetricsSnapshot? global)
    {
        long nowTicks = Stopwatch.GetTimestamp();
        long nowMs = Environment.TickCount64;
        var rows = _proc.Sample();

        // ---- GPU 子采样（0.5Hz，连续超时自动拉长间隔，plan §5 降级）----
        if (nowMs >= _gpuNextAt)
        {
            _gpuSnap = _gpu.Sample();
            _gpuByPid.Clear();
            foreach (var g in _gpuSnap.Rows)
                _gpuByPid[g.Pid] = g;                       // 采样器已按 PID 合行，键唯一
            int interval = _gpu.Overloaded ? _gpuIntervalMs * 5 / 2 : _gpuIntervalMs;
            _gpuNextAt = nowMs + interval;
            _snap.Gpu = _gpuSnap;
            _snap.GpuMs = _gpuSnap.SampleMs;
        }

        _snap.Global = global ?? new MetricsSnapshot();
        _snap.Processes = rows;
        _snap.ProcessCount = rows.Count;
        _snap.ProcessMs = _proc.LastSampleMs;
        _snap.At = DateTime.Now;

        for (int i = 0; i < rows.Count; i++)
        {
            var p = rows[i];
            if (p.Path != null)
            {
                var v = _ver.Get(p.Path, nowTicks);
                p.Company = v.Company;
                p.Description = v.Description;
            }
            p.IsSystem = p.IsSystemService || SystemNames.Contains(NameWithoutExt(p.Name));
            if (_gpuByPid.Count > 0 && _gpuByPid.TryGetValue(p.Pid, out var gm))
            {
                p.GpuPercent = gm.UtilPercent;
                p.GpuDedicatedBytes = gm.DedicatedBytes;
                p.GpuSharedBytes = gm.SharedBytes;
                p.GpuTopEngine = gm.TopEngine;
            }
            else if (p.GpuPercent != 0 || p.GpuDedicatedBytes != 0)
            {
                // 进程退出 GPU 活动（或已消失）：不残留旧值
                p.GpuPercent = 0;
                p.GpuDedicatedBytes = 0;
                p.GpuSharedBytes = 0;
                p.GpuTopEngine = null;
            }
        }

        BuildApps(rows, nowTicks);
        return _snap;
    }

    private void BuildApps(List<ProcessMetric> rows, long nowTicks)
    {
        _apps.Clear();
        _frame.Clear();

        for (int i = 0; i < rows.Count; i++)
        {
            var p = rows[i];
            string key = p.Path ?? "\0" + p.Name;
            if (!_frame.TryGetValue(key, out var g))
            {
                g = new AppMetric { Key = key, Path = p.Path, Members = new List<ProcessMetric>(4) };
                if (p.Path != null)
                {
                    var v = _ver.Get(p.Path, nowTicks);
                    g.Name = VersionInfoCache.DisplayName(v, p.Name);
                }
                _frame[key] = g;
                _apps.Add(g);
            }
            g.Members.Add(p);
            g.CpuPercent += p.CpuPercent;
            g.WorkingSet64 += p.WorkingSet64;
            g.PrivateMemory64 += p.PrivateMemory64;
            g.GpuPercent += p.GpuPercent;
            g.GpuDedicatedBytes += p.GpuDedicatedBytes;
            g.GpuSharedBytes += p.GpuSharedBytes;
            if (p.GpuTopEngine != null &&
                (g.GpuTopEngine == null || p.GpuPercent > g.GpuPercent - p.GpuPercent))
                g.GpuTopEngine = p.GpuTopEngine;      // util 最高成员的引擎类型
            g.HasVisibleWindow |= p.HasVisibleWindow;
            g.HasMainWindow |= p.HasMainWindow;
            g.IsForeground |= p.IsForeground;
            g.IsSystem |= p.IsSystem;
            g.AccessLimited |= p.AccessLimited;
            if (g.Path == null) g.Path = p.Path;
        }

        // 第二遍：每个组挑代表进程（前台 > 有可见窗口 > CPU 最高），补显示名
        for (int i = 0; i < _apps.Count; i++)
        {
            var g = _apps[i];
            ProcessMetric best = g.Members[0];
            for (int j = 1; j < g.Members.Count; j++)
            {
                var c = g.Members[j];
                if (PickScore(c) > PickScore(best)) best = c;
            }
            g.Main = best;
            if (g.Name.Length == 0 || g.Path == null)
            {
                if (best.Path != null)
                    g.Name = VersionInfoCache.DisplayName(_ver.Get(best.Path, nowTicks), best.Name);
                else if (g.Name.Length == 0)
                    g.Name = best.Name;
            }
        }

        _snap.Apps = _apps;

        // 代表进程评分（阶段 3 加主窗口与 GPU）：前台 > 应用主窗 > 有窗口 > CPU/GPU 活跃
        static int PickScore(ProcessMetric p) =>
            (p.IsForeground ? 8 : 0) +
            (p.HasMainWindow ? 4 : p.HasVisibleWindow ? 2 : 0) +
            (p.CpuPercent > 0.5 || p.GpuPercent > 0.5 ? 1 : 0);
    }

    /// <summary>黑名单按无扩展名比较（阶段 1 的隐患：名单存 "svchost"，进程名是 "svchost.exe"，永远不命中）。</summary>
    internal static string NameWithoutExt(string name)
    {
        int i = name.LastIndexOf('.');
        return i > 0 ? name[..i] : name;
    }

    public void Dispose()
    {
        _proc.Dispose();
        _gpu.Dispose();
        _frame.Clear();
        _apps.Clear();
        _gpuByPid.Clear();
    }
}
