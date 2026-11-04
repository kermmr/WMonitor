using System.Diagnostics;
using System.Runtime.InteropServices;
using WMonitor.Interop;

namespace WMonitor.Metrics;

/// <summary>单个 PID 一帧的 GPU 指标（GPU 视图一行 / 合入 ProcessMetric 的来源）。</summary>
public sealed class GpuProcessMetric
{
    public int Pid;

    /// <summary>主适配器键（引擎 util 或专用显存最大的 LUID+phys），格式与 MetricSampler 的适配器键一致：
    /// 0x00000000_0x00010FB0_phys_0。多卡进程在快照里已按 PID 合并，此字段标记「主要跑在哪张卡」。</summary>
    public string? Luid;

    /// <summary>GPU 利用率（3 帧滑动平均，跨全部引擎类型与适配器求和后归一到 100%）。</summary>
    public double UtilPercent;

    public long DedicatedBytes;
    public long SharedBytes;

    /// <summary>当帧利用率最高的引擎类型原文（3D / Copy / VideoDecode / Cuda …），详情区显示。</summary>
    public string? TopEngine;

    /// <summary>活跃 (pid,luid,eng) 实例数（诊断用）。</summary>
    public int EngineCount;
}

/// <summary>
/// 单卡一行的 GPU 进程指标（阶段 3 多卡筛选用）：按 (pid,luid) 原始聚合，不做 PID 合行。
/// Luid 归一为小写 —— GPU Engine 实例小写、GPU Process Memory 大写两族（docs/12 §5.4），
/// 比较一律 OrdinalIgnoreCase。
/// </summary>
public sealed class GpuLuidMetric
{
    public int Pid;

    /// <summary>适配器键（含 phys 段），小写。下拉分组用 <see cref="GpuProcessSampler.LuidBase"/> 去掉 phys。</summary>
    public string Luid = "";

    public double UtilPercent;            // 3 帧滑动平均（仅该卡）
    public long DedicatedBytes;           // 该卡专用显存
    public long SharedBytes;              // 共享显存按 pid 只有一份，挂在 util 最高的卡行
}

/// <summary>GpuProcessSampler 一帧输出。只读快照，UI / Hub 不修改；下一帧 Sample() 后 Rows 失效。</summary>
public sealed class GpuProcessSnapshot
{
    public List<GpuProcessMetric> Rows = new();

    /// <summary>per-(pid,luid) 行（阶段 3 多卡筛选用）。与 Rows 同一刷新周期，多卡进程在这里仍是多行。</summary>
    public List<GpuLuidMetric> Luids = new();

    /// <summary>GPU Engine 计数器是否可用（false = 无 GPU / 驱动不支持 / PDH 缺计数器）。</summary>
    public bool Available;

    /// <summary>最近一次降级原因（collect 失败 / 计数器缺失；null = 正常）。</summary>
    public string? Error;

    /// <summary>本帧 GPU Engine 实例数（诊断用）。</summary>
    public int EngineInstances;

    public double SampleMs;
    public DateTime At;
}

/// <summary>
/// GPU 进程级采样（plan §3.2）：独立 PDH query 读
/// \GPU Engine(*)\Utilization Percentage 与 \GPU Process Memory(*)\Dedicated/Shared Usage，
/// 解析实例名 pid_N_luid_..._phys_N_eng_N_engtype_X，按 PID+LUID 聚合、利用率 3 帧滑动平均。
///
/// 预算 ≤ 8 ms/帧（目标 ≤5ms），0.5 Hz 子采样（调度在 AppMetricsHub，两次采样间沿用旧值）。
/// 不与 MetricSampler 共享 query —— GPU Engine 实例集每帧都在变（进程启停），
/// 共享会引入扩容与 dispose 竞争（plan §10.6）。
///
/// 连续 3 帧超过 12 ms 时 <see cref="Overloaded"/> 置真，Hub 自动拉长子采样间隔（plan §5 降级）。
/// 只在 UI 线程调用，无锁。
/// </summary>
public sealed class GpuProcessSampler : IDisposable
{
    private IntPtr _query;
    private IntPtr _cEngine, _cDedicated, _cShared;
    private IntPtr _buf;                 // 三个计数器顺序复用的 grow-only 格式化数组缓冲
    private uint _bufCap;
    private uint _itemCount;             // 最近一次 ReadArray 的条目数
    private bool _disposed;

