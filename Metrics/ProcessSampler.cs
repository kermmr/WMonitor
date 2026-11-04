using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WMonitor.Interop;

namespace WMonitor.Metrics;

/// <summary>
/// 单个进程一帧的指标。对象按 PID 跨帧复用（采样与消费都在 UI 线程串行进行，见 AGENTS.md §6），
/// <see cref="ProcessSampler.Sample"/> 每帧返回新 List；消费方只读，不要保存元素引用跨帧使用。
/// </summary>
public sealed class ProcessMetric
{
    public int Pid;
    public int ParentPid;                 // NtQuerySystemInformation（阶段 3），受限进程也有
    public string Name = "";              // 路径文件名 > NtQuery 镜像名 > 空（UI 显示「PID n」）
    public string? Path;
    public string? Company;
    public string? Description;
    public string? WindowTitle;           // 主窗口标题（阶段 3 起取最大应用窗，不再是 z 序首个）
    public bool HasVisibleWindow;
    public bool HasMainWindow;            // 最大可见窗口是应用主窗（无 owner 或 WS_EX_APPWINDOW）
    public bool IsForeground;
    public long WorkingSet64;
    public long PrivateMemory64;
    public double CpuPercent;
    public long KernelTime100Ns;
    public long UserTime100Ns;
    public DateTime? StartedAt;
    public bool AccessLimited;            // 路径读不到（提权 / PPL / 内核项）；名称可能仍有（NtQuery）
    public string? Architecture;          // x86 / x64 / ARM64

    /// <summary>token user SID（"S-1-5-…"）。null = 打不开 token（内核项 / PPL / 他人服务进程）。每 PID 一次性读取。</summary>
    public string? OwnerSid;

    /// <summary>会话 ID（NtQuery；-1 = 未知）。0 = 服务/系统会话，是系统进程的免权限信号
    /// （非提权读不到服务进程 token，owner SID 只能覆盖同用户进程 —— 实测 334 个全是本机用户 SID）。</summary>
    public int SessionId = -1;

    /// <summary>owner 是 SYSTEM / 服务类虚拟账户（S-1-5-18/19/20、S-1-5-80-、S-1-5-96-），或 SessionId==0。</summary>
    public bool IsSystemService;

    public bool IsSystem;                 // AppMetricsHub 标注：IsSystemService 或黑名单兜底命中

    // GPU 指标（阶段 2）：AppMetricsHub 按 PID 从 GpuProcessSnapshot 合入，
    // 0.5Hz 子采样，两次 GPU 采样之间沿用旧值。
    public double GpuPercent;             // 3 帧滑动平均
    public long GpuDedicatedBytes;
    public long GpuSharedBytes;
    public string? GpuTopEngine;          // util 最高的引擎类型原文（3D / Cuda / VideoDecode …）
}

/// <summary>
/// 进程级采样：枚举 PID、CPU 差分、WorkingSet / PrivateMemory、完整路径、架构、窗口可见性。
/// 预算 ≤ 4 ms/帧（plan §5）。只在 UI 线程调用，无锁。
///
/// 设计要点（plan §3.1 / 进度文档 §3）：
/// - EnumProcesses + OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)，不用 Process.GetProcesses()（GC 压力）。
/// - 句柄按 PID 缓存、PID 消失即保留 12 秒（graveyard，防瞬时进程闪烁），到期 CloseHandle。
/// - 路径 / 架构是慢信息，TTL 15 秒；窗口状态每帧由 <see cref="WindowScanner"/> 扫一次。
/// - NtQuerySystemInformation（阶段 3）：受限进程镜像名 + 父 PID，常规 0.5Hz、有新 PID 时立即刷。
/// - token owner SID（阶段 3）：每 Entry 一次，供系统进程判定（AppMetricsHub）。
/// </summary>
public sealed class ProcessSampler : IDisposable
{
    private const int SlowInfoTtlMs = 15_000;
    private const int GraveyardTtlMs = 12_000;
    private const int NtMaintainMs = 60_000;   // 维护性全量刷新（正常情况下新 PID 触发已覆盖）
    private const int NtRetryMs = 500;         // 两次 NtQuery 最小间隔（触发风暴 / 失败重试节流）
    private const ushort MachineX64 = 0x8664, MachineX86 = 0x014C, MachineArm64 = 0xAA64;

