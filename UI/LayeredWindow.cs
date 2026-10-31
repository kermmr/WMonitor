using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using WMonitor.Interop;

namespace WMonitor.UI;

/// <summary>
/// 分层窗口：内容画到 32bpp ARGB 位图，用 UpdateLayeredWindow 推上去。
/// 好处是圆角边缘和半透明都是真 alpha，没有 Region 锯齿；也不抢焦点、不进 Alt-Tab。
/// </summary>
public class LayeredWindow : Form
{
    public LayeredWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        MinimizeBox = false;
        MaximizeBox = false;
        BackColor = Color.Black;
        SetStyle(ControlStyles.Opaque, true);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW;
            if (NoActivate) cp.ExStyle |= Native.WS_EX_NOACTIVATE;
            // 点击穿透：鼠标事件直接落到下面的窗口。分层窗口 + TRANSPARENT 是合法组合。
            if (ClickThrough) cp.ExStyle |= Native.WS_EX_TRANSPARENT;
            return cp;
        }
    }

    /// <summary>true = 不抢焦点（贴片）；false = 可激活、失焦自动关（详情面板）。</summary>
    public bool NoActivate { get; set; } = true;

    private bool _clickThrough;

    /// <summary>点击穿透（<c>WS_EX_TRANSPARENT</c>）。运行时切换要调 <see cref="Control.UpdateStyles"/> 才生效。</summary>
    public bool ClickThrough
    {
        get => _clickThrough;
        set
        {
            if (_clickThrough == value) return;
            _clickThrough = value;
            if (IsHandleCreated) UpdateStyles();
        }
    }

    protected override bool ShowWithoutActivation => true;

    /// <summary>把位图推到窗口的 (x,y)（屏幕坐标），尺寸跟随位图。</summary>
    protected bool Blit(Bitmap bmp, int x, int y)
    {
        if (!IsHandleCreated) CreateHandle();

        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        IntPtr memDc = Native.CreateCompatibleDC(screenDc);
        IntPtr hBmp = IntPtr.Zero;
        IntPtr old = IntPtr.Zero;
        bool ok = false;
        try
        {
            hBmp = bmp.GetHbitmap(Color.FromArgb(0));
            old = Native.SelectObject(memDc, hBmp);

            var dst = new Native.POINT(x, y);
            var size = new Native.SIZE(bmp.Width, bmp.Height);
            var src = new Native.POINT(0, 0);
            var blend = new Native.BLENDFUNCTION
            {
                BlendOp = Native.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = Native.AC_SRC_ALPHA
            };

            ok = Native.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, Native.ULW_ALPHA);
        }
        finally
        {
            if (old != IntPtr.Zero) Native.SelectObject(memDc, old);
            if (hBmp != IntPtr.Zero) Native.DeleteObject(hBmp);
            Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
        return ok;
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }

    // 分层窗口靠 UpdateLayeredWindow 呈现，屏蔽常规绘制
    protected override void WndProc(ref Message m)
    {
        const int WM_PAINT = 0x000F;
        const int WM_ERASEBKGND = 0x0014;
        if (m.Msg == WM_PAINT || m.Msg == WM_ERASEBKGND) return;
        base.WndProc(ref m);
    }
}

internal static class Gfx
{
    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private static readonly Dictionary<(string, float, FontStyle), Font> Fonts = new();

    public static Font F(string family, float px, FontStyle style)
    {
        var key = (family, px, style);
        if (Fonts.TryGetValue(key, out var f)) return f;
        f = new Font(family, px, style, GraphicsUnit.Pixel);
        Fonts[key] = f;
        return f;
    }

    public static void Text(Graphics g, string s, Font f, Color c, float x, float y)
    {
        using var b = new SolidBrush(c);
        g.DrawString(s, f, b, x, y, StringFormat.GenericTypographic);
    }

    public static SizeF Measure(Graphics g, string s, Font f)
    {
        var m = g.MeasureString(s, f, PointF.Empty, StringFormat.GenericTypographic);
        return new SizeF(m.Width, m.Height);
    }

    public static void DisposeFonts()
    {
        foreach (var f in Fonts.Values) f.Dispose();
        Fonts.Clear();
    }
}