    /// <summary>3 帧滑动平均槽位。未写入的槽为 0，均值按有效帧数 N 计算。</summary>
    private sealed class Stat
    {
        public readonly double[] U = new double[3];
        public int Head;
        public int N;
        public long LastFrame;           // 帧计数器，过期键清理用

        public double Push(double v)
        {
            U[Head] = v;
            Head = (Head + 1) % U.Length;
            if (N < U.Length) N++;
            return (U[0] + U[1] + U[2]) / N;
        }
    }

    /// <summary>滑动平均状态，键 "pid|luidphys"（无 luid 段的旧式实例用 "pid|"）。</summary>
    private readonly Dictionary<string, Stat> _stats = new(64);
    private long _frameNo;
    private int _slowStreak;

    private static readonly int ItemSize = Marshal.SizeOf<Native.PDH_FMT_COUNTERVALUE_ITEM>();

    public bool Available { get; private set; }

    /// <summary>GPU Process Memory 的 Shared Usage 计数器缺失（个别精简驱动只有 Dedicated）。</summary>
    public bool SharedUnavailable { get; private set; }

    /// <summary>最近一次降级原因（供 UI 状态行 / probe 显示；正常为 null）。</summary>
    public string? Error { get; private set; }

    /// <summary>本帧采样耗时（毫秒）。预算 ≤8ms（plan §5）。</summary>
    public double LastSampleMs { get; private set; }

    /// <summary>连续 3 帧超 12ms（plan §5 的降级线）——Hub 看到后把子采样间隔拉长 2.5 倍。</summary>
    public bool Overloaded => _slowStreak >= 3;

    private readonly GpuProcessSnapshot _snap = new();

    public GpuProcessSampler()
    {
        uint r = Native.PdhOpenQuery(null, IntPtr.Zero, out _query);
        if (r != 0)
        {
            Error = $"PdhOpenQuery failed: 0x{r:X}";
            return;
        }

        _cEngine = AddCounter(@"\GPU Engine(*)\Utilization Percentage");
        _cDedicated = AddCounter(@"\GPU Process Memory(*)\Dedicated Usage");
        _cShared = AddCounter(@"\GPU Process Memory(*)\Shared Usage");

        if (_cEngine == IntPtr.Zero || _cDedicated == IntPtr.Zero)
        {
            Error = "GPU Engine / GPU Process Memory 计数器不可用（无 GPU / 驱动不支持）";
            Native.PdhCloseQuery(_query);
            _query = IntPtr.Zero;
            return;
        }
        Available = true;
        if (_cShared == IntPtr.Zero) SharedUnavailable = true;

        // 预热一次：利用率是两次采样间的差分值，首帧才有效
        Native.PdhCollectQueryData(_query);
    }

