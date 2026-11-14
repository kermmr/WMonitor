using System.Globalization;
using System.Runtime.InteropServices;
using WMonitor.Interop;

namespace WMonitor.Metrics;

/// <summary>一块当前活着的适配器：PDH 锁键 + 显示名 + 专用显存。</summary>
public sealed record GpuAdapter(
    string Key,        // 归一化锁键 "0x00000000_0x00010FB0_phys_0"（= MetricSampler gpuMem 的键，cfg.LockedGpuLuid 存它）
    string Name,       // DXGI 适配器描述（如 "NVIDIA GeForce RTX 3060"）；匹配不到时用 LUID 兜底
    ulong VramBytes,   // DXGI DedicatedVideoMemory
    string LuidBase);  // "0x00000000_0x00010FB0"（不含 phys 段）

/// <summary>
/// 枚举可锁定的显卡（v0.30，配合设置界面「监视的显卡」下拉）。
/// 键必须来自 PDH <c>\GPU Adapter Memory(*)</c> 的真实实例（锁的语义就是按这个键过滤，
/// 见 MetricSampler.Sample 的 gpuMem 分支），名字按 LUID 匹配：
/// DXGI 适配器描述 → <paramref name="nameOverrides"/>（NVML，主进程采样器传入）→ "GPU 0x…" 兜底。
/// 列表是打开设置窗口时的静态快照 —— GPU 热插拔很罕见，不值得为它常驻监听。
/// </summary>
public static class GpuLocator
{
    public static List<GpuAdapter> Enumerate(IReadOnlyDictionary<ulong, string>? nameOverrides = null)
    {
        var byLuid = EnumerateDxgi();
        var list = new List<GpuAdapter>(4);

        foreach (string raw in EnumeratePdhKeys())
        {
            // 原始实例 "luid_0xHHHHHHHH_0xLLLLLL_phys_N" → 与 MetricSampler.gpuMem 相同的归一化键。
            // 之前存过原始实例名（带 luid_ 前缀），和 gpuMem 的键对不上 —— 锁定静默回落，白锁。
            string key = MetricSampler.NormalizeInstance(raw);
            var (hi, lo) = ParseLuid(key);
            if (hi == 0 && lo == 0) continue;

            ulong full64 = ((uint)hi << 32) | lo;
            var dx = byLuid.Find(d => d.High == hi && d.Low == lo);
            string name = dx?.Name
                          ?? (nameOverrides != null && nameOverrides.TryGetValue(full64, out var nv) ? nv : null)
                          ?? $"GPU 0x{lo:X}";
            string luidBase = key[..key.LastIndexOf("_phys_", StringComparison.OrdinalIgnoreCase)];
            list.Add(new GpuAdapter(key, name, dx?.VramBytes ?? 0, luidBase));
        }

        // PDH 枚举失败（比如计数器还没就绪）时退回 DXGI 造键 —— 仍能出下拉，只是键没被 PDH 验证过
        if (list.Count == 0)
            foreach (var d in byLuid)
            {
                string key = $"0x{d.High:X8}_0x{d.Low:X}_phys_0";
                list.Add(new GpuAdapter(key, d.Name, d.VramBytes, key[..key.LastIndexOf("_phys_")]));
            }

        return list;
    }