    private sealed class Entry
    {
        public ProcessMetric M = new();
        public IntPtr Handle;          // QUERY_LIMITED 句柄，够读路径 / 时间 / 内存 / token
        public long PathAt;            // TickCount64，下次刷新路径的时间
        public bool MachineTried;
        public bool OwnerTried;        // token owner 只读一次（PID 存续期内不变）
        public long LastSeen;
        public bool HavePrev;          // 有上一帧 CPU 时间才做差分（新建 / 复活的首帧为 0）
        public long PrevK, PrevU;      // 上一帧 Kernel+User（100ns）
    }

    private sealed class Grave
    {
        public Entry E = null!;
        public long ExpiresAt;
    }

    private sealed class NtProc
    {
        public string? Name;
        public int Parent;
        public int Session;
    }

    private readonly Dictionary<int, Entry> _live = new(256);
    private readonly Dictionary<int, Grave> _graveyard = new(16);
    private readonly Dictionary<int, NtProc> _nt = new(256);
    private uint[] _pids = new uint[1024];
    private readonly StringBuilder _sb = new(1100);
    private readonly WindowScanner _windows = new();
    private IntPtr _ntBuf;
    private uint _ntCap;
    private long _ntNextAt;                // TickCount64，下次允许刷新（≥500ms 间隔）
    private long _ntMaintainAt;            // TickCount64，维护性刷新时间
    private int _newSinceNt;               // 上次 NtQuery 后「真新建」的 Entry 数（复活不算）
    private long _lastTicks;
    private bool _disposed;

    /// <summary>本帧采样耗时（毫秒），状态栏与 probe 显示。</summary>
    public double LastSampleMs { get; private set; }

    /// <summary>本帧 NtQuerySystemInformation 刷新耗时（毫秒，诊断用；未刷新为 0）。</summary>
    public double LastNtMs { get; private set; }

    /// <summary>本帧进程数（含系统与受限项）。</summary>
    public int Count { get; private set; }

