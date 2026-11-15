using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using WMonitor.Interop;

namespace WMonitor.Metrics;

public sealed class MetricsSnapshot
{
    public double CpuPercent;
    public double CpuGhz;
    public ulong RamTotalBytes;
    public ulong RamUsedBytes;
    public double GpuPercent;
    public ulong VramUsedBytes;
    public ulong VramTotalBytes;
    public double GpuTempC = double.NaN;
    public double GpuPowerW = double.NaN;
    public double NetDownBps;
    public double NetUpBps;

    /// <summary>磁盘读写（B/s）。步骤 5 才由 PDH 填充，之前恒为 0。</summary>
    public double DiskReadBps;
    public double DiskWriteBps;

    /// <summary>网速统计的接口名（v0.35 网速分接口）。空 = 全部物理网卡加总；非空 = 锁定了单块网卡
    /// （托盘菜单「网速接口」），详情面板网速行用它显示来源。</summary>
    public string NetSource = "";

    /// <summary>各适配器的专用显存占用（键 = NormalizeInstance 归一键；v0.35）。
    /// AppMetricsHub 拿它给进程级 Dedicated Usage 钳上限 —— 610.47 驱动的进程级计数器
    /// 会报超过卡总量的垃圾值（实测 12GB 卡上报 109GB，Get-Counter 直读原始值一致）。
    /// 比较一律 OrdinalIgnoreCase（docs/12 §5.4：engine 小写 / memory 大写两族，别信单侧归一）。</summary>
    public Dictionary<string, ulong> VramByLuid = new(StringComparer.OrdinalIgnoreCase);
    public string GpuName = "GPU";
    public bool HasGpu;
    public bool HasVramTotal;
    public bool HasTemp;

    public double RamPercent => RamTotalBytes == 0 ? 0 : RamUsedBytes * 100.0 / RamTotalBytes;
    public double VramPercent => VramTotalBytes == 0 ? 0 : VramUsedBytes * 100.0 / VramTotalBytes;
    public double VramGb => VramUsedBytes / 1024.0 / 1024 / 1024;
    public double VramTotalGb => VramTotalBytes / 1024.0 / 1024 / 1024;
    public double RamUsedGb => RamUsedBytes / 1024.0 / 1024 / 1024;
    public double RamTotalGb => RamTotalBytes / 1024.0 / 1024 / 1024;
}

/// <summary>
/// 一次 PDH 查询挂多个计数器：CPU 占用 / CPU 频率比 / GPU Engine(通配) / GPU Adapter Memory(通配)。
/// 采样在后台线程，1 Hz 时 CPU 开销约 0.05%。
/// </summary>
public sealed class MetricSampler : IDisposable
{
    private IntPtr _query;
    private IntPtr _gpuQuery;
    private IntPtr _cCpu, _cCpuFreq, _cGpuEngine, _cGpuMem, _cDiskRead, _cDiskWrite;
    private bool _disposed;

    private NvmlGpu? _nvml;
    private ulong _lastDown, _lastUp;
    private long _lastNetTicks;
    private string? _lockedLuid;
    private bool _lockMissLogged;
    private ulong? _lockFull64;
    private ulong _vramTotalFallback;

    /// <summary>网速统计锁定的网卡（NetworkInterface.Id，空 = 全部物理网卡加总）。
    /// 设置入口：托盘菜单「网速接口」；MonitorAppContext 在启动与 RefreshAll 时同步进来。</summary>
    public string NetInterfaceId { get; set; } = "";

    /// <summary>锁定接口当前的显示名（面板网速行展示）。未锁定或接口不在了为 null。</summary>
    public string? NetInterfaceName { get; private set; }
    private bool _netSelMissLogged;

    // 高频分配的结构只算一次
    private static readonly int ItemSize = Marshal.SizeOf<Native.PDH_FMT_COUNTERVALUE_ITEM>();

