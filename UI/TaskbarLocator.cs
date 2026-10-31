using System.Text;
using WMonitor.Interop;

namespace WMonitor.UI;

/// <summary>屏幕矩形（左上右下，屏幕坐标）。不直接复用 <see cref="Native.RECT"/>：Native 是 internal，
/// public 结构体不能把它暴露出去。</summary>
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>一块屏的任务栏：句柄、显示器设备名、任务栏矩形、该屏有效 DPI。</summary>
public readonly record struct TaskbarInfo(
    IntPtr Hwnd,
    string Device,      // "\\.\DISPLAY1"
    ScreenRect Rect,    // 任务栏窗口矩形（屏幕坐标）
    uint Dpi,           // 该显示器的有效 DPI（96 = 100%）
    bool IsPrimary)
{
    public float DpiScale => Dpi / 96f;
}

/// <summary>
/// 枚举所有任务栏：主屏 Shell_TrayWnd + 副屏 Shell_SecondaryTrayWnd（每屏一个）。
/// 副屏任务栏只在「在所有显示器上显示任务栏」开启时存在；关掉时只会返回主屏。
/// </summary>
public static class TaskbarLocator
{
    // 委托必须保持引用，否则 GC 可能在 EnumWindows 回调期间回收它
    private static readonly Native.EnumWindowsProc EnumProc = OnEnumWindow;
    private static readonly List<IntPtr> Found = new(4);
    private static readonly StringBuilder ClassBuf = new(64);

    /// <summary>主屏在前，其余按屏幕左边界升序（保证「屏幕 1/2/3」不随枚举顺序跳动）。</summary>
    public static List<TaskbarInfo> Enumerate()
    {
        var list = new List<TaskbarInfo>(4);

        IntPtr primary = Native.FindWindow("Shell_TrayWnd", null);
        if (primary != IntPtr.Zero) TryAdd(list, primary, true);

        Found.Clear();
        Native.EnumWindows(EnumProc, IntPtr.Zero);
        foreach (IntPtr h in Found) TryAdd(list, h, false);

        list.Sort((a, b) =>
            a.IsPrimary != b.IsPrimary ? (a.IsPrimary ? -1 : 1)
                                       : a.Rect.Left.CompareTo(b.Rect.Left));
        return list;
    }

    /// <summary>按设备名取一块；找不到（拔屏、配置残留）回落主屏。</summary>
    public static TaskbarInfo? Find(List<TaskbarInfo> all, string? device)
    {
        if (all.Count == 0) return null;
        if (!string.IsNullOrEmpty(device))
            foreach (var t in all)
                if (string.Equals(t.Device, device, StringComparison.OrdinalIgnoreCase)) return t;
        return all[0];
    }

    private static bool OnEnumWindow(IntPtr hWnd, IntPtr lParam)
    {
        ClassBuf.Clear();
        if (Native.GetClassName(hWnd, ClassBuf, ClassBuf.Capacity) > 0
            && ClassBuf.ToString() == "Shell_SecondaryTrayWnd")
            Found.Add(hWnd);
        return true;   // 继续枚举
    }

    private static void TryAdd(List<TaskbarInfo> list, IntPtr hwnd, bool primary)
    {
        if (!Native.GetWindowRect(hwnd, out var r)) return;
        if (r.Width <= 0 || r.Height <= 0) return;
        foreach (var t in list) if (t.Hwnd == hwnd) return;   // 去重

        var (device, dpi) = QueryMonitor(hwnd);
        list.Add(new TaskbarInfo(hwnd, device,
            new ScreenRect(r.Left, r.Top, r.Right, r.Bottom), dpi, primary));
    }

    /// <summary>任务栏所属显示器 + 该屏有效 DPI。DPI 查询失败（旧系统 / 远程会话）回落 96。</summary>
    private static (string Device, uint Dpi) QueryMonitor(IntPtr hwnd)
    {
        IntPtr mon = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
        if (mon == IntPtr.Zero) return ("", 96);

        var mi = new Native.MONITORINFOEX
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFOEX>()
        };
        string device = "";
        if (Native.GetMonitorInfo(mon, ref mi)) device = mi.szDevice ?? "";

        uint dpi = 96;
        if (Native.GetDpiForMonitor(mon, Native.MDT_EFFECTIVE_DPI, out uint dx, out _) == 0 && dx > 0)
            dpi = dx;

        return (device, dpi);
    }

    /// <summary>诊断用：一行一块屏，写日志/`--probe` 时好排查。</summary>
    public static string Describe(List<TaskbarInfo> all)
    {
        if (all.Count == 0) return "(无任务栏)";
        var sb = new StringBuilder();
        for (int i = 0; i < all.Count; i++)
        {
            var t = all[i];
            sb.Append($"#{i} {(t.IsPrimary ? "主" : "副")} {t.Device} {t.Rect.Width}×{t.Rect.Height}@{t.Rect.Left},{t.Rect.Top} dpi={t.Dpi}");
            if (i < all.Count - 1) sb.Append(" | ");
        }
        return sb.ToString();
    }
}
