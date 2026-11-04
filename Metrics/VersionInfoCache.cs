using System.Runtime.InteropServices;
using System.Text;
using WMonitor.Interop;

namespace WMonitor.Metrics;

/// <summary>
/// 按完整路径缓存 FileDescription / ProductName / Company（TTL 15 秒，失败同样缓存，避免每帧重试读 PE 资源）。
/// 主面板用它把 chrome.exe 显示成「Google Chrome」。
/// </summary>
public sealed class VersionInfoCache
{
    public readonly record struct Info(string Description, string Product, string Company);

    private sealed class Entry
    {
        public Info V;
        public long At;     // Stopwatch ticks
    }

    private readonly Dictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(15);

    public Info Get(string? path, long nowTicks)
    {
        if (path == null) return default;
        if (_cache.TryGetValue(path, out var e) && nowTicks - e.At < Ttl.Ticks) return e.V;

        var v = Read(path);
        _cache[path] = new Entry { V = v, At = nowTicks };
        return v;
    }

    public static string DisplayName(in Info info, string fallback) =>
        FirstNonEmpty(info.Description, info.Product, fallback);

    private static string FirstNonEmpty(params string?[] xs)
    {
        foreach (var x in xs)
            if (!string.IsNullOrWhiteSpace(x)) return x;
        return "";
    }

    private static Info Read(string path)
    {
        try
        {
            uint handle;
            uint size = Native.GetFileVersionInfoSize(path, out handle);
            if (size == 0) return default;

            IntPtr block = Marshal.AllocHGlobal((int)size);
            try
            {
                if (!Native.GetFileVersionInfo(path, handle, size, block)) return default;

                if (!Native.VerQueryValue(block, @"\VarFileInfo\Translation", out IntPtr tp, out uint tl) ||
                    tl < 4)
                    return default;

                // 逐个语言页找，谁有非空 FileDescription / ProductName 用谁
                for (uint off = 0; off + 4 <= tl; off += 4)
                {
                    ushort lang = (ushort)Marshal.ReadInt16(tp, (int)off);
                    ushort cp = (ushort)Marshal.ReadInt16(tp, (int)off + 2);
                    string prefix = $"\\StringFileInfo\\{lang:X4}{cp:X4}\\";

                    string desc = Query(block, prefix + "FileDescription");
                    string prod = Query(block, prefix + "ProductName");
                    string comp = Query(block, prefix + "CompanyName");
                    if (desc.Length > 0 || prod.Length > 0 || comp.Length > 0)
                        return new Info(desc, prod, comp);
                }
            }
            finally { Marshal.FreeHGlobal(block); }
        }
        catch { }
        return default;
    }

    private static string Query(IntPtr block, string key)
    {
        if (!Native.VerQueryValue(block, key, out IntPtr p, out uint len) || p == IntPtr.Zero || len == 0)
            return "";
        return Marshal.PtrToStringUni(p, (int)len - 1) ?? "";   // len 含结尾 \0
    }
}