    public List<ProcessMetric> Sample()
    {
        var sw = Stopwatch.StartNew();
        long nowTick = Stopwatch.GetTimestamp();
        double intervalSec = _lastTicks == 0 ? 0 : (nowTick - _lastTicks) / (double)Stopwatch.Frequency;
        _lastTicks = nowTick;
        long nowMs = Environment.TickCount64;

        int n = ReadPids();
        _windows.Scan();

        // ---- NtQuery 镜像名 / 父 PID：PID 的名字与父 PID 终身不变，只在「首帧 / 有真新 PID / 60s 维护」时刷。
        // 实测刷新一次 ~6ms（500+ 进程），不适合周期刷；graveyard 复活的 Entry 名字还在，不算新 PID。----
        bool ntDue = _newSinceNt > 0 ? nowMs >= _ntNextAt : nowMs >= _ntMaintainAt;
        if (ntDue)
        {
            var ntSw = Stopwatch.StartNew();
            bool ok = RefreshNt();
            LastNtMs = ntSw.Elapsed.TotalMilliseconds;
            if (ok) _newSinceNt = 0;
            _ntNextAt = nowMs + NtRetryMs;         // 成功防风暴、失败节流重试，同一个最小间隔
            if (ok) _ntMaintainAt = nowMs + NtMaintainMs;
        }
        else
        {
            LastNtMs = 0;
        }

        var rows = new List<ProcessMetric>(_live.Count + 16);
        double invCores = 1.0 / Environment.ProcessorCount;

        for (int i = 0; i < n; i++)
        {
            int pid = (int)_pids[i];
            if (pid <= 0) continue;

            if (!_live.TryGetValue(pid, out var e))
            {
                bool revived = _graveyard.Remove(pid, out var g);
                e = revived && g != null ? g.E : Create(pid);
                e.HavePrev = false;                    // 复活进程的旧差分基线已过期，重算一帧
                _live[pid] = e;
                if (!revived) _newSinceNt++;           // 只有真新建的 PID 才需要 NtQuery 补名字 / 父 PID
            }
            e.LastSeen = nowMs;

            var m = e.M;
            m.Pid = pid;

            // ---- CPU 差分（归一化到整机 100%）----
            if (e.Handle != IntPtr.Zero && Native.GetProcessTimes(e.Handle, out long created, out _, out long k, out long u))
            {
                m.StartedAt ??= DateTime.FromFileTime(created);
                m.KernelTime100Ns = k;
                m.UserTime100Ns = u;
                m.CpuPercent = e.HavePrev && intervalSec > 0.05
                    ? Math.Clamp(((k + u) - (e.PrevK + e.PrevU)) * 1e-7 / intervalSec * invCores * 100.0, 0, 100)
                    : 0;
                e.PrevK = k;
                e.PrevU = u;
                e.HavePrev = true;
            }
            else
            {
                m.CpuPercent = 0;
            }

            // ---- 内存 ----
            if (e.Handle != IntPtr.Zero)
            {
                var pmc = new Native.PROCESS_MEMORY_COUNTERS { cb = (uint)Marshal.SizeOf<Native.PROCESS_MEMORY_COUNTERS>() };
                if (Native.K32GetProcessMemoryInfo(e.Handle, ref pmc, pmc.cb))
                {
                    m.WorkingSet64 = (long)pmc.WorkingSetSize;
                    m.PrivateMemory64 = (long)pmc.PrivateUsage;   // EX 布局：任务管理器「专用内存」口径
                }
            }

            RefreshSlow(e, pid, nowMs);
            FillWindow(m);

            rows.Add(m);
        }

        ReapDead(nowMs);

        Count = rows.Count;
        LastSampleMs = sw.Elapsed.TotalMilliseconds;
        return rows;
    }

