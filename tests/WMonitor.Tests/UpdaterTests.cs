using Xunit;
using WMonitor;

namespace WMonitor.Tests;

/// <summary>
/// 自动更新的纯逻辑部分（v0.32）：版本口径 + 替换脚本内容。
/// 网络与文件 IO 不在此覆盖（--update-check 探针管端到端检查，真实替换流程留用户手动触发）。
/// </summary>
public class UpdaterTests
{
    // ---------------- ParseVersion ----------------

    [Theory]
    [InlineData("v0.31", 0, 31, 0)]
    [InlineData("v0.31.2", 0, 31, 2)]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("V1.0", 1, 0, 0)]
    [InlineData("v10.20.30", 10, 20, 30)]
    public void ParseVersion_Valid(string tag, int m, int mi, int p) =>
        Assert.Equal((m, mi, p), Updater.ParseVersion(tag));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v")]
    [InlineData("vX.2")]
    [InlineData("1.2.3.4")]
    [InlineData("v0.31-beta")]
    [InlineData("v-1.0")]
    public void ParseVersion_Invalid_ReturnsNull(string? tag) =>
        Assert.Null(Updater.ParseVersion(tag));

    // ---------------- IsNewer（显式 current，与程序集版本解耦） ----------------

    [Fact]
    public void IsNewer_ByPatch() =>
        Assert.True(Updater.IsNewer("v0.31.1", (0, 31, 0)));

    [Fact]
    public void IsNewer_ByMinor() =>
        Assert.True(Updater.IsNewer("v0.32", (0, 31, 9)));

    [Fact]
    public void IsNewer_MajorWins() =>
        Assert.True(Updater.IsNewer("v1.0", (0, 99, 99)));

    [Fact]
    public void IsNewer_SameOrOlder_False()
    {
        Assert.False(Updater.IsNewer("v0.31", (0, 31, 0)));
        Assert.False(Updater.IsNewer("v0.31", (0, 31, 1)));    // 本地补丁号更高
        Assert.False(Updater.IsNewer("v0.30", (0, 31, 0)));
    }

    [Fact]
    public void IsNewer_InvalidTag_False() =>
        Assert.False(Updater.IsNewer("garbage", (0, 31, 0)));

    // ---------------- BuildReplaceScript ----------------

    [Fact]
    public void Script_RenRunningExeThenCopiesNew()
    {
        string s = Updater.BuildReplaceScript(@"C:\t\WMonitor.new.exe", @"D:\app\WMonitor.exe", false);
        // 旧 .old 先清（上次更新遗留），再 ren 运行中的 exe（Windows 只允许改名不允许删）
        Assert.Contains("del /q \"D:\\app\\WMonitor.old.exe\"", s);
        Assert.Contains("ren \"D:\\app\\WMonitor.exe\" \"WMonitor.old.exe\"", s);
        // copy 而非 move：跨卷 move = copy+delete，对运行中的 exe 会被拒
        Assert.Contains("copy /y \"C:\\t\\WMonitor.new.exe\" \"D:\\app\\WMonitor.exe\"", s);
        Assert.Contains("start \"\" \"D:\\app\\WMonitor.exe\"", s);
        Assert.Contains("del \"%~f0\"", s);
        Assert.Contains("ping -n 5", s);
        Assert.DoesNotContain("WMonitor.deps.json", s);        // 非 framework 安装不清旧清单
    }

    [Fact]
    public void Script_FrameworkCleanup_DeletesStaleManifests()
    {
        string s = Updater.BuildReplaceScript(@"C:\t\WMonitor.new.exe", @"D:\app\WMonitor.exe", true);
        Assert.Contains("WMonitor.deps.json", s);
        Assert.Contains("WMonitor.dll", s);
        Assert.Contains("WMonitor.runtimeconfig.json", s);
    }

    /// <summary>targetExe 里的空格（Program Files 类路径）必须被引号包住。</summary>
    [Fact]
    public void Script_SpacesInPaths_Quoted()
    {
        string s = Updater.BuildReplaceScript(@"C:\my tmp\WMonitor.new.exe", @"C:\Program Files\WMonitor\WMonitor.exe", false);
        Assert.Contains("\"C:\\Program Files\\WMonitor\\WMonitor.exe\"", s);
        Assert.Contains("\"C:\\my tmp\\WMonitor.new.exe\"", s);
    }
}
