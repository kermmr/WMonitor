using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using WMonitor.Metrics;

namespace WMonitor.UI;

internal static class TrayIconRenderer
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private const string Font = "Segoe UI";

    public static Icon Render(TrayStyle style, MetricsSnapshot s, Theme th, int size)
    {
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            g.Clear(Color.Transparent);

            switch (style)
            {
                case TrayStyle.DualRing: DrawDualRing(g, s, th, size); break;
                case TrayStyle.FourBar: DrawFourBar(g, s, th, size); break;
                case TrayStyle.Single: DrawSingle(g, s, th, size); break;
                default: DrawDualNumber(g, s, th, size); break;
            }
        }

        IntPtr h = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(h);
            return (Icon)tmp.Clone();
        }
        finally { DestroyIcon(h); }
    }

    private static void DrawDualNumber(Graphics g, MetricsSnapshot s, Theme th, int size)
    {
        float k = size / 16f;
        var f = Gfx.F(Font, 7.4f * k, FontStyle.Bold);
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var b1 = new SolidBrush(th.Cpu);
        using var b2 = new SolidBrush(th.Gpu);
        using var line = new Pen(Color.FromArgb(70, th.Text), Math.Max(1f, k * 0.7f));

        var r1 = new RectangleF(0, -0.5f * k, size, size * 0.5f);
        var r2 = new RectangleF(0, size * 0.5f + 0.5f * k, size, size * 0.5f);
        g.DrawString(((int)Math.Round(s.CpuPercent)).ToString(), f, b1, r1, sf);
        g.DrawString(((int)Math.Round(s.GpuPercent)).ToString(), f, b2, r2, sf);
        g.DrawLine(line, 3 * k, size / 2f, size - 3 * k, size / 2f);
    }

    private static void DrawDualRing(Graphics g, MetricsSnapshot s, Theme th, int size)
    {
        float k = size / 16f;
        float pad = 1.4f * k;
        float outer = size - pad * 2;
        float inner = outer * 0.56f;

        using (var track = new Pen(Color.FromArgb(46, th.Text), 1.9f * k))
            g.DrawEllipse(track, pad, pad, outer, outer);
        using (var pen = new Pen(th.Cpu, 1.9f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(pen, pad, pad, outer, outer, -90, (float)(Math.Clamp(s.CpuPercent, 0, 100) * 3.6));

        float ip = pad + (outer - inner) / 2f;
        using (var track2 = new Pen(Color.FromArgb(46, th.Text), 1.9f * k))
            g.DrawEllipse(track2, ip, ip, inner, inner);
        using (var pen2 = new Pen(th.Gpu, 1.9f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(pen2, ip, ip, inner, inner, -90, (float)(Math.Clamp(s.GpuPercent, 0, 100) * 3.6));
    }

    private static void DrawFourBar(Graphics g, MetricsSnapshot s, Theme th, int size)
    {
        float k = size / 16f;
        float x = 1.5f * k, w = size - 3 * k, h = 2.6f * k;
        var vals = new[] { s.CpuPercent, s.RamPercent, s.GpuPercent, s.VramPercent };
        var cols = new[] { th.Cpu, th.Ram, th.Gpu, th.Vram };

        for (int i = 0; i < 4; i++)
        {
            float y = 1.2f * k + i * (h + 0.9f * k);
            using (var bg = new SolidBrush(Color.FromArgb(46, th.Text)))
                g.FillRectangle(bg, x, y, w, h);
            using (var fg = new SolidBrush(cols[i]))
                g.FillRectangle(fg, x, y, Math.Max(0.8f * k, (float)(Math.Clamp(vals[i], 0, 100) / 100.0) * w), h);
        }
    }

    private static void DrawSingle(Graphics g, MetricsSnapshot s, Theme th, int size)
    {
        float k = size / 16f;
        var f = Gfx.F(Font, 10.5f * k, FontStyle.Bold);
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var b = new SolidBrush(th.Text);
        g.DrawString(((int)Math.Round(s.CpuPercent)).ToString(), f, b,
            new RectangleF(0, -1 * k, size, size * 0.82f), sf);

        using (var track = new SolidBrush(Color.FromArgb(46, th.Text)))
            g.FillRectangle(track, 1.5f * k, size - 3.2f * k, size - 3 * k, 1.8f * k);
        using (var fg = new SolidBrush(th.Cpu))
            g.FillRectangle(fg, 1.5f * k, size - 3.2f * k,
                Math.Max(0.8f * k, (float)(Math.Clamp(s.CpuPercent, 0, 100) / 100.0) * (size - 3 * k)), 1.8f * k);
    }
}
