using Xunit;
using WMonitor.Metrics;

namespace WMonitor.Tests;

/// <summary>
/// 网速分接口（v0.35）的选择器匹配。真实网卡枚举与统计走 MetricSampler（需系统环境，探针管），
/// 这里只覆盖纯匹配：Id 优先、名称兜底、大小写不敏感、空选择器永不命中（空 = 全部，由调用方处理）。
/// </summary>
public class NetNicTests
{
    private const string Id = "{A1B2C3D4-1111-2222-3333-444455556666}";
    private const string Name = "以太网";

    [Fact]
    public void Matches_ById_Exact() =>
        Assert.True(MetricSampler.NicMatches(Id, Id, Name));

    [Fact]
    public void Matches_ById_CaseInsensitive() =>
        Assert.True(MetricSampler.NicMatches(Id.ToLowerInvariant(), Id, Name));

    [Fact]
    public void Matches_ByName_Fallback() =>
        Assert.True(MetricSampler.NicMatches(Name, Id, Name));

    [Fact]
    public void NoMatch_OtherIdOrName()
    {
        Assert.False(MetricSampler.NicMatches("{FFFF-FFFF}", Id, Name));
        Assert.False(MetricSampler.NicMatches("WLAN", Id, Name));
    }

    [Fact]
    public void EmptySelector_NeverMatches() =>
        // 空 = 全部接口，由 ReadNetTotals 的 only==null 分支处理；匹配器不能把空当通配
        Assert.False(MetricSampler.NicMatches("", Id, Name));
}
