using System.Diagnostics;
using System.Text.Json;

namespace WMonitor;

/// <summary>
/// GitHub Releases 自动更新（v0.32）。零依赖：公开仓库匿名 REST 即可，不碰凭据。
/// 流程：Check()（后台线程调，24h 一次 + 菜单手动）→ 发现新版本 → 菜单出现「更新到 vX.Y…」
/// → Apply() 下载到 %TEMP%\WMonitor.new.exe → 写一个替换脚本并启动 → 本进程 Quit()
/// → 脚本等几秒后 move 新 exe 覆盖旧 exe（顺带清掉 framework 多文件安装的旧 dll/deps/runtimeconfig，
/// 否则新单文件 exe 会撞上旧清单）→ start 新 exe → 脚本自删。
///
/// 防呆：dotnet host 下（探针 / 开发）ProcessPath 是 dotnet.exe —— 绝不替换宿主，
/// CanApply = false 时 UI 不显示更新项、Apply 直接拒绝。
/// 所有网络异常一律转 null / LastError：更新检查绝不能打扰常驻主功能。
/// </summary>
internal static class Updater
{
    private const string ApiLatest = "https://api.github.com/repos/kermmr/WMonitor/releases/latest";

    private static readonly HttpClient Http = CreateHttp();

    public sealed record UpdateInfo(string Version, string Tag, string DownloadUrl, long SizeBytes);

    /// <summary>当前可用更新（null = 已是最新 / 未检查 / 检查失败）。UI 读它决定是否显示更新项。</summary>
    public static UpdateInfo? Available;

    /// <summary>最近一次 Check 失败的原因（balloon 用；成功时清空）。</summary>
    public static string? LastError;

    /// <summary>能否在本进程内走替换流程（dotnet host 下 false —— 探针/开发场景）。</summary>
    public static bool CanApply
    {
        get
        {
            string exe = Environment.ProcessPath ?? "";
            return exe.Length > 0
                && !exe.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase)
                && !exe.EndsWith("dotnet", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient();
        var ver = typeof(Updater).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        h.DefaultRequestHeaders.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue("WMonitor-Updater", ver));
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
    }

    // ---------------- 版本口径（纯函数，单测覆盖） ----------------