    public MetricSampler()
    {
        uint r = Native.PdhOpenQuery(null, IntPtr.Zero, out _query);
        if (r != 0) throw new InvalidOperationException($"PdhOpenQuery failed: 0x{r:X}");

        _cCpu = Add(@"\Processor Information(_Total)\% Processor Utility");
        _cCpuFreq = Add(@"\Processor Information(_Total)\% Processor Performance");
        _cGpuMem = Add(@"\GPU Adapter Memory(*)\Dedicated Usage");
        _cDiskRead = Add(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
        _cDiskWrite = Add(@"\PhysicalDisk(_Total)\Disk Write Bytes/sec");

        _vramTotalFallback = ReadVramTotalFromRegistry();
        try { _nvml = NvmlGpu.TryCreate(); } catch { _nvml = null; }

        // GPU Engine 有数百个实例，collect 一次要 10ms。只有没有 NVML 时才需要它，
        // 而且挂在独立 query 上，不拖慢主采样。
        if (_nvml == null || !_nvml.HasUtil)
        {
            if (Native.PdhOpenQuery(null, IntPtr.Zero, out _gpuQuery) == 0)
            {
                uint rg = Native.PdhAddEnglishCounter(_gpuQuery,
                    @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _cGpuEngine);
                if (rg != 0) { Native.PdhCloseQuery(_gpuQuery); _gpuQuery = IntPtr.Zero; _cGpuEngine = IntPtr.Zero; }
            }
        }

        // 预热一次：多数 PDH 计数器要两次采样才有效
        Native.PdhCollectQueryData(_query);
        Thread.Sleep(120);
        Native.PdhCollectQueryData(_query);


        var (d, u, t) = ReadNetTotals();
        _lastDown = d; _lastUp = u; _lastNetTicks = t;
    }

    /// <summary>
    /// 锁定贴片显示哪块显卡（PDH 实例键，如 "0x00000000_0x00010FB0_phys_0"；null = 自动选占用最大的）。
    /// v0.30：顺带把 NVML 活动设备切到同一张卡 —— 否则多卡机器锁第二张卡时，
    /// 显存是第二张的而利用率/温度还是设备 0 的，口径错位。
    /// </summary>
    /// <summary>NVML 的 LUID → 设备名（无 NVML / 旧驱动无 nvmlDeviceGetLuid 时为 null）。GpuLocator 命名兜底。</summary>
    public IReadOnlyDictionary<ulong, string>? GpuNamesByLuid => _nvml?.NameByLuid;

    /// <summary>
    /// 锁定贴片显示哪块显卡（PDH 实例键，如 "luid_0x00000000_0x00010FB0_phys_0"；null = 自动选占用最大的）。
    /// v0.30：顺带把 NVML 活动设备切到同一张卡（要 nvmlDeviceGetLuid；实测 610.47 驱动没有 ——
    /// 那种机器锁定时利用率/温度改走 PDH Engine 按 LUID 聚合，见 <see cref="NvmlCoversLock"/>）。
    /// </summary>
    public void LockGpu(string? luid)
    {
        _lockedLuid = string.IsNullOrWhiteSpace(luid) ? null : luid;
        _lockMissLogged = false;
        _lockFull64 = null;
        ulong? full = null;
        if (_lockedLuid != null)
        {
            var (hi, lo) = GpuLocator.ParseLuid(_lockedLuid);
            if (hi != 0 || lo != 0) { _lockFull64 = ((uint)hi << 32) | lo; full = _lockFull64; }
        }
        _nvml?.SelectByLuid(full);
        EnsureGpuEngineQuery();
    }

    /// <summary>
    /// 锁定但 NVML 无法按 LUID 取数（无 NVML 或旧驱动）时才需要 GPU Engine 查询 ——
    /// 数百实例 collect 一次 ~10ms（docs/05 §8），所以平时根本不建：锁定即用即建，解锁即关。
    /// </summary>
    private void EnsureGpuEngineQuery()
    {
        // 无 NVML 时 engine query 本来就常驻（构造器建的那份，自动选卡也要靠它聚合利用率）；
        // 有 NVML 时只在「锁了卡且 NVML 认不出这张卡」的窗口期临时存在。
        bool nvmlUsable = _nvml != null && _nvml.HasUtil;
        bool need = !nvmlUsable || (_lockedLuid != null && !NvmlCoversLock());
        if (need && _gpuQuery == IntPtr.Zero)
        {
            if (Native.PdhOpenQuery(null, IntPtr.Zero, out _gpuQuery) == 0)
            {
                uint rg = Native.PdhAddEnglishCounter(_gpuQuery,
                    @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _cGpuEngine);
                if (rg != 0) { Native.PdhCloseQuery(_gpuQuery); _gpuQuery = IntPtr.Zero; _cGpuEngine = IntPtr.Zero; }
            }
        }
        else if (!need && _gpuQuery != IntPtr.Zero)
        {
            Native.PdhCloseQuery(_gpuQuery);
            _gpuQuery = IntPtr.Zero;
            _cGpuEngine = IntPtr.Zero;
        }
    }

    /// <summary>NVML 能否给出这张锁定卡的数（无锁 = true —— NVML 设备口径本来就是全局的）。</summary>
    private bool NvmlCoversLock()
    {
        if (_nvml == null || !_nvml.HasUtil) return false;
        return _lockedLuid == null || (_lockFull64 != null && _nvml.CoversLuid(_lockFull64.Value));
    }

    private IntPtr Add(string path)
    {
        uint r = Native.PdhAddEnglishCounter(_query, path, IntPtr.Zero, out IntPtr h);
        return r == 0 ? h : IntPtr.Zero;
    }

    public MetricsSnapshot Sample()
    {
        var s = new MetricsSnapshot();

        if (_query != IntPtr.Zero) Native.PdhCollectQueryData(_query);

        // ---- CPU ----
        double cpu = ReadSingle(_cCpu);
        s.CpuPercent = double.IsNaN(cpu) ? 0 : Math.Clamp(cpu, 0, 100);

        double freqRatio = ReadSingle(_cCpuFreq);
        double baseGhz = CpuBaseGhz;
        s.CpuGhz = double.IsNaN(freqRatio) ? baseGhz : baseGhz * freqRatio / 100.0;

        // ---- RAM ----
        var mem = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (Native.GlobalMemoryStatusEx(ref mem))
        {
            s.RamTotalBytes = mem.ullTotalPhys;
            s.RamUsedBytes = mem.ullTotalPhys - mem.ullAvailPhys;
        }

        // ---- GPU ----
        // 实例键形如 luid_0x00000000_0x00010FB0_phys_0（实测格式，含 luid_ 前缀）；LockedGpuLuid 存的就是它
        var gpuMem = ReadArray(_cGpuMem);
        s.VramByLuid.Clear();
        foreach (var (k, v) in gpuMem)
            if (v > 0) s.VramByLuid[k] = (ulong)Math.Max(0, v);   // 查找用 OrdinalIgnoreCase 字典,原键直存
        string? target = _lockedLuid;
        ulong best = 0;
        bool lockedFound = false;
        if (target != null)
        {
            foreach (var (k, v) in gpuMem)
                if (string.Equals(k, target, StringComparison.OrdinalIgnoreCase))
                { best = (ulong)v; lockedFound = true; break; }

            // 锁的卡不在了（拔卡 / 驱动重装 LUID 变化）：回落自动选，别把显存冻在 0。
            // 只记一次日志，别每秒刷。
            if (!lockedFound)
            {
                if (!_lockMissLogged)
                {
                    _lockMissLogged = true;
                    Dbg.Log($"locked gpu key missing, fallback to auto: {_lockedLuid}");
                }
                target = null;
            }
        }
        if (target == null)
        {
            foreach (var (k, v) in gpuMem)
                if (v > best) { best = (ulong)v; target = k; }
        }

        if (target != null)
        {
            s.HasGpu = true;
            s.VramUsedBytes = best;
            // 总量只在 NVML 确认是这张卡时才用（否则回落注册表 qwMemorySize —— 单值，第二张卡可能不准，
            // 但比张冠李戴强）；docs/05 §1：AdapterRAM 是 32 位字段，别用。
            ulong total = _nvml != null && NvmlCoversLock() ? _nvml.MemoryTotal : 0;
            if (total == 0) total = _vramTotalFallback;
            s.VramTotalBytes = total;
            s.HasVramTotal = total > 0;

            // N 卡优先用 NVML（微秒级）；锁定但 NVML 认不出这张卡时改走 PDH Engine 按 LUID 聚合
            //（旧驱动没有 nvmlDeviceGetLuid，NVML 只有设备 0 的数 —— 张冠李戴不如没有）。
            if (_nvml != null && _nvml.HasUtil && NvmlCoversLock())
            {
                s.GpuPercent = Math.Clamp(double.IsNaN(_nvml.GpuUtilPercent) ? 0 : _nvml.GpuUtilPercent, 0, 100);
            }
            else if (_gpuQuery != IntPtr.Zero && target != null)
            {
                Native.PdhCollectQueryData(_gpuQuery);
                double sum = 0;
                // gpuMem 与 engine 的键在 ReadArray 里都已被 NormalizeInstance 归一成 "0xH_0xL_phys_N"；
                // 按 LUID base 匹配（不吃 phys 段，锁 phys_0 也算上 phys_1 的引擎）。
                foreach (var (k, v) in ReadArray(_cGpuEngine))
                    if (string.Equals(GpuProcessSampler.LuidBase(k), GpuProcessSampler.LuidBase(target),
                            StringComparison.OrdinalIgnoreCase)) sum += v;
                s.GpuPercent = Math.Clamp(sum, 0, 100);
            }
        }

        // ---- NVML: 利用率 / 名字 / 温度 / 功耗 ----
        // 锁定但 NVML 认不出该卡时跳过：温度/功耗/名字都是设备 0 的，显示出来就是错的口径。
        if (_nvml != null && NvmlCoversLock())
        {
            _nvml.Refresh();
            s.GpuName = _nvml.Name;
            s.GpuTempC = _nvml.TemperatureC;
            s.GpuPowerW = _nvml.PowerW;
            s.HasTemp = !double.IsNaN(s.GpuTempC);
        }

        // ---- 网速 ----
        var (down, up, ticks) = ReadNetTotals();
        s.NetSource = NetInterfaceName ?? "";
        if (_lastNetTicks > 0 && ticks > _lastNetTicks)
        {
            double secs = (ticks - _lastNetTicks) / (double)Stopwatch.Frequency;
            if (secs > 0.05)
            {
                s.NetDownBps = Math.Max(0, down - _lastDown) / secs;
                s.NetUpBps = Math.Max(0, up - _lastUp) / secs;
            }
        }
        _lastDown = down; _lastUp = up; _lastNetTicks = ticks;

        // ---- 磁盘 IO（速率计数器，PDH 自己按两次采样差分）----
        double dr = ReadSingle(_cDiskRead);
        double dw = ReadSingle(_cDiskWrite);
        s.DiskReadBps = double.IsNaN(dr) ? 0 : Math.Max(0, dr);
        s.DiskWriteBps = double.IsNaN(dw) ? 0 : Math.Max(0, dw);

        return s;
    }

    // ---------------- PDH helpers ----------------

    private static double ReadSingle(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return double.NaN;
        uint size = 0, count = 0;
        uint r = Native.PdhGetFormattedCounterArray(counter, Native.PDH_FMT_DOUBLE, ref size, out count, IntPtr.Zero);
        if (r != Native.PDH_MORE_DATA || size == 0) return double.NaN;

        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            r = Native.PdhGetFormattedCounterArray(counter, Native.PDH_FMT_DOUBLE, ref size, out count, buf);
            if (r != 0 || count == 0) return double.NaN;
            var item = Marshal.PtrToStructure<Native.PDH_FMT_COUNTERVALUE_ITEM>(buf);
            return item.FmtValue.CStatus == 0 ? item.FmtValue.doubleValue : double.NaN;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>返回 实例名归一化键 → 数值。键形如 0x00000000_0x00010FB0_phys_0</summary>
    private static List<(string Key, double Value)> ReadArray(IntPtr counter)
    {
        var list = new List<(string, double)>(64);
        if (counter == IntPtr.Zero) return list;

        uint size = 0, count = 0;
        uint r = Native.PdhGetFormattedCounterArray(counter, Native.PDH_FMT_DOUBLE, ref size, out count, IntPtr.Zero);
        if (r != Native.PDH_MORE_DATA || size == 0) return list;

        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            r = Native.PdhGetFormattedCounterArray(counter, Native.PDH_FMT_DOUBLE, ref size, out count, buf);
            if (r != 0) return list;

            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<Native.PDH_FMT_COUNTERVALUE_ITEM>(buf + i * ItemSize);
                if (item.FmtValue.CStatus != 0 || item.szName == null) continue;
                string key = NormalizeInstance(item.szName);
                if (key.Length == 0) continue;
                list.Add((key, item.FmtValue.doubleValue));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }

    /// <summary>
    /// pid_1208_luid_0x00000000_0x00010FB0_phys_0_eng_0_engtype_3D  →  0x00000000_0x00010FB0_phys_0
    /// luid_0x00000000_0x00010FB0_phys_0                           →  0x00000000_0x00010FB0_phys_0
    /// </summary>
    /// <summary>P/Invoke 读到的原始实例名 → 归一化键 "0xH_0xL_phys_N"（GpuLocator 存配置用同一口径）。</summary>
    public static string NormalizeInstance(string name)
    {
        int i = name.IndexOf("luid_", StringComparison.Ordinal);
        if (i < 0) return string.Empty;
        int start = i + 5;
        int end = name.IndexOf("_eng_", start, StringComparison.Ordinal);
        if (end < 0) end = name.Length;
        return name.Substring(start, end - start).TrimEnd('_');
    }

    // ---------------- misc ----------------

    private static double _cpuBaseGhz = -1;
    public static double CpuBaseGhz
    {
        get
        {
            if (_cpuBaseGhz > 0) return _cpuBaseGhz;
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                object? v = k?.GetValue("~MHz");
                if (v != null) _cpuBaseGhz = Convert.ToDouble(v, CultureInfo.InvariantCulture) / 1000.0;
            }
            catch { }
            if (_cpuBaseGhz <= 0) _cpuBaseGhz = 3.0;
            return _cpuBaseGhz;
        }
    }

    private static ulong ReadVramTotalFromRegistry()
    {
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (root == null) return 0;
            ulong best = 0;
            foreach (string sub in root.GetSubKeyNames())
            {
                if (sub.Length != 4 || !int.TryParse(sub, out _)) continue;
                using var k = root.OpenSubKey(sub);
                if (k?.GetValue("HardwareInformation.qwMemorySize") is long q && q > 0)
                    best = Math.Max(best, (ulong)q);
            }
            return best;
        }
        catch { return 0; }
    }

