using System.Diagnostics;

namespace WMonitor.Metrics;

/// <summary>可告警的指标。顺序固定（数组下标即枚举值），不要插在中间。</summary>
public enum AlertMetric { Cpu = 0, Ram = 1, Gpu = 2, Vram = 3, GpuTemp = 4 }

/// <summary>一条告警规则。固定 5 条内置，不做动态增删（成本是固定表的 4 倍，收益不大）。</summary>
public sealed class AlertRule
{
    public AlertMetric Metric { get; set; }
    public bool Enabled { get; set; }
    public double Threshold { get; set; }        // 百分比或 °C
    public int DurationSec { get; set; } = 5;    // 持续越线多久才触发
    public int CooldownSec { get; set; } = 300;  // 触发后静默期

    public static AlertRule[] Defaults() => new[]
    {
        new AlertRule { Metric = AlertMetric.Cpu,     Threshold = 90, DurationSec = 30 },
        new AlertRule { Metric = AlertMetric.Ram,     Threshold = 90, DurationSec = 60 },
        new AlertRule { Metric = AlertMetric.Gpu,     Threshold = 95, DurationSec = 10 },
        new AlertRule { Metric = AlertMetric.Vram,    Threshold = 95, DurationSec = 10 },
        new AlertRule { Metric = AlertMetric.GpuTemp, Threshold = 85, DurationSec = 5  },
    };

    public AlertRule Clone() => (AlertRule)MemberwiseClone();
}

/// <summary>本 tick 新触发的告警（动作派发用）。</summary>
public readonly record struct AlertEvent(AlertMetric Metric, double Value, double Threshold);

/// <summary>每 tick 的告警快照（UI 着色用）。数组按枚举下标，避免每秒 new HashSet。</summary>
public sealed class AlertView
{
    public readonly bool[] Firing = new bool[5];
    public readonly List<AlertEvent> Events = new(2);

    public bool AnyFiring
    {
        get
        {
            foreach (bool b in Firing) if (b) return true;
            return false;
        }
    }

    public bool IsFiring(AlertMetric m) => Firing[(int)m];

    public static AlertView Empty { get; } = new();
}

/// <summary>
/// 阈值告警状态机：Idle → Armed（越线计时）→ Firing（派发一次）。
/// 迟滞（恢复线比触发线低 5% / 3°C）防抖动；冷却期只压派发、不压状态（贴片仍然红）。
/// 全在 UI 线程调用。
/// </summary>
public sealed class AlertEngine
{
    private const int N = 5;

    private readonly AlertRule[] _rules = new AlertRule[N];
    private readonly long[] _armedAt = new long[N];     // 0 = 未武装
    private readonly long[] _lastFired = new long[N];   // 0 = 从未触发
    private readonly bool[] _firing = new bool[N];
    private readonly AlertView _view = new();

    public bool Enabled { get; private set; }

    /// <summary>静音：只停派发，<see cref="AlertView.Firing"/> 照常维护（贴片还是红的）。</summary>
    public bool Muted { get; set; }

    /// <summary>把配置里的规则按 <see cref="AlertRule.Metric"/> 归位；缺的补默认，不信任数组长度与顺序。</summary>
    public void Configure(AppConfig cfg)
    {
        Enabled = cfg.AlertsEnabled;
        var defaults = AlertRule.Defaults();
        for (int i = 0; i < N; i++) _rules[i] = defaults[i].Clone();

        if (cfg.AlertRules != null)
            foreach (var r in cfg.AlertRules)
            {
                if (r == null) continue;
                int i = (int)r.Metric;
                if (i < 0 || i >= N) continue;
                _rules[i] = r.Clone();
            }

        for (int i = 0; i < N; i++)
        {
            _rules[i].Metric = (AlertMetric)i;
            _rules[i].Threshold = Math.Clamp(_rules[i].Threshold, 1, 200);
            _rules[i].DurationSec = Math.Clamp(_rules[i].DurationSec, 1, 3600);
            _rules[i].CooldownSec = Math.Clamp(_rules[i].CooldownSec, 1, 86400);
        }
    }

    public AlertRule Rule(AlertMetric m) => _rules[(int)m];

    public AlertView Evaluate(MetricsSnapshot s, long nowTicks)
    {
        _view.Events.Clear();
        Array.Clear(_view.Firing);

        if (!Enabled) return _view;

        for (int i = 0; i < N; i++)
        {
            var r = _rules[i];
            double v = Value((AlertMetric)i, s);

            if (!r.Enabled || double.IsNaN(v))
            {
                _armedAt[i] = 0;
                _firing[i] = false;
                continue;
            }

            bool above = v >= r.Threshold;
            bool below = v < r.Threshold - Hysteresis((AlertMetric)i);

            if (above)
            {
                if (_armedAt[i] == 0) _armedAt[i] = nowTicks;
                double held = (nowTicks - _armedAt[i]) / (double)Stopwatch.Frequency;

                if (held >= r.DurationSec && !_firing[i])
                {
                    _firing[i] = true;
                    bool cooled = _lastFired[i] == 0 ||
                                  (nowTicks - _lastFired[i]) / (double)Stopwatch.Frequency >= r.CooldownSec;
                    if (cooled)
                    {
                        _lastFired[i] = nowTicks;
                        if (!Muted) _view.Events.Add(new AlertEvent((AlertMetric)i, v, r.Threshold));
                    }
                }
            }
            else if (below)
            {
                // 落到恢复线以下才算解除；迟滞带内保持原状态，避免阈值抖动来回跳
                _armedAt[i] = 0;
                _firing[i] = false;
            }

            _view.Firing[i] = _firing[i];
        }

        return _view;
    }

    /// <summary>清掉全部状态（改规则后调用，避免旧武装时间影响新阈值）。</summary>
    public void Reset()
    {
        Array.Clear(_armedAt);
        Array.Clear(_lastFired);
        Array.Clear(_firing);
    }

    /// <summary>百分比 5 个百分点、温度 3 °C。</summary>
    private static double Hysteresis(AlertMetric m) => m == AlertMetric.GpuTemp ? 3 : 5;

    private static double Value(AlertMetric m, MetricsSnapshot s) => m switch
    {
        AlertMetric.Ram => s.RamPercent,
        AlertMetric.Gpu => s.HasGpu ? s.GpuPercent : double.NaN,
        AlertMetric.Vram => s.HasVramTotal ? s.VramPercent : double.NaN,
        AlertMetric.GpuTemp => s.HasTemp ? s.GpuTempC : double.NaN,
        _ => s.CpuPercent,
    };

    public static string Name(AlertMetric m) => m switch
    {
        AlertMetric.Ram => "内存",
        AlertMetric.Gpu => "GPU",
        AlertMetric.Vram => "显存",
        AlertMetric.GpuTemp => "GPU 温度",
        _ => "CPU",
    };

    public static string Unit(AlertMetric m) => m == AlertMetric.GpuTemp ? "°C" : "%";
}