    private Entry Create(int pid)
    {
        var e = new Entry
        {
            Handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid),
            PathAt = 0
        };
        if (pid == 4) e.M.Name = "System";   // System 进程打不开但名字是公认的（NtQuery 也有同名兜底）
        return e;
    }

    /// <summary>
    /// NtQuerySystemInformation(SystemProcessInformation) 全量镜像名 + 父 PID。
    /// 对 PPL / 提权进程也可读（任务管理器进程名同源），buffer grow-only 复用。
    /// 失败时沿用上一轮结果（名称短时间过期无害），返回 false 由调用方节流重试。
    /// x64 结构偏移见 Native.NtQuerySystemInformation 注释。
    /// </summary>
    private bool RefreshNt()
    {
        for (int attempt = 0; ; attempt++)
        {
            uint rc = Native.NtQuerySystemInformation(Native.SystemProcessInformation, _ntBuf, _ntCap, out uint needed);
            if (rc == Native.STATUS_INFO_LENGTH_MISMATCH && needed > _ntCap && attempt < 8)
            {
                uint want = Math.Max(needed, _ntCap * 2);
                if (_ntBuf != IntPtr.Zero) Marshal.FreeHGlobal(_ntBuf);
                _ntBuf = Marshal.AllocHGlobal((int)want);
                _ntCap = want;
                continue;
            }
            if (rc != 0) return false;
            break;
        }

        _nt.Clear();
        long off = 0;
        // 读取到 SessionId（+0x64）需要完整 0x68 字节；用 0x60 会把末尾 8 字节读出缓冲。
        while (off + 0x68 <= _ntCap)
        {
            int pid = (int)Marshal.ReadIntPtr(_ntBuf, (int)(off + 0x50));
            if (pid > 0)
            {
                short nlen = Marshal.ReadInt16(_ntBuf, (int)(off + 0x38));
                string? name = null;
                if (nlen > 0)
                {
                    IntPtr nptr = Marshal.ReadIntPtr(_ntBuf, (int)(off + 0x40));
                    if (nptr != IntPtr.Zero)
                        name = Marshal.PtrToStringUni(nptr, nlen / 2);
                }
                _nt[pid] = new NtProc
                {
                    Name = name,
                    Parent = (int)Marshal.ReadIntPtr(_ntBuf, (int)(off + 0x58)),
                    Session = Marshal.ReadInt32(_ntBuf, (int)(off + 0x64))
                };
            }
            uint next = (uint)Marshal.ReadInt32(_ntBuf, (int)off);
            if (next == 0) break;
            off += next;
        }
        return true;
    }

    /// <summary>慢信息：完整路径（TTL 15 秒，失败也缓存同样时长，避免每帧重试）、名称、架构（一次性）、token owner（一次性）。</summary>
    private void RefreshSlow(Entry e, int pid, long nowMs)
    {
        var m = e.M;

        if (nowMs >= e.PathAt)
        {
            e.PathAt = nowMs + SlowInfoTtlMs;
            if (e.Handle != IntPtr.Zero)
            {
                _sb.Clear();
                uint size = (uint)_sb.Capacity;
                if (Native.QueryFullProcessImageName(e.Handle, 0, _sb, ref size) && size > 0)
                {
                    m.Path = _sb.ToString(0, (int)size);
                    string name = Path.GetFileName(m.Path);
                    m.Name = name.Length > 0 ? name : m.Path;
                    m.AccessLimited = false;
                }
                else
                {
                    // 打得开句柄但读不到路径（多为提权 / PPL）：标受限，名称交给 NtQuery 镜像名
                    m.AccessLimited = true;
                }
            }
            else
            {
                m.AccessLimited = true;
            }
        }

        if (m.Name.Length == 0 && _nt.TryGetValue(pid, out var np) && np.Name is { Length: > 0 } nn)
            m.Name = nn;

        if (m.ParentPid == 0 && _nt.TryGetValue(pid, out var np2))
        {
            m.ParentPid = np2.Parent;
            m.SessionId = np2.Session;
        }

        if (!e.OwnerTried && e.Handle != IntPtr.Zero)
        {
            e.OwnerTried = true;
            if (TryReadOwnerSid(e.Handle, out string? sid)) m.OwnerSid = sid;
        }
        m.IsSystemService = IsSystemServiceSid(m.OwnerSid) || m.SessionId == 0;

        if (!e.MachineTried && e.Handle != IntPtr.Zero)
        {
            e.MachineTried = true;
            if (Native.IsWow64Process2(e.Handle, out ushort pm, out ushort nm))
            {
                ushort eff = pm == 0 ? nm : pm;
                m.Architecture = eff switch
                {
                    MachineX86 => "x86",
                    MachineArm64 => "ARM64",
                    MachineX64 => "x64",
                    _ => null
                };
            }
        }
    }

    /// <summary>token user SID。QUERY_LIMITED 句柄即可；打不开（PPL / 内核项）返回 false，判定回退名称黑名单。</summary>
    private static bool TryReadOwnerSid(IntPtr hProcess, out string? sid)
    {
        sid = null;
        if (!Native.OpenProcessToken(hProcess, Native.TOKEN_QUERY, out IntPtr tok)) return false;
        try
        {
            // 第一次调用探长度：返回 false（ERROR_INSUFFICIENT_BUFFER）且 ReturnLength>0 才是正常路径，
            // bool 结果不能当失败依据（首轮 probe 因此全军覆没 ownerKnown=0）
            Native.GetTokenInformation(tok, Native.TokenUser, IntPtr.Zero, 0, out uint len);
            if (len == 0 || len > 4096) return false;
            IntPtr buf = Marshal.AllocHGlobal((int)len);
            try
            {
                if (!Native.GetTokenInformation(tok, Native.TokenUser, buf, len, out _)) return false;
                IntPtr psid = Marshal.ReadIntPtr(buf);       // TOKEN_USER.User.Sid（x64 偏移 0）
                if (psid == IntPtr.Zero || !Native.ConvertSidToStringSid(psid, out IntPtr str)) return false;
                try { sid = Marshal.PtrToStringUni(str); }
                finally { Native.LocalFree(str); }
                return sid != null;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { Native.CloseHandle(tok); }
    }

    /// <summary>
    /// SID 前缀判定（不调 LookupAccountSid：免本地化映射与 LSASS 往返）：
    /// S-1-5-18 SYSTEM、S-1-5-19 LOCAL SERVICE、S-1-5-20 NETWORK SERVICE（精确匹配），
    /// S-1-5-80-* 每服务虚拟账户（svchost 组）、S-1-5-96-* 窗口管理器（DWM / UMFD）。
    /// 用户账户 S-1-5-21-*、UWP S-1-12-*、提权用户都不在此列。
    /// </summary>
    public static bool IsSystemServiceSid(string? sid) => sid switch
    {
        "S-1-5-18" or "S-1-5-19" or "S-1-5-20" => true,
        string s when s.StartsWith("S-1-5-80-", StringComparison.Ordinal) => true,
        string s when s.StartsWith("S-1-5-96-", StringComparison.Ordinal) => true,
        _ => false
    };

    private void FillWindow(ProcessMetric m)
    {
        if (_windows.Info.TryGetValue(m.Pid, out var w))
        {
            m.HasVisibleWindow = true;
            m.HasMainWindow = w.HasMainWindow;
            m.WindowTitle = w.Title;
            m.IsForeground = w.Foreground;
        }
        else
        {
            m.HasVisibleWindow = false;
            m.HasMainWindow = false;
            m.IsForeground = false;
            m.WindowTitle = null;
        }
    }

    /// <summary>本帧没出现的 PID 移入保留区；保留区到期（12 秒）的关句柄、释放。</summary>
    private void ReapDead(long nowMs)
    {
        List<int>? dead = null;
        foreach (var (pid, e) in _live)
            if (e.LastSeen != nowMs)
                (dead ??= new List<int>()).Add(pid);

        if (dead != null)
        {
            foreach (int pid in dead)
            {
                var e = _live[pid];
                _live.Remove(pid);
                if (e.Handle != IntPtr.Zero)
                    _graveyard[pid] = new Grave { E = e, ExpiresAt = nowMs + GraveyardTtlMs };
            }
        }

        if (_graveyard.Count > 0)
        {
            List<int>? gone = null;
            foreach (var (pid, g) in _graveyard)
                if (nowMs >= g.ExpiresAt)
                    (gone ??= new List<int>()).Add(pid);

            if (gone != null)
                foreach (int pid in gone)
                    if (_graveyard.Remove(pid, out var g) && g.E.Handle != IntPtr.Zero)
                        Native.CloseHandle(g.E.Handle);
        }
    }

    /// <summary>扩容循环：EnumProcesses 永不报「缓冲太小」，只会截断 —— 用 needed ≤ 缓冲判断有没有截断。</summary>
    private int ReadPids()
    {
        while (true)
        {
            if (Native.K32EnumProcesses(_pids, (uint)(_pids.Length * sizeof(uint)), out uint needed) &&
                needed <= (uint)_pids.Length * sizeof(uint))
                return (int)(needed / sizeof(uint));
            _pids = new uint[_pids.Length * 2];
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var e in _live.Values)
            if (e.Handle != IntPtr.Zero) Native.CloseHandle(e.Handle);
        _live.Clear();
        foreach (var g in _graveyard.Values)
            if (g.E.Handle != IntPtr.Zero) Native.CloseHandle(g.E.Handle);
        _graveyard.Clear();
        if (_ntBuf != IntPtr.Zero) { Marshal.FreeHGlobal(_ntBuf); _ntBuf = IntPtr.Zero; _ntCap = 0; }
    }
}

/// <summary>
/// 每帧一次 EnumWindows：可见 + 有标题 + 非工具窗 + 未被 DWM cloak 的顶层窗口，
/// 记录 归属 PID → { 主窗口标题, 是否前台, 是否应用主窗 }。
/// 阶段 3：每个 PID 按窗口面积选「主窗口」（可见 + 非工具 + 未 cloak 里面积最大者），
/// 标题取主窗口 —— z 序首个可能是子窗口（如浏览器的 DevTools / 浮窗）；
/// 无 owner 或 WS_EX_APPWINDOW 才算应用主窗（区分出模态子窗 / 调色板）。
/// </summary>
internal sealed class WindowScanner
{
    public sealed class WinInfo
    {
        public string? Title;
        public bool Foreground;
        public bool HasMainWindow;
        public int MainArea;            // 当前主窗口面积，后续窗口与之比较
    }

    public readonly Dictionary<int, WinInfo> Info = new(64);

    private readonly Native.EnumWindowsProc _proc;
    private readonly StringBuilder _sb = new(256);

    public WindowScanner() => _proc = OnWindow;

    public void Scan()
    {
        Info.Clear();
        Native.EnumWindows(_proc, IntPtr.Zero);

        IntPtr fg = Native.GetForegroundWindow();
        if (fg != IntPtr.Zero && Native.GetWindowThreadProcessId(fg, out uint fgPid) != 0 && fgPid != 0)
        {
            if (!Info.TryGetValue((int)fgPid, out var wi))
                Info[(int)fgPid] = wi = new WinInfo();
            wi.Foreground = true;
        }
    }

    private bool OnWindow(IntPtr hWnd, IntPtr _)
    {
        if (!Native.IsWindowVisible(hWnd)) return true;
        int ex = Native.GetWindowLong(hWnd, Native.GWL_EXSTYLE);
        if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return true;
        int len = Native.GetWindowTextLength(hWnd);

        int cloaked = 0;
        Native.DwmGetWindowAttribute(hWnd, Native.DWMWA_CLOAKED, ref cloaked, sizeof(int));
        if (cloaked != 0) return true;              // UWP 挂起宿主：看不见不算

        if (Native.GetWindowThreadProcessId(hWnd, out uint pid) == 0 || pid == 0) return true;

        // 主窗口候选：无 owner（GW_OWNER）或 WS_EX_APPWINDOW 的窗口才算应用主窗，
        // 面积最大的胜出 —— 调色板 / 模态子窗 / 工具窗让位
        bool appWindow = (ex & Native.WS_EX_APPWINDOW) != 0 || Native.GetWindow(hWnd, Native.GW_OWNER) == IntPtr.Zero;
        int area = 0;
        if (appWindow && Native.GetWindowRect(hWnd, out var rc))
            area = rc.Width > 0 && rc.Height > 0 ? rc.Width * rc.Height : 0;

        if (!Info.TryGetValue((int)pid, out var wi))
            Info[(int)pid] = wi = new WinInfo();

        if (area > wi.MainArea)
        {
            wi.MainArea = area;
            wi.HasMainWindow = true;
            wi.Title = null;
            if (len > 0)
            {
                _sb.Clear();
                if (Native.GetWindowText(hWnd, _sb, _sb.Capacity) > 0)
                    wi.Title = _sb.ToString();
            }
        }
        else if (wi.Title == null && len > 0 && area == 0)
        {
            // 没有任何主窗候选时（纯后台 / 无标题主窗），退回首个有标题的窗口
            _sb.Clear();
            if (Native.GetWindowText(hWnd, _sb, _sb.Capacity) > 0)
                wi.Title = _sb.ToString();
        }
        return true;
    }
}
