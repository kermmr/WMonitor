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
    public Color Disk { get; init; }

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

    // ---- 设置窗口（v0.11 Bento 面板）：窗口底色比卡片深一档，卡片靠色差浮起来 ----
    /// <summary>设置窗口底色（卡片之外的地方）。</summary>
    public Color WindowBg { get; init; }
    /// <summary>卡片底色。</summary>
    public Color CardBg { get; init; }
    /// <summary>卡片 1px 描边。</summary>
    public Color CardBorder { get; init; }
    /// <summary>次要按钮底色（深色主题）。</summary>
    public Color Soft { get; init; }
    /// <summary>次要按钮底色（浅色主题）：要比窗口底色暗一档，否则按钮和背景糊在一起。</summary>
    public Color SoftLight { get; init; }
    /// <summary>主色（主按钮、选中态）。</summary>
    public Color Accent { get; init; }

    /// <summary>右键菜单底色：比卡片略亮一档、带一点透明度，压在任务栏/桌面上有层次。</summary>
    public Color MenuBg { get; init; }
    /// <summary>右键菜单 1px 描边。</summary>
    public Color MenuBorder { get; init; }

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
        Disk = Color.FromArgb(0xFF, 0x8F, 0xB1),
        Text = Color.FromArgb(0xEE, 0xF1, 0xF6),
        Label = Color.FromArgb(0x9A, 0xA3, 0xB2),
        PillBg = Color.FromArgb(246, 0x22, 0x24, 0x28),
        PillBorder = Color.FromArgb(38, 255, 255, 255),
        Track = Color.FromArgb(34, 255, 255, 255),
        Sep = Color.FromArgb(28, 255, 255, 255),
        PanelBg = Color.FromArgb(242, 0x18, 0x1B, 0x20),
        Hover = Color.FromArgb(30, 255, 255, 255),
        Alert = Color.FromArgb(0xFF, 0x45, 0x3A),
        WindowBg = Color.FromArgb(0xFF, 0x0E, 0x10, 0x14),
        CardBg = Color.FromArgb(0xFF, 0x19, 0x1C, 0x22),
        CardBorder = Color.FromArgb(18, 255, 255, 255),
        Soft = Color.FromArgb(0xFF, 0x26, 0x2A, 0x31),
        SoftLight = Color.FromArgb(0xFF, 0xDD, 0xE2, 0xE8),
        Accent = Color.FromArgb(0xFF, 0x0A, 0x84, 0xFF),
        MenuBg = Color.FromArgb(250, 0x1E, 0x22, 0x29),
        MenuBorder = Color.FromArgb(34, 255, 255, 255)
    };

    public static readonly Theme Light = new()
    {
        IsDark = false,
        Cpu = Color.FromArgb(0x0A, 0x84, 0xFF),
        Ram = Color.FromArgb(0x7C, 0x5C, 0xE0),
        Gpu = Color.FromArgb(0x11, 0xA3, 0x6B),
        Vram = Color.FromArgb(0xD1, 0x84, 0x00),
        Net = Color.FromArgb(0x0E, 0x9C, 0x94),
        Disk = Color.FromArgb(0xC2, 0x25, 0x5C),
        Text = Color.FromArgb(0x14, 0x14, 0x14),
        Label = Color.FromArgb(0x5C, 0x63, 0x6E),
        PillBg = Color.FromArgb(248, 0xF4, 0xF4, 0xF4),
        PillBorder = Color.FromArgb(26, 0, 0, 0),
        Track = Color.FromArgb(26, 0, 0, 0),
        Sep = Color.FromArgb(24, 0, 0, 0),
        PanelBg = Color.FromArgb(246, 0xF7, 0xF8, 0xFA),
        Hover = Color.FromArgb(20, 0, 0, 0),
        Alert = Color.FromArgb(0xD7, 0x00, 0x15),
        WindowBg = Color.FromArgb(0xFF, 0xED, 0xEF, 0xF3),
        CardBg = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
        CardBorder = Color.FromArgb(16, 0, 0, 0),
        Soft = Color.FromArgb(0xFF, 0xE8, 0xEA, 0xEE),
        SoftLight = Color.FromArgb(0xFF, 0xD8, 0xDE, 0xE6),
        Accent = Color.FromArgb(0xFF, 0x0A, 0x84, 0xFF),
        MenuBg = Color.FromArgb(250, 0xFB, 0xFC, 0xFE),
        MenuBorder = Color.FromArgb(26, 0, 0, 0)
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