    public GpuProcessSnapshot Sample()
    {
        if (_disposed) return _snap;
        var sw = Stopwatch.StartNew();

        if (!Available)
        {
            LastSampleMs = 0;
            _snap.Rows.Clear();
            _snap.Luids.Clear();
            _snap.Available = false;
            _snap.Error = Error ?? "GPU 进程数据不可用";
            _snap.SampleMs = 0;
            _snap.At = DateTime.Now;
            return _snap;
        }

        uint rc = Native.PdhCollectQueryData(_query);
        if (rc != 0)
        {
            // collect 失败：保留上一帧数据（旧值沿用），标注降级原因
            _snap.Error = Error = $"PdhCollectQueryData failed: 0x{rc:X}";
            _snap.SampleMs = LastSampleMs = sw.Elapsed.TotalMilliseconds;
            _snap.At = DateTime.Now;
            return _snap;
        }

        _frameNo++;

        // ---- GPU Engine：按 (pid|luid) 求和 util，记录当帧利用率最高的引擎类型 ----
        // (Util, TopEng, TopVal, EngCount)：TopVal 单独记最高引擎的数值，不能拿累加和当比较基准
        var engine = new Dictionary<string, (double Util, string? TopEng, double TopVal, int EngCount)>(64);
        int engineInstances = 0;
        if (ReadArray(_cEngine))
        {
            var items = EnumerateItems();
            engineInstances = items.Count;
            for (int i = 0; i < items.Count; i++)
            {
                var (name, value) = items[i];
                if (double.IsNaN(value)) continue;
                if (!TrySplitEngine(name, out int pid, out ReadOnlySpan<char> luid, out ReadOnlySpan<char> engtype))
                    continue;

                string key = string.Concat(pid.ToString(), "|", luid);
                string? eng = engtype.Length > 0 ? new string(engtype) : null;
                if (!engine.TryGetValue(key, out var cur))
                    engine[key] = (value, eng, value, 1);
                else
                {
                    string? top = cur.TopEng;
                    double topVal = cur.TopVal;
                    if (eng != null && value > topVal) { top = eng; topVal = value; }
                    engine[key] = (cur.Util + value, top, topVal, cur.EngCount + 1);
                }
            }
        }

        // ---- GPU Process Memory：按 pid 汇总专用 / 共享显存；主适配器另存 (pid|luid) 键 ----
        var dedByPid = new Dictionary<int, long>(32);
        var sharedByPid = new Dictionary<int, long>(32);
        var dedByPidLuid = new Dictionary<string, long>(32);
        var luidByPid = new Dictionary<int, string?>(32);
        if (ReadArray(_cDedicated))
            foreach (var (name, value) in EnumerateItems())
                AccumulateMemory(name, value, dedByPid, dedByPidLuid, luidByPid);
        if (!SharedUnavailable && ReadArray(_cShared))
            foreach (var (name, value) in EnumerateItems())
                AccumulateMemory(name, value, sharedByPid, null, null);

        // ---- 合行：按 PID 一行（多卡进程 sum util，主适配器取单卡 util 最高者）----
        var byPid = new Dictionary<int, GpuProcessMetric>(engine.Count + 8);
        var luidUtil = new Dictionary<int, double>(engine.Count + 8);   // 该 pid 主适配器行的单卡 util
        var rows = new List<GpuProcessMetric>(engine.Count + 8);

        // per-(pid,luid) 行（阶段 3 多卡筛选）：luid 归一小写（GPU Engine 小写 / GPU Process Memory
        // 大写两族，docs/12 §5.4）；旧式无 luid 段实例不进 per-LUID 列表
        var luidRows = new List<GpuLuidMetric>(engine.Count + 8);
        var luidByKey = new Dictionary<string, GpuLuidMetric>(engine.Count + 8);

        foreach (var (key, e) in engine)
        {
            int sep = key.IndexOf('|');
            int pid = int.Parse(key.AsSpan(0, sep));
            string luid = key[(sep + 1)..];
            string? luidKey = luid.Length > 0 ? luid : (luidByPid.TryGetValue(pid, out var l) ? l : null);

            if (!_stats.TryGetValue(key, out var st))
                _stats[key] = st = new Stat();
            st.LastFrame = _frameNo;
            double util = Math.Clamp(st.Push(e.Util), 0, 100);

            if (luid.Length > 0)
            {
                string lk = string.Concat(pid.ToString(), "|", luid.ToLowerInvariant());
                if (!luidByKey.TryGetValue(lk, out var lr))
                {
                    luidByKey[lk] = lr = new GpuLuidMetric { Pid = pid, Luid = luid.ToLowerInvariant(), UtilPercent = util };
                    luidRows.Add(lr);
                }
                else
                {
                    lr.UtilPercent = Math.Clamp(lr.UtilPercent + util, 0, 100);
                }
            }

            if (!byPid.TryGetValue(pid, out var row))
            {
                byPid[pid] = row = new GpuProcessMetric
                {
                    Pid = pid,
                    Luid = luidKey,
                    UtilPercent = util,
                    DedicatedBytes = dedByPid.TryGetValue(pid, out long d) ? d : 0,
                    SharedBytes = sharedByPid.TryGetValue(pid, out long s) ? s : 0,
                    TopEngine = e.TopEng,
                    EngineCount = e.EngCount
                };
                luidUtil[pid] = e.Util;
                rows.Add(row);
            }
            else
            {
                row.UtilPercent = Math.Clamp(row.UtilPercent + util, 0, 100);
                row.EngineCount += e.EngCount;
                if (e.Util > luidUtil[pid])
                {
                    luidUtil[pid] = e.Util;
                    row.Luid = luidKey;
                    row.TopEngine = e.TopEng;
                }
            }
        }

        // 有专用显存占用但本帧无引擎实例的进程（CUDA 常驻 / 空闲 3D 上下文）：util 0、显存照报
        foreach (var (pid, ded) in dedByPid)
        {
            if (byPid.ContainsKey(pid) || ded <= 0) continue;
            var row = new GpuProcessMetric
            {
                Pid = pid,
                Luid = luidByPid.TryGetValue(pid, out var l) ? l : null,
                UtilPercent = 0,
                DedicatedBytes = ded,
                SharedBytes = sharedByPid.TryGetValue(pid, out long s) ? s : 0,
                TopEngine = null,
                EngineCount = 0
            };
            byPid[pid] = row;
            rows.Add(row);
        }

        // per-LUID 专用显存：GPU Process Memory 的 luid 段大写 → 归一小写与引擎行合流；
        // 仅有显存、无引擎活动的 (pid,luid) 也出行（Ardot 这类常驻显存进程筛选时可见）
        foreach (var (key, bytes) in dedByPidLuid)
        {
            if (bytes <= 0) continue;
            int sep = key.IndexOf('|');
            if (sep <= 0) continue;
            int pid = int.Parse(key.AsSpan(0, sep));
            string luidRaw = key[(sep + 1)..];
            if (luidRaw.Length == 0) continue;
            string lk = string.Concat(pid.ToString(), "|", luidRaw.ToLowerInvariant());
            if (!luidByKey.TryGetValue(lk, out var lr))
            {
                luidByKey[lk] = lr = new GpuLuidMetric { Pid = pid, Luid = luidRaw.ToLowerInvariant() };
                luidRows.Add(lr);
            }
            lr.DedicatedBytes += bytes;
        }

        // 共享显存按 pid 只有一份（实例 luid 段不参与聚合）→ 挂到该 pid util 最高的卡行
        foreach (var (pid, shr) in sharedByPid)
        {
            if (shr <= 0) continue;
            GpuLuidMetric? best = null;
            foreach (var lr in luidRows)
                if (lr.Pid == pid && (best == null || lr.UtilPercent > best.UtilPercent)) best = lr;
            if (best != null) best.SharedBytes = shr;
        }

        _snap.Luids = luidRows;

        PruneStats();
        _slowStreak = sw.Elapsed.TotalMilliseconds > 12 ? _slowStreak + 1 : 0;

        _snap.Rows = rows;
        _snap.Available = true;
        _snap.Error = null;
        _snap.EngineInstances = engineInstances;
        _snap.SampleMs = LastSampleMs = sw.Elapsed.TotalMilliseconds;
        _snap.At = DateTime.Now;
        return _snap;
    }

