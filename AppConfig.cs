using System.Text.Json;
using System.Text.Json.Serialization;
using WMonitor.Metrics;

namespace WMonitor;

public enum StripStyle { Numbers = 0, Bars = 1, Rings = 2 }
public enum TrayStyle { DualNumber = 0, DualRing = 1, FourBar = 2, Single = 3 }
public enum ThemeMode { Dark = 0, Light = 1, System = 2 }

/// <summary>托盘图标可以显示的指标。</summary>
public enum TrayMetric { Cpu = 0, Ram = 1, Gpu = 2, Vram = 3, Net = 4, Temp = 5 }

/// <summary>贴片锚点。</summary>
public enum StripAnchor
{
    TaskbarLeft = 0,    // 任务栏最左
    BeforeApps = 1,     // 应用图标区左边
    AfterApps = 2,      // 应用图标区右边
    BeforeTray = 3,     // 托盘箭头左边（默认）
    Free = 4            // 自定义：拖到任意位置（任务栏上或桌面）
}

public sealed class AppConfig
{
    public StripStyle Strip { get; set; } = StripStyle.Numbers;
    public TrayStyle Tray { get; set; } = TrayStyle.DualNumber;
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public int RefreshMs { get; set; } = 1000;

    public bool ShowCpu { get; set; } = true;
    public bool ShowRam { get; set; } = true;
    public bool ShowGpu { get; set; } = true;
    public bool ShowVram { get; set; } = true;
    public bool ShowNet { get; set; } = false;
    public bool ShowTemp { get; set; } = false;

    /// <summary>是否显示任务栏贴片（关掉后只剩托盘图标）。</summary>
    public bool ShowStrip { get; set; } = true;

    /// <summary>托盘图标主指标：双数字/双环取两个、单指标取这一个、四段条取第一个。</summary>
    public TrayMetric TrayPrimary { get; set; } = TrayMetric.Cpu;
    public TrayMetric TraySecondary { get; set; } = TrayMetric.Gpu;
    /// <summary>四段条的第三、第四条。</summary>
    public TrayMetric TrayThird { get; set; } = TrayMetric.Ram;
    public TrayMetric TrayFourth { get; set; } = TrayMetric.Vram;

    public bool StartWithWindows { get; set; } = false;
    public int OffsetX { get; set; } = 0;

    public StripAnchor Anchor { get; set; } = StripAnchor.BeforeTray;
    public int FreeX { get; set; } = -1;
    public int FreeY { get; set; } = -1;
    public bool AlwaysOnTop { get; set; } = true;

    /// <summary>贴片所在显示器设备名（如 <c>\\.\DISPLAY2</c>）。空 = 主屏。</summary>
    public string StripMonitor { get; set; } = "";

    /// <summary>每块屏各贴一片（多贴片模式下自由坐标不持久化）。</summary>
    public bool ShowOnAllMonitors { get; set; } = false;

    // ---------------- 历史 ----------------

    /// <summary>历史总开关。关掉后不采、不存、不落盘，面板退回 v0.04 的 60 点实时缓冲。</summary>
    public bool HistoryEnabled { get; set; } = true;

    /// <summary>跨重启保留：落盘到 %APPDATA%\WMonitor\history。关掉后只有内存 1 小时。</summary>
    public bool HistoryPersist { get; set; } = true;

    /// <summary>保留时长（小时）：6 / 24 / 72。</summary>
    public int HistoryRetentionHours { get; set; } = 24;

    /// <summary>面板默认时间窗（分钟）：1 / 5 / 60 / 1440。</summary>
    public int ChartWindowMinutes { get; set; } = 1;

    // ---------------- 告警 ----------------

    /// <summary>告警总开关。关闭时 AlertEngine 不参与计算，零开销。</summary>
    public bool AlertsEnabled { get; set; } = false;

    /// <summary>5 条固定规则（按 Metric 归位，数组长度与顺序都不被信任）。</summary>
    public AlertRule[] AlertRules { get; set; } = AlertRule.Defaults();

    public bool AlertStrip { get; set; } = true;      // 贴片数字变红
    public bool AlertTrayFlash { get; set; } = true;  // 托盘图标闪烁
    public bool AlertToast { get; set; } = true;      // 系统通知
    public bool AlertScript { get; set; } = false;    // 运行脚本
    public string? AlertScriptPath { get; set; }

    /// <summary>贴片底色与描边的不透明度 0–100。</summary>
    public int StripOpacity { get; set; } = 100;
    public string? LockedGpuLuid { get; set; }

    /// <summary>当前托盘样式实际用到的指标槽位（单指标 1 个、双样式 2 个、四段条 4 个）。</summary>
    [JsonIgnore]
    public TrayMetric[] TraySlots => Tray switch
    {
        TrayStyle.Single => new[] { TrayPrimary },
        TrayStyle.FourBar => new[] { TrayPrimary, TraySecondary, TrayThird, TrayFourth },
        _ => new[] { TrayPrimary, TraySecondary }
    };

    [JsonIgnore]
    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WMonitor");

    [JsonIgnore]
    public static string FilePath => Path.Combine(Dir, "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), JsonOpts);
                if (cfg != null) return cfg;
            }
        }
        catch { }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { }
    }

    public AppConfig Clone() => (AppConfig)MemberwiseClone();

    public bool AnyStripField =>
        ShowCpu || ShowRam || ShowGpu || ShowVram || ShowNet || ShowTemp;
}
