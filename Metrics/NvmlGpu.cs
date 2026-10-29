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

    private IntPtr _lib;
    private IntPtr _dev;
    private DShutdown? _shutdown;
    private DGetTemp? _getTemp;
    private DGetPower? _getPower;
    private DGetMem? _getMem;
    private DGetUtil? _getUtil;

    public string Name { get; private set; } = "GPU";
    public ulong MemoryTotal { get; private set; }
    public double TemperatureC { get; private set; } = double.NaN;
    public double PowerW { get; private set; } = double.NaN;

    /// <summary>SM 利用率 %（比 PDH 的 GPU Engine 数组快两个数量级）。</summary>
    public double GpuUtilPercent { get; private set; }
    public bool HasUtil => _getUtil != null;

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

        var handle = Fn<DGetHandle>("nvmlDeviceGetHandleByIndex_v2");
        if (handle == null || handle(0, out _dev) != 0) return false;

        _shutdown = Fn<DShutdown>("nvmlShutdown");
        _getTemp = Fn<DGetTemp>("nvmlDeviceGetTemperature");
        _getPower = Fn<DGetPower>("nvmlDeviceGetPowerUsage");
        _getMem = Fn<DGetMem>("nvmlDeviceGetMemoryInfo");
        _getUtil = Fn<DGetUtil>("nvmlDeviceGetUtilizationRates");

        var getName = Fn<DGetName>("nvmlDeviceGetName");
        if (getName != null)
        {
            var sb = new StringBuilder(96);
            if (getName(_dev, sb, (uint)sb.Capacity) == 0 && sb.Length > 0) Name = sb.ToString();
        }

        if (_getMem != null && _getMem(_dev, out NvmlMemory m) == 0 && m.Total > 0)
            MemoryTotal = m.Total;

        return true;
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
