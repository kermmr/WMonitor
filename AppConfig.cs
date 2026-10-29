using System.Text.Json;
using System.Text.Json.Serialization;

namespace WMonitor;

public enum StripStyle { Numbers = 0, Bars = 1, Rings = 2 }
public enum TrayStyle { DualNumber = 0, DualRing = 1, FourBar = 2, Single = 3 }
public enum ThemeMode { Dark = 0, Light = 1, System = 2 }

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

    public bool StartWithWindows { get; set; } = false;
    public int OffsetX { get; set; } = 0;

    public StripAnchor Anchor { get; set; } = StripAnchor.BeforeTray;
    public int FreeX { get; set; } = -1;
    public int FreeY { get; set; } = -1;
    public bool AlwaysOnTop { get; set; } = true;

    /// <summary>贴片底色与描边的不透明度 0–100。</summary>
    public int StripOpacity { get; set; } = 100;
    public string? LockedGpuLuid { get; set; }

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
