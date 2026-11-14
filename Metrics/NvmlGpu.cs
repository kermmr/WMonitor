using System.Runtime.InteropServices;
using System.Text;
using WMonitor.Interop;

namespace WMonitor.Metrics;

/// <summary>
/// NVML（nvidia-smi 的底层库）动态加载。没有 N 卡 / 没有 nvml.dll 时静默降级，
/// 温度功耗返回 NaN，UI 侧不显示而不是显示 0。
/// </summary>
public sealed class NvmlGpu : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlUtilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    private const uint NVML_TEMPERATURE_GPU = 0;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DInit();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DShutdown();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DGetName(IntPtr dev, StringBuilder buf, uint len);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DGetTemp(IntPtr dev, uint sensor, out uint temp);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DGetPower(IntPtr dev, out uint mw);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DGetMem(IntPtr dev, out NvmlMemory mem);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DGetUtil(IntPtr dev, out NvmlUtilization util);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DGetLuid(IntPtr dev, out ulong luid, out uint deviceMask);

    private IntPtr _lib;
    private IntPtr _first;                  // 设备 0（默认 / 找不到锁定卡时的回落）
    private IntPtr _dev;                    // 当前活动设备（Refresh / Name / MemoryTotal 都读它）
    private readonly Dictionary<ulong, IntPtr> _byLuid = new();        // 完整 64 位 LUID → 设备句柄
    private readonly Dictionary<ulong, string> _namesByLuid = new();   // LUID → 设备名（GpuLocator 兜名用）
    private readonly Dictionary<IntPtr, (string Name, ulong Total)> _info = new(); // 每设备静态信息缓存
    private DShutdown? _shutdown;
    private DGetTemp? _getTemp;
    private DGetPower? _getPower;
    private DGetMem? _getMem;
    private DGetUtil? _getUtil;
    private DGetName? _getName;
    private DGetHandle? _handle;

    public string Name { get; private set; } = "GPU";
    public ulong MemoryTotal { get; private set; }
    public double TemperatureC { get; private set; } = double.NaN;
    public double PowerW { get; private set; } = double.NaN;

    /// <summary>SM 利用率 %（比 PDH 的 GPU Engine 数组快两个数量级）。</summary>
    public double GpuUtilPercent { get; private set; }
    public bool HasUtil => _getUtil != null;

    /// <summary>LUID → 设备名（nvmlDeviceGetLuid 可用时才有内容）。GpuLocator 命名兜底用。</summary>
    public IReadOnlyDictionary<ulong, string> NameByLuid => _namesByLuid;

    /// <summary>
    /// 这个 LUID 能否被 NVML 精确取数（利用率/温度/功耗）。
    /// 旧驱动没有 nvmlDeviceGetLuid（实测 610.47 就没有）→ 永远 false，
    /// 锁定时采样端要改走 PDH Engine 按 LUID 聚合，别把设备 0 的数张冠李戴。
    /// </summary>
    public bool CoversLuid(ulong full64) => _byLuid.ContainsKey(full64);

    public static NvmlGpu? TryCreate()
    {
        foreach (string dll in new[] { "nvml.dll", @"C:\Program Files\NVIDIA Corporation\NVSMI\nvml.dll" })
        {
            IntPtr lib = Native.LoadLibrary(dll);
            if (lib == IntPtr.Zero) continue;
            try
            {
                var g = new NvmlGpu { _lib = lib };
                if (g.Init()) return g;
                Native.FreeLibrary(lib);
            }
            catch
            {
                Native.FreeLibrary(lib);
            }
        }
        return null;
    }

    private T? Fn<T>(string name) where T : Delegate
    {
        IntPtr p = Native.GetProcAddress(_lib, name);
        return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(p);
    }

    private bool Init()
    {
        var init = Fn<DInit>("nvmlInit_v2") ?? Fn<DInit>("nvmlInit");
        if (init == null || init() != 0) return false;

        var count = Fn<DGetCount>("nvmlDeviceGetCount_v2");
        if (count == null || count(out uint n) != 0 || n == 0) return false;

        _handle = Fn<DGetHandle>("nvmlDeviceGetHandleByIndex_v2");
        if (_handle == null || _handle(0, out _dev) != 0) return false;
        _first = _dev;

        _shutdown = Fn<DShutdown>("nvmlShutdown");
        _getTemp = Fn<DGetTemp>("nvmlDeviceGetTemperature");
        _getPower = Fn<DGetPower>("nvmlDeviceGetPowerUsage");
        _getMem = Fn<DGetMem>("nvmlDeviceGetMemoryInfo");
        _getUtil = Fn<DGetUtil>("nvmlDeviceGetUtilizationRates");
        _getName = Fn<DGetName>("nvmlDeviceGetName");

        // v0.30：多卡按 LUID 选设备（设置界面「监视的显卡」）。nvmlDeviceGetLuid 旧驱动没有，
        // 缺了就保持单设备行为 —— 锁定只影响 PDH 显存键，利用率/温度仍显示设备 0。
        var getLuid = Fn<DGetLuid>("nvmlDeviceGetLuid");
        if (getLuid != null)
            for (uint i = 0; i < n; i++)
            {
                if (_handle(i, out IntPtr d) != 0) continue;
                if (getLuid(d, out ulong luid, out _) != 0) continue;
                _byLuid[luid] = d;
                var sb = new StringBuilder(96);
                if (_getName != null && _getName(d, sb, (uint)sb.Capacity) == 0 && sb.Length > 0)
                    _namesByLuid[luid] = sb.ToString();
            }

        SetActive(_dev);
        return true;
    }

    /// <summary>
    /// 按锁定显卡切活动设备（MetricSampler.LockGpu 调用，启动与设置变更各一次）。
    /// <paramref name="luid"/> 为 null 或表里找不到（旧驱动没 nvmlDeviceGetLuid / 卡被拔了）→ 设备 0。
    /// </summary>
    public void SelectByLuid(ulong? luid)
    {
        IntPtr dev = luid != null && _byLuid.TryGetValue(luid.Value, out IntPtr d) ? d : _first;
        if (dev == _dev) return;
        SetActive(dev);
    }

    /// <summary>活动设备切换：Name / MemoryTotal 换成新设备的（每设备只读一次，缓存进 _info）。</summary>
    private void SetActive(IntPtr dev)
    {
        _dev = dev;
        if (!_info.TryGetValue(dev, out var info))
        {
            string name = "GPU";
            if (_getName != null)
            {
                var sb = new StringBuilder(96);
                if (_getName(dev, sb, (uint)sb.Capacity) == 0 && sb.Length > 0) name = sb.ToString();
            }
            ulong total = 0;
            if (_getMem != null && _getMem(dev, out NvmlMemory m) == 0) total = m.Total;
            info = (name, total);
            _info[dev] = info;
        }
        Name = info.Name;
        MemoryTotal = info.Total;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DGetCount(out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DGetHandle(uint index, out IntPtr dev);

    /// <summary>温度/功耗按需刷新（采样线程每 tick 调一次）。</summary>
    public void Refresh()
    {
        if (_dev == IntPtr.Zero) return;
        if (_getTemp != null && _getTemp(_dev, NVML_TEMPERATURE_GPU, out uint t) == 0)
            TemperatureC = t;
        if (_getPower != null && _getPower(_dev, out uint mw) == 0)
            PowerW = mw / 1000.0;
        if (_getUtil != null && _getUtil(_dev, out NvmlUtilization u) == 0)
            GpuUtilPercent = u.Gpu;
    }

    public void Dispose()
    {
        try { _shutdown?.Invoke(); } catch { }
        if (_lib != IntPtr.Zero) { Native.FreeLibrary(_lib); _lib = IntPtr.Zero; }
    }
}
