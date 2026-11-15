using Xunit;
using WMonitor.Metrics;

namespace WMonitor.Tests;

/// <summary>
/// 详情面板「TOP 进程」选择逻辑（v0.35）：排序键取值、展示文本、榜单过滤（系统项不入榜、
/// 零值不入榜、降序、截断 n）。绘制与点击链路由 --panel-top-probe 晨检截图守。
/// </summary>
public class PanelTopTests
{
    private static AppMetric App(string key, double cpu = 0, double gpu = 0, long vram = 0, bool system = false) =>
        new()
        {
            Key = key,
            Name = key,
            Main = new ProcessMetric { Pid = 100, Name = key },
            Members = new List<ProcessMetric>(),
            CpuPercent = cpu,
            GpuPercent = gpu,
            GpuDedicatedBytes = vram,
            IsSystem = system
        };

    // ---------------- Value ----------------

    [Fact]
    public void Value_MapsSortKey()
    {
        var a = App("x", cpu: 10, gpu: 20, vram: 3000);
        Assert.Equal(10, PanelTopList.Value(a, PanelTopSort.Cpu));
        Assert.Equal(20, PanelTopList.Value(a, PanelTopSort.Gpu));
        Assert.Equal(3000, PanelTopList.Value(a, PanelTopSort.Vram));
    }

    // ---------------- ValueText ----------------

    [Theory]
    [InlineData(0.4, "0.4%")]
    [InlineData(12.34, "12.3%")]
    [InlineData(100, "100%")]
    public void ValueText_Percent(double v, string want) =>
        Assert.Equal(want, PanelTopList.ValueText(App("x", cpu: v), PanelTopSort.Cpu));

    [Fact]
    public void ValueText_Vram_GbAndMb()
    {
        Assert.Equal("1.5 GB", PanelTopList.ValueText(App("x", vram: (long)(1.5 * 1073741824)), PanelTopSort.Vram));
        Assert.Equal("512 MB", PanelTopList.ValueText(App("x", vram: 512 * 1048576), PanelTopSort.Vram));
    }

    // ---------------- Pick ----------------

    [Fact]
    public void Pick_SortsDescending_AndCapsN()
    {
        var apps = new[] { App("a", cpu: 1), App("b", cpu: 9), App("c", cpu: 5), App("d", cpu: 7) };
        var top = PanelTopList.Pick(apps, PanelTopSort.Cpu, 3);
        Assert.Equal(new[] { "b", "d", "c" }, top.Select(a => a.Key));
    }

    [Fact]
    public void Pick_SkipsSystem_AndZero()
    {
        var apps = new[]
        {
            App("sys", cpu: 50, system: true),
            App("idle", cpu: 0),
            App("real", cpu: 3)
        };
        var top = PanelTopList.Pick(apps, PanelTopSort.Cpu, 3);
        var app = Assert.Single(top);
        Assert.Equal("real", app.Key);
    }

    [Fact]
    public void Pick_Vram_KeepsOnlyHolders()
    {
        var apps = new[] { App("nogpu", cpu: 20), App("holder", vram: 1073741824) };
        var top = PanelTopList.Pick(apps, PanelTopSort.Vram, 3);
        var app = Assert.Single(top);
        Assert.Equal("holder", app.Key);
    }

    [Fact]
    public void Pick_EmptyInput_EmptyList() =>
        Assert.Empty(PanelTopList.Pick(Array.Empty<AppMetric>(), PanelTopSort.Cpu, 3));
}