    // ---------------- 实例名解析 ----------------

    /// <summary>"0x00000000_0x00010fb0_phys_0" → "0x00000000_0x00010fb0"（下拉按物理适配器分组）。
    /// 输入应为归一小写（GpuLuidMetric.Luid），分组比较仍用 OrdinalIgnoreCase 兜底。</summary>
    public static string LuidBase(string luid)
    {
        int i = luid.IndexOf("_phys_", StringComparison.OrdinalIgnoreCase);
        return i > 0 ? luid[..i] : luid;
    }

    /// <summary>
    /// pid_1208_luid_0x00000000_0x00010FB0_phys_0_eng_0_engtype_3D
    ///   → pid=1208, luid="0x00000000_0x00010FB0_phys_0", engtype="3D"
    /// 旧式 GPU Process Memory 实例没有 luid 段：pid_1208_phys_0_eng_0_engtype_3D → luid=""。
    /// </summary>
    private static bool TrySplitEngine(string name, out int pid, out ReadOnlySpan<char> luid, out ReadOnlySpan<char> engtype)
    {
        pid = 0; luid = default; engtype = default;
        if (!name.StartsWith("pid_", StringComparison.Ordinal)) return false;
        int end = 4;
        while (end < name.Length && name[end] >= '0' && name[end] <= '9') end++;
        if (end == 4 || !int.TryParse(name.AsSpan(4, end - 4), out pid) || pid <= 0) return false;

        int li = name.IndexOf("luid_", end, StringComparison.Ordinal);
        if (li >= 0)
        {
            int ls = li + 5;
            int le = name.IndexOf("_eng_", ls, StringComparison.Ordinal);
            if (le < 0) le = name.Length;
            luid = name.AsSpan(ls, le - ls).TrimEnd('_');
        }

        int ei = name.LastIndexOf("engtype_", StringComparison.Ordinal);
        if (ei >= 0) engtype = name.AsSpan(ei + 8);
        return true;
    }

