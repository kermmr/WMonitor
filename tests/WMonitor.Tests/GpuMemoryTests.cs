using Xunit;
using WMonitor.Metrics;

namespace WMonitor.Tests;

/// <summary>
/// GPU Process Memory 实例聚合（v0.35 修复）：610.47 驱动的实例带 _eng_N_engtype_X 段且每引擎
/// 一份相同数值，老实现按实例累加把进程显存放大 ~30 倍（适配器 3857MB 报成 109GB）。
/// 修复 = (pid|luid) 组内取 max、组间求和；这里覆盖去重、跨卡求和与旧式无 luid 实例。
/// </summary>
public class GpuMemoryTests
{
    private static Dictionary<int, long> Finalize(params (string Name, double Bytes)[] items)
    {
        var groups = new Dictionary<string, long>();
        foreach (var (name, bytes) in items)
            GpuProcessSampler.AccumulateMemory(name, bytes, groups);
        var byPid = new Dictionary<int, long>();
        GpuProcessSampler.FinalizeMemory(groups, byPid, null, null);
        return byPid;
    }

    [Fact]
    public void DuplicateEngtypeInstances_TakeMaxNotSum()
    {
        // 同一 (pid,luid) 按引擎类型重复：每份都是进程-适配器总量
        var byPid = Finalize(
            ("pid_1716_luid_0x00000000_0x000128F3_phys_0_eng_0_engtype_3D", 3857L * 1048576),
            ("pid_1716_luid_0x00000000_0x000128F3_phys_0_eng_1_engtype_Copy", 3857L * 1048576),
            ("pid_1716_luid_0x00000000_0x000128F3_phys_0_eng_2_engtype_VideoDecode", 3857L * 1048576));
        Assert.Equal(3857L * 1048576, byPid[1716]);
    }

    [Fact]
    public void CrossLuid_SumsGroupMaxes()
    {
        long a = 4000L * 1048576, b = 500L * 1048576;
        var byPid = Finalize(
            ($"pid_10_luid_0x0_0xA_phys_0_eng_0_engtype_3D", a),
            ($"pid_10_luid_0x0_0xA_phys_0_eng_1_engtype_Cuda", a),        // 同卡重复 → max
            ($"pid_10_luid_0x0_0xB_phys_0_eng_0_engtype_3D", b));         // 另一张卡 → 求和
        Assert.Equal(a + b, byPid[10]);
    }

    [Fact]
    public void LegacyInstance_WithoutLuid_CountsByPid()
    {
        var byPid = Finalize(("pid_42_phys_0_eng_0_engtype_3D", 123456));
        Assert.Equal(123456, byPid[42]);
    }

    [Fact]
    public void NonPidOrInvalid_Names_Ignored()
    {
        var byPid = Finalize(
            ("luid_0x0_0xA_phys_0_eng_0_engtype_3D", 999),
            ("pid_x_eng_0", 999),
            ("pid_-1", 999));
        Assert.Empty(byPid);
    }

    [Fact]
    public void DifferentValues_InSameGroup_TakeMax()
    {
        // 引擎实例间数值抖动（采样窗不同步）：max 兜底，不叠加
        var byPid = Finalize(
            ("pid_7_luid_0x0_0xA_phys_0_eng_0_engtype_3D", 1000),
            ("pid_7_luid_0x0_0xA_phys_0_eng_1_engtype_3D", 1010));
        Assert.Equal(1010, byPid[7]);
    }
}
