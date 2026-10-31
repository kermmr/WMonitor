using Microsoft.Win32;

namespace WMonitor.UI;

public sealed class Theme
{
    public bool IsDark { get; init; }

    public Color Cpu { get; init; }
    public Color Ram { get; init; }
    public Color Gpu { get; init; }
    public Color Vram { get; init; }
    public Color Net { get; init; }

    public Color Text { get; init; }
    public Color Label { get; init; }
    public Color PillBg { get; init; }
    public Color PillBorder { get; init; }
    public Color Track { get; init; }
    public Color Sep { get; init; }
    public Color PanelBg { get; init; }

    /// <summary>告警色：贴片数字变红、托盘闪烁环都用它。</summary>
    public Color Alert { get; init; }

    /// <summary>WinForms 控件的 BackColor 必须是全不透明色。</summary>
    public Color PanelBgOpaque => Color.FromArgb(255, PanelBg.R, PanelBg.G, PanelBg.B);
    public Color Line => IsDark ? Color.FromArgb(255, 46, 50, 57) : Color.FromArgb(255, 224, 226, 231);
    public Color Hover { get; init; }

    private static readonly Color DarkCpu = Color.FromArgb(0x4C, 0xC2, 0xFF);
    private static readonly Color DarkRam = Color.FromArgb(0xB3, 0x9D, 0xFF);
    private static readonly Color DarkGpu = Color.FromArgb(0x3D, 0xDC, 0x97);
    private static readonly Color DarkVram = Color.FromArgb(0xFF, 0xB3, 0x40);
    private static readonly Color DarkNet = Color.FromArgb(0x6E, 0xE7, 0xD7);

    public static readonly Theme Dark = new()
    {
        IsDark = true,
        Cpu = DarkCpu,
        Ram = DarkRam,
        Gpu = DarkGpu,
        Vram = DarkVram,
        Net = DarkNet,
        Text = Color.FromArgb(0xEE, 0xF1, 0xF6),
        Label = Color.FromArgb(0x9A, 0xA3, 0xB2),
        PillBg = Color.FromArgb(246, 0x22, 0x24, 0x28),
        PillBorder = Color.FromArgb(38, 255, 255, 255),
        Track = Color.FromArgb(34, 255, 255, 255),
        Sep = Color.FromArgb(28, 255, 255, 255),
        PanelBg = Color.FromArgb(242, 0x18, 0x1B, 0x20),
        Hover = Color.FromArgb(30, 255, 255, 255),
        Alert = Color.FromArgb(0xFF, 0x45, 0x3A)
    };

    public static readonly Theme Light = new()
    {
        IsDark = false,
        Cpu = Color.FromArgb(0x0A, 0x84, 0xFF),
        Ram = Color.FromArgb(0x7C, 0x5C, 0xE0),
        Gpu = Color.FromArgb(0x11, 0xA3, 0x6B),
        Vram = Color.FromArgb(0xD1, 0x84, 0x00),
        Net = Color.FromArgb(0x0E, 0x9C, 0x94),
        Text = Color.FromArgb(0x14, 0x14, 0x14),
        Label = Color.FromArgb(0x5C, 0x63, 0x6E),
        PillBg = Color.FromArgb(248, 0xF4, 0xF4, 0xF4),
        PillBorder = Color.FromArgb(26, 0, 0, 0),
        Track = Color.FromArgb(26, 0, 0, 0),
        Sep = Color.FromArgb(24, 0, 0, 0),
        PanelBg = Color.FromArgb(246, 0xF7, 0xF8, 0xFA),
        Hover = Color.FromArgb(20, 0, 0, 0),
        Alert = Color.FromArgb(0xD7, 0x00, 0x15)
    };

    public static bool SystemUsesLight()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            // 任务栏跟随「Windows 模式」，不是「应用模式」
            object? v = k?.GetValue("SystemUsesLightTheme");
            if (v is int i) return i != 0;
        }
        catch { }
        return false;
    }

    public static Theme Resolve(ThemeMode mode) => mode switch
    {
        ThemeMode.Dark => Dark,
        ThemeMode.Light => Light,
        _ => SystemUsesLight() ? Light : Dark
    };
}