    private static readonly string[] NetSkip =
    {
        "loopback", "teredo", "isatap", "vethernet", "vmware", "virtualbox",
        "hyper-v", "tailscale", "wsl", "npcap", "bluetooth", "pseudo"
    };

    private List<NetworkInterface>? _nics;
    private long _nicsAt;

    private static bool IsRealNic(NetworkInterface ni)
    {
        if (ni.OperationalStatus != OperationalStatus.Up) return false;
        if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
            ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) return false;
        string hay = (ni.Name + " " + ni.Description).ToLowerInvariant();
        foreach (var s in NetSkip) if (hay.Contains(s)) return false;
        return true;
    }

    /// <summary>托盘菜单「网速接口」要列的物理网卡（Id + 显示名）。枚举失败返回空表（菜单不出现该子项）。</summary>
    internal static List<(string Id, string Name)> EnumRealNics()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(IsRealNic)
                .Select(n => (n.Id, n.Name))
                .ToList();
        }
        catch { return new List<(string, string)>(); }
    }

    /// <summary>接口选择器匹配：优先按 Id（GUID，重命名网卡也不变），也接受按名称（手改配置给名称也认）。
    /// 大小写不敏感 —— GUID 与名称的既有比较口径一致。</summary>
    public static bool NicMatches(string selector, string id, string name) =>
        string.Equals(selector, id, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(selector, name, StringComparison.OrdinalIgnoreCase);

    private (ulong Down, ulong Up, long Ticks) ReadNetTotals()
    {
        long now = Stopwatch.GetTimestamp();

        // 枚举网卡本身要 8~16ms（虚拟网卡多），缓存 30 秒
        if (_nics == null || now - _nicsAt > Stopwatch.Frequency * 30)
        {
            try { _nics = NetworkInterface.GetAllNetworkInterfaces().Where(IsRealNic).ToList(); }
            catch { _nics = new List<NetworkInterface>(); }
            _nicsAt = now;
        }

        // v0.35 网速分接口：锁定单块网卡统计；接口不在了（拔出 / 禁用）回落全部，别把网速冻在 0
        NetworkInterface? only = null;
        if (NetInterfaceId.Length > 0)
            foreach (var ni in _nics)
                if (NicMatches(NetInterfaceId, ni.Id, ni.Name)) { only = ni; break; }

        bool miss = NetInterfaceId.Length > 0 && only == null;
        if (miss != _netSelMissLogged)
        {
            _netSelMissLogged = miss;
            if (miss) Dbg.Log($"net interface missing, fallback to all: {NetInterfaceId}");
        }
        NetInterfaceName = only?.Name;

        ulong down = 0, up = 0;
        if (only != null)
        {
            (down, up) = ReadNic(only);
        }
        else
        {
            foreach (var ni in _nics)
            {
                var (d, u) = ReadNic(ni);
                down += d;
                up += u;
            }
        }
        return (down, up, now);
    }

    private static (ulong Down, ulong Up) ReadNic(NetworkInterface ni)
    {
        try
        {
            var st = ni.GetIPStatistics();
            return ((ulong)Math.Max(0, st.BytesReceived), (ulong)Math.Max(0, st.BytesSent));
        }
        catch { return (0, 0); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_query != IntPtr.Zero) { Native.PdhCloseQuery(_query); _query = IntPtr.Zero; }
        if (_gpuQuery != IntPtr.Zero) { Native.PdhCloseQuery(_gpuQuery); _gpuQuery = IntPtr.Zero; }
        _nvml?.Dispose();
        _nvml = null;
    }
}