    /// <summary>"v0.31" / "v0.31.2" / "1.2.3" → (主, 次, 修订)；缺段补 0；畸形 → null。</summary>
    internal static (int M, int Mi, int P)? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var t = tag.Trim().TrimStart('v', 'V');
        var parts = t.Split('.');
        if (parts.Length is < 1 or > 3) return null;
        var nums = new List<int>(3);
        foreach (var s in parts)
        {
            if (!int.TryParse(s, out int n) || n < 0) return null;
            nums.Add(n);
        }
        while (nums.Count < 3) nums.Add(0);
        return (nums[0], nums[1], nums[2]);
    }

    /// <summary>tag 是否比 current 新（纯比较，测试友好）。</summary>
    internal static bool IsNewer(string? tag, (int M, int Mi, int P) current)
    {
        if (ParseVersion(tag) is not { } v) return false;
        return v.M != current.M ? v.M > current.M
             : v.Mi != current.Mi ? v.Mi > current.Mi
             : v.P > current.P;
    }

    /// <summary>tag 是否比当前程序集版本新。</summary>
    public static bool IsNewer(string? tag)
    {
        var v = typeof(Updater).Assembly.GetName().Version!;
        return IsNewer(tag, (v.Major, v.Minor, v.Build));
    }

    // ---------------- 检查 ----------------

    /// <summary>同步检查（放后台线程调）。返回可用更新；无更新 / 失败返回 null（LastError 说明原因）。</summary>
    public static UpdateInfo? Check()
    {
        Available = null;
        LastError = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var resp = Http.GetAsync(ApiLatest, cts.Token).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode)
            {
                LastError = $"GitHub API {(int)resp.StatusCode}";
                return null;
            }

            using var doc = JsonDocument.Parse(resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false).GetAwaiter().GetResult());
            var root = doc.RootElement;
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!IsNewer(tag))
            {
                LastError = null;                       // 已是最新不算错误
                return null;
            }

            // 资产按安装形态挑：framework 多文件安装拿 framework 单文件版（并清旧清单），
            // 其余（单文件绿色版 / 安装器装的）拿 portable。
            bool frameworkMode = File.Exists(Path.Combine(
                Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? ".", "WMonitor.deps.json"));
            string suffix = frameworkMode ? "-framework.exe" : "-portable.exe";

            string? url = null;
            long size = 0;
            foreach (var a in root.GetProperty("assets").EnumerateArray())
            {
                string name = a.GetProperty("name").GetString() ?? "";
                if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                url = a.GetProperty("browser_download_url").GetString();
                size = a.GetProperty("size").GetInt64();
                break;
            }
            if (url == null)
            {
                LastError = $"Release {tag} 里没有 {suffix} 资产";
                return null;
            }

            var info = new UpdateInfo(tag.Trim().TrimStart('v', 'V'), tag, url, size);
            Available = info;
            return info;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return null;
        }
    }

    // ---------------- 应用 ----------------

    /// <summary>
    /// 下载 + 写替换脚本 + 启动脚本。调用方随后自行退出（脚本会等几秒再 move）。
    /// 在 dotnet host 下抛异常 —— 见 <see cref="CanApply"/>。
    /// </summary>
    public static void Apply(UpdateInfo u)
    {
        string targetExe = Environment.ProcessPath
            ?? throw new InvalidOperationException("ProcessPath 为空");
        if (!CanApply) throw new InvalidOperationException("dotnet host 下拒绝替换（开发/探针场景）");

        string tempDir = Path.GetTempPath();
        string newExe = Path.Combine(tempDir, "WMonitor.new.exe");
        if (File.Exists(newExe)) File.Delete(newExe);

        using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8)))
        using (var resp = Http.GetAsync(u.DownloadUrl, cts.Token).GetAwaiter().GetResult())
        {
            resp.EnsureSuccessStatusCode();
            using var src = resp.Content.ReadAsStream(cts.Token);
            using var dst = File.Create(newExe);
            src.CopyTo(dst);
        }
        if (new FileInfo(newExe).Length < 1024 * 1024)
            throw new IOException($"下载不完整（{new FileInfo(newExe).Length} 字节）");

        bool frameworkMode = File.Exists(Path.Combine(Path.GetDirectoryName(targetExe) ?? ".", "WMonitor.deps.json"));
        string script = BuildReplaceScript(newExe, targetExe, frameworkMode);
        string scriptPath = Path.Combine(tempDir, "WMonitor.update.cmd");
        File.WriteAllText(scriptPath, script);

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{scriptPath}\"",
            WorkingDirectory = tempDir,
            CreateNoWindow = true,
            UseShellExecute = false
        });
    }

    /// <summary>
    /// 替换脚本内容（纯函数，单测覆盖）。 Windows 不允许删除/覆盖运行中的 exe，
    /// 且 %TEMP%(C:) 到安装盘(D:) 的 move 是跨卷 copy+delete —— 同样会被拒。
    /// 用「ren 运行中的 exe（改名允许）→ copy 新 exe 进来 → 旧文件留 .old」：
    /// 下一次更新先 del 旧 .old 再 ren（此时旧进程早已退出）；app 启动时也会顺手清一次。
    /// </summary>
    internal static string BuildReplaceScript(string newExe, string targetExe, bool cleanupFrameworkFiles)
    {
        var dir = Path.GetDirectoryName(targetExe) ?? ".";
        var old = Path.Combine(dir, "WMonitor.old.exe");
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("ping -n 5 127.0.0.1 >nul");
        sb.AppendLine($"del /q \"{old}\" >nul 2>&1");
        sb.AppendLine($"ren \"{targetExe}\" \"WMonitor.old.exe\" >nul 2>&1");
        sb.AppendLine($"copy /y \"{newExe}\" \"{targetExe}\" >nul");
        if (cleanupFrameworkFiles)
            sb.AppendLine($"del /q \"{Path.Combine(dir, "WMonitor.dll")}\" \"{Path.Combine(dir, "WMonitor.deps.json")}\" \"{Path.Combine(dir, "WMonitor.runtimeconfig.json")}\" >nul 2>&1");
        sb.AppendLine($"start \"\" \"{targetExe}\"");
        sb.AppendLine($"del /q \"{newExe}\" >nul 2>&1");
        sb.AppendLine("del \"%~f0\"");
        return sb.ToString();
    }
}