    /// <summary>GPU Process Memory 实例 → 按 pid 累加字节；（可选）按 pid|luid 另存一份取主适配器。</summary>
    private static void AccumulateMemory(
        string name, double bytes,
        Dictionary<int, long> byPid,
        Dictionary<string, long>? byPidLuid,
        Dictionary<int, string?>? luidByPid)
    {
        if (!name.StartsWith("pid_", StringComparison.Ordinal)) return;
        int end = 4;
        while (end < name.Length && name[end] >= '0' && name[end] <= '9') end++;
        if (end == 4 || !int.TryParse(name.AsSpan(4, end - 4), out int pid) || pid <= 0) return;

        long v = double.IsNaN(bytes) ? 0 : (long)Math.Max(0, bytes);
        byPid[pid] = byPid.TryGetValue(pid, out long cur) ? cur + v : v;

        if (byPidLuid == null) return;
        int li = name.IndexOf("luid_", end, StringComparison.Ordinal);
        string luid = "";
        if (li >= 0)
        {
            int ls = li + 5;
            int le = name.IndexOf("_eng_", ls, StringComparison.Ordinal);
            if (le < 0) le = name.Length;
            luid = name[ls..le].TrimEnd('_');
        }
        string key = string.Concat(pid.ToString(), "|", luid);
        byPidLuid[key] = byPidLuid.TryGetValue(key, out long c2) ? c2 + v : v;
        if (luidByPid != null && luid.Length > 0)
            luidByPid[pid] = luid;
    }

    /// <summary>4 个 GPU 帧没再出现的键清掉（≈8 秒，防瞬时进程把字典撑大）。</summary>
    private void PruneStats()
    {
        if (_stats.Count <= 256) return;
        List<string>? gone = null;
        foreach (var (key, st) in _stats)
            if (_frameNo - st.LastFrame > 4)
                (gone ??= new List<string>()).Add(key);
        if (gone != null)
            foreach (var k in gone) _stats.Remove(k);
    }

    // ---------------- PDH 数组读取 ----------------

    private IntPtr AddCounter(string path)
    {
        uint r = Native.PdhAddEnglishCounter(_query, path, IntPtr.Zero, out IntPtr h);
        return r == 0 ? h : IntPtr.Zero;
    }

    /// <summary>
    /// 格式化数组读到共享 grow-only 缓冲。成功后 <see cref="_itemCount"/> 有效，
    /// 随即用 <see cref="EnumerateItems"/> 遍历（下一次 ReadArray 会覆盖缓冲，不能交叉）。
    /// </summary>
    private bool ReadArray(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return false;
        for (int attempt = 0; attempt < 3; attempt++)     // 实例集在两次调用间可能增长，允许重试
        {
            uint size = _bufCap;
            uint r = Native.PdhGetFormattedCounterArray(counter, Native.PDH_FMT_DOUBLE, ref size, out uint count, _buf);
            if (r == Native.PDH_MORE_DATA && size > _bufCap)
            {
                if (_buf != IntPtr.Zero) Marshal.FreeHGlobal(_buf);
                _buf = Marshal.AllocHGlobal((int)size);
                _bufCap = size;
                continue;
            }
            if (r == 0) { _itemCount = count; return true; }
            return false;
        }
        return false;
    }

    private readonly List<(string Name, double Value)> _items = new(128);

    private List<(string, double)> EnumerateItems()
    {
        var items = _items;
        items.Clear();
        for (int i = 0; i < _itemCount; i++)
        {
            var item = Marshal.PtrToStructure<Native.PDH_FMT_COUNTERVALUE_ITEM>(_buf + i * ItemSize);
            if (item.FmtValue.CStatus != 0 || item.szName == null) continue;
            items.Add((item.szName, item.FmtValue.doubleValue));
        }
        return items;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_query != IntPtr.Zero) { Native.PdhCloseQuery(_query); _query = IntPtr.Zero; }
        if (_buf != IntPtr.Zero) { Marshal.FreeHGlobal(_buf); _buf = IntPtr.Zero; _bufCap = 0; }
    }
}
