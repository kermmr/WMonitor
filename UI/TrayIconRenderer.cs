using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using WMonitor.Metrics;

namespace WMonitor.UI;

internal static class TrayIconRenderer
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private const string Font = "Segoe UI";

    /// <summary>一个指标在托盘图标里的呈现：文字 + 环形/进度填充比例 + 颜色。</summary>
    public readonly record struct Val(string Text, double Pct, Color Color);

    public static Icon Render(AppConfig cfg, MetricsSnapshot s, Theme th, int size)
    {
        // 4 倍超采样再缩回目标尺寸：16px 下直接画字，笔画粗细不匀、边缘发暗；
        // 超采样后 GDI+ 的亚像素信息被平均掉，数字明显更干净（见 --icons 对比图）。
        const int Ss = 4;
        int hi = size * Ss;
        using var big = new Bitmap(hi, hi, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(big))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            g.Clear(Color.Transparent);

            var vals = Values(cfg, s, th);
            switch (cfg.Tray)
            {
                case TrayStyle.DualRing: DrawDualRing(g, vals, th, hi); break;
                case TrayStyle.FourBar: DrawFourBar(g, vals, th, hi); break;
                case TrayStyle.Single: DrawSingle(g, vals[0], th, hi); break;
                default: DrawDualNumber(g, vals, th, hi); break;
            }
        }

        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g2 = Graphics.FromImage(bmp))
        {
            g2.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g2.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g2.SmoothingMode = SmoothingMode.HighQuality;
            g2.DrawImage(big, new Rectangle(0, 0, size, size));
        }
        BoostAlpha(bmp, 1.35f);   // 缩小会把笔画边缘的平均成半透明，提一点 alpha 才不发灰

        IntPtr h = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(h);
            return (Icon)tmp.Clone();
        }
        finally { DestroyIcon(h); }
    }

    /// <summary>托盘图标缓存键：样式 + 主题 + 每个槽位的文字与填充比例。</summary>
    public static string Key(AppConfig cfg, MetricsSnapshot s, Theme th)
    {
        var sb = new StringBuilder(48);
        sb.Append((int)cfg.Tray).Append('|').Append(th.IsDark ? 1 : 0);
        foreach (var v in Values(cfg, s, th))
            sb.Append('|').Append(v.Text).Append(':').Append((int)Math.Round(v.Pct));
        return sb.ToString();
    }

    private static Val[] Values(AppConfig cfg, MetricsSnapshot s, Theme th)
    {
        var slots = cfg.TraySlots;
        var arr = new Val[slots.Length];
        for (int i = 0; i < slots.Length; i++) arr[i] = Metric(slots[i], s, th);
        return arr;
    }

    public static Val Metric(TrayMetric m, MetricsSnapshot s, Theme th) => m switch
    {
        TrayMetric.Ram => Pct(s.RamPercent, th.Ram),
        TrayMetric.Gpu => Pct(s.GpuPercent, th.Gpu),
        TrayMetric.Vram => Pct(s.VramPercent, th.Vram),
        TrayMetric.Net => new Val(NetText(s.NetDownBps), NetPct(s.NetDownBps), th.Net),
        TrayMetric.Temp => new Val(TempText(s.GpuTempC), TempPct(s.GpuTempC), th.Gpu),
        _ => Pct(s.CpuPercent, th.Cpu),
    };

    private static Val Pct(double v, Color c)
    {
        double p = double.IsNaN(v) ? 0 : Math.Clamp(v, 0, 100);
        return new Val(((int)Math.Round(p)).ToString(), p, c);
    }

    private static string TempText(double t) => double.IsNaN(t) ? "—" : ((int)Math.Round(t)).ToString();

    /// <summary>温度环的填充比例：30 °C 起、100 °C 满。</summary>
    private static double TempPct(double t) => double.IsNaN(t) ? 0 : Math.Clamp((t - 30) / 70.0 * 100, 0, 100);

    /// <summary>
    /// 网速环的填充比例：网速没有百分比，用对数刻度 —— 20 KB/s ≈ 26%，1 MB/s ≈ 60%，100 MB/s 满环。
    /// 线性刻度会让日常几十 KB/s 的流量在环上完全看不出来。
    /// </summary>
    private static double NetPct(double bps)
    {
        double kb = Math.Max(0, bps) / 1024.0;
        return Math.Clamp(Math.Log10(kb + 1) / Math.Log10(100 * 1024 + 1) * 100, 0, 100);
    }

    private static string NetText(double bps)
    {
        double kb = bps / 1024.0;
        if (kb < 1) return "0";
        if (kb < 1000) return ((int)kb).ToString() + "K";
        double mb = kb / 1024.0;
        return mb < 10 ? mb.ToString("0.0") + "M" : ((int)mb).ToString() + "M";
    }

    // ---------------- 双数字 ----------------

    private static void DrawDualNumber(Graphics g, Val[] v, Theme th, int size)
    {
        float px = size * 0.58f;          // 16px 图标 → 9.3px 字（v0.03 只有 7.4px，看不清）
        DrawNum(g, v[0], size / 2f, size * 0.25f, px, size - 1.5f);
        DrawNum(g, v[1], size / 2f, size * 0.75f, px, size - 1.5f);
    }

    // ---------------- 双环 ----------------

    private static void DrawDualRing(Graphics g, Val[] v, Theme th, int size)
    {
        float pad = size * 0.09f;
        float outer = size - pad * 2;
        float inner = outer * 0.56f;
        float stroke = Math.Max(1.6f, size * 0.125f);

        using (var track = new Pen(Color.FromArgb(46, th.Text), stroke))
            g.DrawEllipse(track, pad, pad, outer, outer);
        using (var pen = new Pen(v[0].Color, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(pen, pad, pad, outer, outer, -90, (float)(Math.Clamp(v[0].Pct, 0, 100) * 3.6));

        float ip = pad + (outer - inner) / 2f;
        using (var track2 = new Pen(Color.FromArgb(46, th.Text), stroke))
            g.DrawEllipse(track2, ip, ip, inner, inner);
        using (var pen2 = new Pen(v[1].Color, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(pen2, ip, ip, inner, inner, -90, (float)(Math.Clamp(v[1].Pct, 0, 100) * 3.6));
    }

    // ---------------- 四段条 ----------------

    private static void DrawFourBar(Graphics g, Val[] v, Theme th, int size)
    {
        float x = size * 0.09f, w = size - x * 2;
        float h = size * 0.16f;
        float gap = size * 0.055f;
        float total = 4 * h + 3 * gap;
        float y0 = (size - total) / 2f;

        for (int i = 0; i < 4; i++)
        {
            var val = v[Math.Min(i, v.Length - 1)];
            float y = y0 + i * (h + gap);
            using (var bg = new SolidBrush(Color.FromArgb(46, th.Text)))
                g.FillRectangle(bg, x, y, w, h);
            using (var fg = new SolidBrush(val.Color))
                g.FillRectangle(fg, x, y, Math.Max(size * 0.06f, (float)(Math.Clamp(val.Pct, 0, 100) / 100.0) * w), h);
        }
    }

    // ---------------- 单指标 ----------------

    private static void DrawSingle(Graphics g, Val v, Theme th, int size)
    {
        float px = size * 0.80f;
        DrawNum(g, v, size / 2f, size * 0.36f, px, size - 1f);

        float barH = size * 0.11f;
        float y = size - size * 0.16f;
        using (var track = new SolidBrush(Color.FromArgb(46, th.Text)))
            g.FillRectangle(track, size * 0.09f, y, size * 0.82f, barH);
        using (var fg = new SolidBrush(v.Color))
            g.FillRectangle(fg, size * 0.09f, y,
                Math.Max(size * 0.05f, (float)(Math.Clamp(v.Pct, 0, 100) / 100.0) * (size * 0.82f)), barH);
    }

    // ---------------- 文字 ----------------
    /// <summary>
    /// 以 (cx, cy) 为数字的视觉中心画一行文字。
    /// GDI+ 的 y 是行盒顶（不是基线）：Segoe UI 基线在 1.08 em、数字高 0.70 em，
    /// 数字中心落在行盒顶下方约 0.73 em —— 反推 y 才是真正居中，也不会上下溢出。
    /// </summary>
    private static void DrawNum(Graphics g, Val v, float cx, float cy, float px, float maxW)
    {
        var f = FitFont(g, v.Text, px, maxW);
        float top = cy - f.Size * 0.73f;
        using var b = new SolidBrush(v.Color);
        using var sf = CenterSf();
        g.DrawString(v.Text, f, b, new RectangleF(cx - maxW / 2f, top, maxW, f.Size * 1.6f), sf);
    }

    /// <summary>字太宽（如 100、12M）就按比例缩到放得下。</summary>
    private static Font FitFont(Graphics g, string text, float px, float maxW)
    {
        float p = px;
        for (int i = 0; i < 8; i++)
        {
            var f = Gfx.F(Font, p, FontStyle.Bold);
            if (Gfx.Measure(g, text, f).Width <= maxW) return f;
            p *= 0.9f;
        }
        return Gfx.F(Font, Math.Max(4f, p), FontStyle.Bold);
    }

    private static StringFormat CenterSf()
    {
        var sf = (StringFormat)StringFormat.GenericTypographic.Clone();
        sf.Alignment = StringAlignment.Center;
        sf.LineAlignment = StringAlignment.Near;
        sf.FormatFlags |= StringFormatFlags.NoClip | StringFormatFlags.NoWrap;
        return sf;
    }

    /// <summary>把半透明的抗锯齿边缘往实色推，抵消缩放后的发灰。</summary>
    private static void BoostAlpha(Bitmap bmp, float factor)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            int len = data.Stride * data.Height;
            var buf = new byte[len];
            Marshal.Copy(data.Scan0, buf, 0, len);
            for (int i = 3; i < len; i += 4)
            {
                int a = buf[i];
                if (a == 0 || a == 255) continue;
                buf[i] = (byte)Math.Min(255, (int)(a * factor));
            }
            Marshal.Copy(buf, 0, data.Scan0, len);
        }
        finally { bmp.UnlockBits(data); }
    }
}
