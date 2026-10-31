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
    private ulong _vramTotalFallback;

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

    public void LockGpu(string? luid) => _lockedLuid = luid;

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
        var gpuMem = ReadArray(_cGpuMem); // key: 0x00000000_0x00010FB0_phys_0 -> bytes
        string? target = _lockedLuid;
        ulong best = 0;
        if (target == null)
        {
            foreach (var (k, v) in gpuMem)
                if (v > best) { best = (ulong)v; target = k; }
        }
        else
        {
            foreach (var (k, v) in gpuMem)
                if (k == target) best = (ulong)v;
        }

        if (target != null)
        {
            s.HasGpu = true;
            s.VramUsedBytes = best;
            ulong total = _nvml?.MemoryTotal ?? 0;
            if (total == 0) total = _vramTotalFallback;
            s.VramTotalBytes = total;
            s.HasVramTotal = total > 0;

            // N 卡优先用 NVML（微秒级）；无 NVML 时才走 PDH 的数百条 GPU Engine 实例
            if (_nvml != null && _nvml.HasUtil)
            {
                s.GpuPercent = Math.Clamp(double.IsNaN(_nvml.GpuUtilPercent) ? 0 : _nvml.GpuUtilPercent, 0, 100);
            }
            else if (_gpuQuery != IntPtr.Zero)
            {
                Native.PdhCollectQueryData(_gpuQuery);
                double sum = 0;
                foreach (var (k, v) in ReadArray(_cGpuEngine))
                    if (k == target) sum += v;
                s.GpuPercent = Math.Clamp(sum, 0, 100);
            }
        }

        // ---- NVML: 利用率 / 名字 / 温度 / 功耗 ----
        if (_nvml != null)
        {
            _nvml.Refresh();
            s.GpuName = _nvml.Name;
            s.GpuTempC = _nvml.TemperatureC;
            s.GpuPowerW = _nvml.PowerW;
            s.HasTemp = !double.IsNaN(s.GpuTempC);
        }

        // ---- 网速 ----
        var (down, up, ticks) = ReadNetTotals();
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
    private static string NormalizeInstance(string name)
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

        ulong down = 0, up = 0;
        foreach (var ni in _nics)
        {
            try
            {
                var st = ni.GetIPStatistics();
                down += (ulong)Math.Max(0, st.BytesReceived);
                up += (ulong)Math.Max(0, st.BytesSent);
            }
            catch { }
        }
        return (down, up, now);
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
