using Microsoft.Win32;

namespace WMonitor;

internal static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WMonitor";

    public static bool IsEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(ValueName) != null;
        }
        catch { return false; }
    }

    public static void Set(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (k == null) return;
            if (on)
            {
                string exe = Environment.ProcessPath ?? "";
                if (exe.Length > 0) k.SetValue(ValueName, $"\"{exe}\"");
            }
            else
            {
                k.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch { }
    }

    /// <summary>
    /// 自启路径自愈（v0.30，roadmap P0 遗留项）：注册表还指向旧位置（用户挪了安装目录、
    /// 或从旧版升级）时改回当前正在运行的 exe。只在「注册的路径已经不存在」时才动 ——
    /// 路径还活着说明用户在多副本场景下有自己的意图，不越权改写。
    /// 返回 true = 修过（调用方写一条诊断日志）。
    /// </summary>
    public static bool RepairIfStale()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (k == null) return false;

            string exe = Environment.ProcessPath ?? "";
            if (exe.Length == 0) return false;
            string registered = (k.GetValue(ValueName) as string)?.Trim('"').Trim() ?? "";
            if (registered.Length == 0) return false;                     // 自启本来就没开，不动
            if (string.Equals(registered, exe, StringComparison.OrdinalIgnoreCase)) return false;
            if (File.Exists(registered)) return false;                    // 旧路径还在 → 用户意图，不动
            k.SetValue(ValueName, $"\"{exe}\"");
            return true;
        }
        catch { return false; }
    }
}