    /// <summary>
    /// "0x00000000_0x00010FB0_phys_0"（归一化）或 "luid_0x…_0x…_phys_0"（原始实例）→ (High, Low)。
    /// NVML 选卡（MetricSampler.LockGpu）也用它。解析失败 → (0, 0)。
    /// </summary>
    public static (int High, uint Low) ParseLuid(string key)
    {
        try
        {
            int li = key.LastIndexOf("luid_", StringComparison.OrdinalIgnoreCase);
            string rest = li >= 0 ? key[(li + "luid_".Length)..] : key;   // 归一化键没有 luid_ 前缀，从头开始
            int underscore = rest.IndexOf('_', StringComparison.Ordinal);
            if (underscore <= 0 || !rest.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return (0, 0);
            int high = int.Parse(rest[..underscore].Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            string second = rest[(underscore + 1)..];
            int end = second.IndexOf('_', StringComparison.Ordinal);
            uint low = uint.Parse((end < 0 ? second : second[..end]).Substring(2),
                NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return (high, low);
        }
        catch { return (0, 0); }
    }

    /// <summary>两路枚举的原始返回码（--gpu-locator 探针用；正常的 Enumerate 失败是静默的）。</summary>
    public static string Diagnose()
    {
        var sb = new System.Text.StringBuilder();

        // PDH 原始路径：两个列表缓冲都给足 —— 只给实例列表、计数器列表传 null 也会 PDH_MORE_DATA
        uint cLen = 4096, iLen = 4096;
        char[] cbuf = new char[cLen], ibuf = new char[iLen];
        uint r = Native.PdhEnumObjectItemsW(null, null, "GPU Adapter Memory",
            cbuf, ref cLen, ibuf, ref iLen, Native.PERF_DETAIL_WIZARD, 0);
        sb.AppendLine($"PdhEnumObjectItemsW(GPU Adapter Memory) → 0x{r:X8}");
        if (r == 0)
            foreach (string s in new string(ibuf, 0, (int)iLen).Split('\0', StringSplitOptions.RemoveEmptyEntries))
                sb.AppendLine($"  instance: {s}");

        // DXGI 原始路径（受限作业对象里可能 E_NOINTERFACE —— docs/05 §〇 同源的沙箱限制，
        // 用户会话里正常；失败时名字退化成 "GPU 0x…"，下拉仍然出得来）
        var iid = new Guid(0x770aae78, 0xf26f, 0x4dba, 0xa9, 0x29, 0x25, 0xc6, 0x0d, 0x8e, 0x55, 0x38);
        int cdx = CreateDXGIFactory1(ref iid, out IntPtr factory);
        sb.AppendLine($"CreateDXGIFactory1(IID_IDXGIFactory1) → 0x{cdx:X8}");
        if (cdx == 0 && factory != IntPtr.Zero)
        {
            var enumAdapters1 = Slot<DEnumAdapters1>(factory, 12);
            for (uint i = 0; i < 8; i++)
            {
                int rea = enumAdapters1(factory, i, out IntPtr adapter);
                if (rea != 0) break;
                var desc = Slot<DGetDesc1>(adapter, 10);
                if (desc(adapter, out DXGI_ADAPTER_DESC1 d) == 0)
                    sb.AppendLine($"  adapter {i}: {d.Description.TrimEnd('\0')}  luidLow=0x{d.LuidLow:X}");
                Slot<DRelease>(adapter, 2)(adapter);
            }
            Slot<DRelease>(factory, 2)(factory);
        }
        return sb.ToString();
    }

    // ---------------- PDH：\GPU Adapter Memory 的当前实例 ----------------

    private static List<string> EnumeratePdhKeys()
    {
        // 两个列表缓冲都要给：计数器列表传 null + 长度 0 时整个调用返回 PDH_MORE_DATA，
        // 即使实例列表装得下（实测本机 0x800007D2）。不够时缓冲翻倍重试（最多 16K）。
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int size = 4096 * (attempt + 1);
            char[] cbuf = new char[size], ibuf = new char[size];
            uint cLen = (uint)cbuf.Length, iLen = (uint)ibuf.Length;
            uint r = Native.PdhEnumObjectItemsW(null, null, "GPU Adapter Memory",
                cbuf, ref cLen, ibuf, ref iLen, Native.PERF_DETAIL_WIZARD, 0);
            if (r == Native.PDH_MORE_DATA && attempt < 2) continue;
            if (r != 0) break;
            var keys = new List<string>(4);
            foreach (string s in new string(ibuf, 0, (int)iLen).Split('\0', StringSplitOptions.RemoveEmptyEntries))
                if (s.Contains("_phys_", StringComparison.OrdinalIgnoreCase)) keys.Add(s);
            return keys;
        }
        return new List<string>(0);
    }

    // ---------------- DXGI：适配器名 + LUID（CreateDXGIFactory1，零依赖手写 vtable） ----------------

    private sealed record DxgiAdapter(string Name, ulong VramBytes, int High, uint Low);

    private static List<DxgiAdapter> EnumerateDxgi()
    {
        var list = new List<DxgiAdapter>(4);
        var iidFactory1 = new Guid(0x770aae78, 0xf26f, 0x4dba, 0xa9, 0x29, 0x25, 0xc6, 0x0d, 0x8e, 0x55, 0x38);
        if (CreateDXGIFactory1(ref iidFactory1, out IntPtr factory) != 0 || factory == IntPtr.Zero) return list;
        try
        {
            var enumAdapters1 = Slot<DEnumAdapters1>(factory, 12);     // IDXGIFactory1.EnumAdapters1
            for (uint i = 0; ; i++)
            {
                if (enumAdapters1(factory, i, out IntPtr adapter) != 0) break;
                try
                {
                    var getDesc1 = Slot<DGetDesc1>(adapter, 10);       // IDXGIAdapter1.GetDesc1
                    if (getDesc1(adapter, out DXGI_ADAPTER_DESC1 d) != 0) continue;
                    string name = d.Description.TrimEnd('\0');
                    if (name.Length == 0) name = "GPU";
                    list.Add(new DxgiAdapter(name, d.DedicatedVideoMemory, d.LuidHigh, d.LuidLow));
                }
                finally { Slot<DRelease>(adapter, 2)(adapter); }
            }
        }
        finally { Slot<DRelease>(factory, 2)(factory); }
        return list;
    }

    // COM 纯手 vtable：项目零依赖原则下不用 ComWrappers，槽位号见 dxgi.h
    // （IUnknown 0-2；IDXGIObject 3-6；IDXGIFactory.EnumAdapters=7；IDXGIFactory1.EnumAdapters1=12；
    //   IDXGIAdapter.EnumOutputs=7；IDXGIAdapter1.GetDesc1=10）。
    private static T Slot<T>(IntPtr comObject, int slot) where T : Delegate
    {
        IntPtr vtbl = Marshal.ReadIntPtr(comObject);
        IntPtr fn = Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(fn);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int DRelease(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int DEnumAdapters1(IntPtr self, uint index, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int DGetDesc1(IntPtr self, out DXGI_ADAPTER_DESC1 desc);

    [DllImport("dxgi.dll", PreserveSig = true)]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

    // Description 是内联的 128 WCHAR（不是指针），ByValTStr 封送正合适。
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }
}
