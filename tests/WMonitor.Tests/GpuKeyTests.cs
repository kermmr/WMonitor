using Xunit;
using WMonitor.Metrics;

namespace WMonitor.Tests;

/// <summary>
/// GPU 锁键三件套的口径测试（v0.30 引入，docs/05 §25 的坑 1）：
/// ParseLuid / NormalizeInstance / LuidBase 对 PDH 原始实例名与归一化键必须给出一致结果。
/// 锁定功能的一切都建立在这三个函数口径统一之上。
/// </summary>
public class GpuKeyTests
{
    private const string Normalized = "0x00000000_0x00010FB0_phys_0";
    private const string RawAdapter = "luid_0x00000000_0x00010FB0_phys_0";
    private const string RawEngine = "pid_1208_luid_0x00000000_0x00010FB0_phys_0_eng_0_engtype_3D";

    // ---------------- MetricSampler.NormalizeInstance ----------------

    [Fact]
    public void NormalizeInstance_AdapterRaw() =>
        Assert.Equal(Normalized, MetricSampler.NormalizeInstance(RawAdapter));

    [Fact]
    public void NormalizeInstance_EngineRaw_StripsPidAndEng() =>
        Assert.Equal(Normalized, MetricSampler.NormalizeInstance(RawEngine));

    [Fact]
    public void NormalizeInstance_NoLuidSegment_Empty() =>
        Assert.Equal("", MetricSampler.NormalizeInstance("pid_1208_phys_0_eng_0_engtype_3D"));

    /// <summary>契约边界：只吃带 luid_ 段的原始实例名；归一化键再进去返回空（GpuLocator 只喂原始名，无影响）。</summary>
    [Fact]
    public void NormalizeInstance_AlreadyNormalized_OutOfContract() =>
        Assert.Equal("", MetricSampler.NormalizeInstance(Normalized));

    // ---------------- GpuLocator.ParseLuid ----------------

    [Fact]
    public void ParseLuid_NormalizedKey() =>
        Assert.Equal((0, 0x10FB0u), GpuLocator.ParseLuid(Normalized));

    [Fact]
    public void ParseLuid_RawAdapterKey() =>
        Assert.Equal((0, 0x10FB0u), GpuLocator.ParseLuid(RawAdapter));

    [Fact]
    public void ParseLuid_RawEngineKey() =>
        Assert.Equal((0, 0x10FB0u), GpuLocator.ParseLuid(RawEngine));

    [Fact]
    public void ParseLuid_NonZeroHighPart() =>
        Assert.Equal((0x1A2B, 0x10FB0u), GpuLocator.ParseLuid("luid_0x00001A2B_0x00010FB0_phys_0"));

    [Fact]
    public void ParseLuid_Garbage_ZeroPair()
    {
        Assert.Equal((0, 0u), GpuLocator.ParseLuid("garbage"));
        Assert.Equal((0, 0u), GpuLocator.ParseLuid(""));
        Assert.Equal((0, 0u), GpuLocator.ParseLuid("luid_0xZZZZ_phys_0"));
    }

    // ---------------- GpuProcessSampler.LuidBase ----------------

    [Fact]
    public void LuidBase_StripsPhysSegment() =>
        Assert.Equal("0x00000000_0x00010fb0", GpuProcessSampler.LuidBase("0x00000000_0x00010fb0_phys_0"));

    [Fact]
    public void LuidBase_CaseInsensitive() =>
        Assert.Equal("0x00000000_0x00010FB0", GpuProcessSampler.LuidBase("0x00000000_0x00010FB0_PHYS_0"));

    [Fact]
    public void LuidBase_NoPhysSegment_Passthrough() =>
        Assert.Equal("0x00000000_0x00010FB0", GpuProcessSampler.LuidBase("0x00000000_0x00010FB0"));

    /// <summary>锁定利用率聚合的匹配前提：adapter 键与 engine 键归一后 LUID base 一致。</summary>
    [Fact]
    public void LuidBase_AdapterAndEngineKeys_Agree()
    {
        var adapter = MetricSampler.NormalizeInstance(RawAdapter);
        var engine = MetricSampler.NormalizeInstance(RawEngine);
        Assert.Equal(GpuProcessSampler.LuidBase(adapter), GpuProcessSampler.LuidBase(engine));
    }
}
