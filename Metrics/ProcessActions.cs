using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WMonitor.Interop;

namespace WMonitor.Metrics;

/// <summary>详情条动作类型。OpenLocation 只是 explorer /select（非管控：不确认、不写日志），
/// 其余四个是管控动作 —— 全部二次确认 + 留痕（plan §7 阶段 4）。</summary>
public enum DashboardAction
{
    OpenLocation,
    Terminate,
    Suspend,
    Resume,
    Priority
}

/// <summary>动作结果。Ok=false 时 Error 是可直接展示的中文原因（plan §7 阶段 4.8）。</summary>
public sealed class ActionResult
{
    public bool Ok;
    public string? Error;

    public static ActionResult Success() => new() { Ok = true };
    public static ActionResult Fail(string reason) => new() { Ok = false, Error = reason };
}

/// <summary>
/// 进程管控执行层：结束 / 挂起 / 恢复 / 优先级，全部只接受显式 PID。
/// UI 层负责二次确认与安全规则；本层只负责「打得开句柄就执行、执行不了给中文原因」+ 写 actions 日志。
/// 句柄按需最小权限：终止 TERMINATE|SYNCHRONIZE、挂起恢复 SUSPEND_RESUME、优先级 SET_INFORMATION，
/// 读优先级 QUERY_LIMITED 即可（Vista+）。
/// </summary>
public static class ProcessActions
{
    /// <summary>打开文件位置等入口不算管控动作，但结束/挂起/恢复/优先级必须留痕。</summary>
    public static string LogPath => Path.Combine(AppConfig.Dir, "dashboard-actions.log");

    // ---------------- 四个动作 ----------------

    /// <summary>结束进程。TerminateProcess 是异步的，等句柄置信号（≤3s）再报成功。</summary>
    public static ActionResult Terminate(int pid, string name = "", string path = "")
    {
        IntPtr h = Native.OpenProcess(
            Native.PROCESS_TERMINATE | Native.SYNCHRONIZE | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero)
            return OpenFailed(DashboardAction.Terminate, pid, name, path);
        try
        {
            if (!Native.TerminateProcess(h, 1))
            {
                // 已终止但句柄没放干净的 zombie（进程对象还在、PID 还在列表里）TerminateProcess 报 5 ——
                // 先等 0ms 看句柄是否已置信号，置了就是「已退出」，不是权限问题。
                if (Native.WaitForSingleObject(h, 0) == Native.WAIT_OBJECT_0)
                    return LogAnd(DashboardAction.Terminate, pid, name, path, 0, GoneMessage);
                return LogAnd(DashboardAction.Terminate, pid, name, path, 0, Win32Error(Marshal.GetLastWin32Error()));
            }
            Native.WaitForSingleObject(h, 3000);   // 超时也报成功：终止已受理，只是还没退干净
            return LogAnd(DashboardAction.Terminate, pid, name, path, 0, null);
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }

    public static ActionResult Suspend(int pid, string name = "", string path = "") =>
        SuspendResume(Native.NtSuspendProcess, DashboardAction.Suspend, pid, name, path);

    public static ActionResult Resume(int pid, string name = "", string path = "") =>
        SuspendResume(Native.NtResumeProcess, DashboardAction.Resume, pid, name, path);

    private static ActionResult SuspendResume(
        Func<IntPtr, uint> ntCall, DashboardAction kind, int pid, string name, string path)
    {
        IntPtr h = Native.OpenProcess(Native.PROCESS_SUSPEND_RESUME, false, pid);
        if (h == IntPtr.Zero)
            return OpenFailed(kind, pid, name, path);
        try
        {
            uint st = ntCall(h);
            return LogAnd(kind, pid, name, path, 0, st == 0 ? null : NtStatusError(st));
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }

    /// <summary>读当前优先级；打不开句柄 / 已退出返回 null（详情条显示「—」即可，不算错误）。</summary>
    public static uint? GetPriority(int pid)
    {
        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            uint c = Native.GetPriorityClass(h);
            if (c == 0)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == 5 || err == 6 || err == 87) return null;
            }
            return c;
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }

    public static ActionResult SetPriority(int pid, uint priorityClass, string name = "", string path = "")
    {
        IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION, false, pid);
        if (h == IntPtr.Zero)
            return OpenFailed(DashboardAction.Priority, pid, name, path);
        try
        {
            if (!Native.SetPriorityClass(h, priorityClass))
                return LogAnd(DashboardAction.Priority, pid, name, path, 0, Win32Error(Marshal.GetLastWin32Error()));
            return LogAnd(DashboardAction.Priority, pid, name, path, 0, null);
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }

    // ---------------- 挂起状态（详情条 / 按钮 label 用，只对选中行调用） ----------------

    /// <summary>
    /// 全部线程都在 Suspended 等待才算挂起（System.Diagnostics 内部也走 NtQuerySystemInformation，
    /// 单进程 ~1–3ms，调用方负责 TTL 缓存，别塞进每帧采样）。进程已退出返回 false。
    /// </summary>
    public static bool IsSuspended(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            var threads = p.Threads;
            if (threads.Count == 0) return false;
            foreach (ProcessThread t in threads)
                if (t.ThreadState != System.Diagnostics.ThreadState.Wait ||
                    t.WaitReason != ThreadWaitReason.Suspended)
                    return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---------------- 错误码 → 中文（plan §7 阶段 4.8） ----------------

    /// <summary>PID 还在系统进程列表里吗。OpenProcess 失败时先查这个：进程都没了就该报「已退出」，
    /// 不能信错误码 —— 实测（Win11 26300）死 PID 的 OpenProcess 可能返回 5（权限）而不是 87（参数）。</summary>
    private static bool ProcessExists(int pid)
    {
        try
        {
            var ids = new uint[4096];                      // 本机 500+ 进程，1024 不够（needed 是字节量）
            if (!Native.K32EnumProcesses(ids, (uint)(ids.Length * 4), out uint needed)) return true;   // 查不到按存在处理，走错误码映射
            int count = (int)(needed / 4);
            for (int i = 0; i < count; i++)
                if (ids[i] == (uint)pid) return true;
            return false;
        }
        catch
        {
            return true;
        }
    }

    private const string GoneMessage = "进程已退出或 PID 无效";

    /// <summary>OpenProcess 失败的统一入口。注意先取 LastWin32Error 再查 ProcessExists ——
    /// EnumProcesses 会把错误码冲掉（实测冲掉后 pid=4 报「错误码 0」）。</summary>
    private static ActionResult OpenFailed(DashboardAction kind, int pid, string name, string path)
    {
        int err = Marshal.GetLastWin32Error();
        return LogAnd(kind, pid, name, path, 0,
            ProcessExists(pid) ? Win32Error(err) : GoneMessage);
    }

    public static string Win32Error(int err) => err switch
    {
        5 => "权限不足：进程受保护，或需要以管理员身份运行 WMonitor",
        6 => "句柄失效：进程可能刚好退出",
        87 => GoneMessage,
        1314 => "需要管理员权限（缺少 SeIncreaseBasePriorityPrivilege）",
        299 => "读取进程信息失败（部分受保护进程拒绝访问）",
        _ => $"操作失败（Win32 错误码 {err}）"
    };

    public static string NtStatusError(uint status) => status switch
    {
        0xC0000022 => "权限不足：进程受保护，或需要以管理员身份运行 WMonitor",
        0xC0000008 => "句柄失效：进程可能刚好退出",
        0xC0000001 => "操作失败（STATUS_UNSUCCESSFUL）",
        _ => $"操作失败（NTSTATUS 0x{status:X8}）"
    };

    // ---------------- 日志 ----------------

    /// <summary>追加一行 actions 日志。Error=null 记成功。失败也留痕（安全审计口径）。</summary>
    public static ActionResult LogAnd(
        DashboardAction kind, int pid, string name, string path, int batchCount, string? error)
    {
        try
        {
            Directory.CreateDirectory(AppConfig.Dir);
            var sb = new StringBuilder(160);
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append(" | ")
              .Append(kind switch
              {
                  DashboardAction.Terminate => "结束",
                  DashboardAction.Suspend => "挂起",
                  DashboardAction.Resume => "恢复",
                  _ => "优先级"
              })
              .Append(" | pid=").Append(pid);
            if (batchCount > 1) sb.Append(" | 批量=").Append(batchCount);
            if (name.Length > 0) sb.Append(" | name=").Append(name);
            if (path.Length > 0) sb.Append(" | path=").Append(path);
            sb.Append(" | 结果=").Append(error == null ? "成功" : "失败");
            if (error != null) sb.Append(" | 原因=").Append(error);
            sb.Append("\r\n");
            File.AppendAllText(LogPath, sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // 日志写不进去不能挡住动作本身已完成的反馈；error.log 由调用方兜底
        }
        return error == null ? ActionResult.Success() : ActionResult.Fail(error);
    }
}
