using System.Diagnostics;
using Xunit;
using WMonitor.Metrics;

namespace WMonitor.Tests;

/// <summary>
/// 告警状态机离线测试（与 --alert-test 同源场景，但以 xunit 断言落进 CI）：
/// 武装 → 触发 → 迟滞内保持 → 跌穿解除 → 冷却抑制 → 冷却后再触发，以及配置按 Metric 归位。
/// 时间线从 1s 起步 —— <c>_armedAt==0</c> 是「未武装」哨兵，t=0 拍的武装会被吞（app 侧无害，
/// 下一拍自动补；测试避开即可）。
/// </summary>
public class AlertEngineTests
{
    private static readonly long Freq = Stopwatch.Frequency;

    private static AppConfig Cfg(Action<AlertRule>? tweak = null)
    {
        var cfg = new AppConfig { AlertsEnabled = true };
        foreach (var r in cfg.AlertRules) r.Enabled = false;
        var cpu = cfg.AlertRules[(int)AlertMetric.Cpu];
        cpu.Enabled = true;
        cpu.Threshold = 90;
        cpu.DurationSec = 5;
        cpu.CooldownSec = 10;
        tweak?.Invoke(cpu);
        return cfg;
    }

    private static MetricsSnapshot Snap(double cpu) => new() { CpuPercent = cpu, HasGpu = true };

    private static AlertEngine NewEngine(AppConfig cfg) { var e = new AlertEngine(); e.Configure(cfg); return e; }

    /// <summary>先在 t=1s 武装一拍 —— 引擎首次 above 只武装不触发，点火测试都要先走这拍。</summary>
    private static void Arm(AlertEngine e) => e.Evaluate(Snap(95), Freq);

    [Fact]
    public void Armed_DurationMet_Fires()
    {
        var e = NewEngine(Cfg());
        Arm(e);
        long t = Freq;                                             // 1s：武装
        Assert.Empty(e.Evaluate(Snap(95), t).Events);
        t += Freq / 2;                                             // held=0.5s
        Assert.Empty(e.Evaluate(Snap(95), t).Events);
        t += Freq;                                                 // held=1.5s
        Assert.Empty(e.Evaluate(Snap(95), t).Events);
        t = 6 * Freq;                                              // held=5s（武装点 1s）
        var v = e.Evaluate(Snap(95), t);
        Assert.Single(v.Events);
        Assert.True(v.IsFiring(AlertMetric.Cpu));
    }

    [Fact]
    public void WithinHysteresis_KeepsFiring_NoRefire()
    {
        var e = NewEngine(Cfg());
        Arm(e);
        long t = 6 * Freq;
        Assert.Single(e.Evaluate(Snap(95), t).Events);             // 触发
        t += Freq;
        var v = e.Evaluate(Snap(87), t);                           // 87 ∈ [85, 90) 迟滞带：仍在烧
        Assert.True(v.IsFiring(AlertMetric.Cpu));
        Assert.Empty(v.Events);                                    // 不重复派发
    }

    [Fact]
    public void BelowHysteresis_Disarms_ReArmNeedsFullDuration()
    {
        var e = NewEngine(Cfg());
        Arm(e);
        long t = 6 * Freq;
        Assert.Single(e.Evaluate(Snap(95), t).Events);
        t += Freq;
        Assert.False(e.Evaluate(Snap(80), t).IsFiring(AlertMetric.Cpu));   // < 85 迟滞下限 → 解除
        t += 3 * Freq;                                             // 重新武装（10s）
        Assert.Empty(e.Evaluate(Snap(95), t).Events);              // held=0
        t += 2 * Freq;                                             // held=2s，不满 5s
        Assert.Empty(e.Evaluate(Snap(95), t).Events);
    }

    [Fact]
    public void AfterCooldown_FiresAgain()
    {
        var e = NewEngine(Cfg());
        Arm(e);
        long t = 6 * Freq;
        Assert.Single(e.Evaluate(Snap(95), t).Events);             // 触发（lastFired=6s）
        t += 11 * Freq;                                            // t=17s
        Assert.False(e.Evaluate(Snap(80), t).IsFiring(AlertMetric.Cpu));   // 解除 + 重置武装
        t += 5 * Freq;                                             // t=22s：重新武装
        Assert.Empty(e.Evaluate(Snap(95), t).Events);              // held=0
        t += 5 * Freq;                                             // t=27s：held 满 5s；距首触发 21s ≥ 冷却 10s
        Assert.Single(e.Evaluate(Snap(95), t).Events);             // 再触发
    }

    [Fact]
    public void DisabledEngine_NoEvents()
    {
        var cfg = Cfg();
        cfg.AlertsEnabled = false;
        var e = NewEngine(cfg);
        Assert.Empty(e.Evaluate(Snap(95), 6 * Freq).Events);
        Assert.Empty(e.Evaluate(Snap(95), 60 * Freq).Events);
    }

    /// <summary>Muted 只停派发：Events 不出，Firing 照常（贴片红环仍要闪）。</summary>
    [Fact]
    public void Muted_KeepsFiring_SuppressesEvents()
    {
        var e = NewEngine(Cfg());
        Arm(e);
        e.Muted = true;
        var v = e.Evaluate(Snap(95), 6 * Freq);
        Assert.True(v.IsFiring(AlertMetric.Cpu));
        Assert.Empty(v.Events);
    }

    /// <summary>配置数组被手改顺序 / 长度不足时按 Metric 归位 + 补齐（docs/05 §21 的坑）。</summary>
    [Fact]
    public void Configure_NormalizesByMetric_AndPads()
    {
        var cfg = new AppConfig { AlertsEnabled = true };
        cfg.AlertRules = new AlertRule[2]                          // 只有 2 条且乱序
        {
            new() { Metric = AlertMetric.GpuTemp, Enabled = true, Threshold = 88 },
            new() { Metric = AlertMetric.Cpu, Enabled = true, Threshold = 70 }
        };

        var e = NewEngine(cfg);
        Assert.Equal(70, e.Rule(AlertMetric.Cpu).Threshold);       // CPU 归位到 CPU 槽
        Assert.Equal(88, e.Rule(AlertMetric.GpuTemp).Threshold);
        Assert.False(e.Rule(AlertMetric.Ram).Enabled);             // 缺的补默认（默认关）
        foreach (var m in Enum.GetValues<AlertMetric>())
        {
            var r = e.Rule(m);
            Assert.InRange(r.Threshold, 1, 200);                   // 阈值钳制
            Assert.InRange(r.DurationSec, 1, 3600);
        }
    }
}
