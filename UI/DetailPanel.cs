using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using WMonitor.Metrics;

namespace WMonitor.UI;

/// <summary>点贴片弹出的详情面板：四行指标 + 60 点曲线 + 温度功耗。</summary>
public sealed class DetailPanel : LayeredWindow
{
    public const int PanelW = 344;
    public const int PanelH = 314;

    private Bitmap? _bmp;
    private readonly List<double>[] _hist = { new(), new(), new(), new() };

    public DetailPanel()
    {
        Name = "WMonitorPanel";
        NoActivate = false;
        Visible = false;
        Deactivate += (_, _) =>
        {
            // 刚弹出时的焦点抖动会立刻触发 Deactivate，300ms 内忽略
            if (DateTime.UtcNow < _deafUntil) return;
            Hide();
        };
    }

    private DateTime _deafUntil;

    public void PushHistory(MetricsSnapshot s)
    {
        Add(0, s.CpuPercent); Add(1, s.RamPercent); Add(2, s.GpuPercent); Add(3, s.VramPercent);
    }

    private void Add(int i, double v)
    {
        var q = _hist[i];
        q.Add(v);
        while (q.Count > 60) q.RemoveAt(0);
    }

    public void OpenNear(Rectangle stripBounds, MetricsSnapshot s, AppConfig cfg, Theme th)
    {
        var screen = Screen.FromRectangle(stripBounds).WorkingArea;
        int x = stripBounds.Right - PanelW;
        int y = stripBounds.Top - PanelH - 8;
        if (y < screen.Top + 8) y = stripBounds.Bottom + 8;   // 任务栏在顶部时翻到下面
        x = Math.Clamp(x, screen.Left + 8, screen.Right - PanelW - 8);

        Update(s, cfg, th);
        _deafUntil = DateTime.UtcNow.AddMilliseconds(350);
        Show();
        Activate();
        _deafUntil = DateTime.UtcNow.AddMilliseconds(350);
        LastPos = new Point(x, y);
        if (_bmp != null) Blit(_bmp, x, y);
    }

    public Point LastPos { get; private set; }

    public event Action? SettingsClicked;

    public void Update(MetricsSnapshot s, AppConfig cfg, Theme th)
    {
        int w = PanelW, h = PanelH;
        if (_bmp == null) _bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);

        using var g = Graphics.FromImage(_bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        g.Clear(Color.Transparent);

        var full = new RectangleF(0.5f, 0.5f, w - 1, h - 1);
        using (var path = Gfx.RoundRect(full, 12f))
        {
            using (var bg = new SolidBrush(th.PanelBg)) g.FillPath(bg, path);
            using (var bd = new Pen(th.PillBorder, 1f)) g.DrawPath(bd, path);
        }

        const float pad = 16f;
        float right = w - pad;

        // ---- 标题 ----
        Gfx.Text(g, "系统监视", Gfx.F("Segoe UI", 12.5f, FontStyle.Bold), th.Text, pad, 13f);
        string cap = $"{Math.Max(1, cfg.RefreshMs / 1000.0):0.#}s · 60 点";
        var capFont = Gfx.F("Segoe UI", 9.5f, FontStyle.Regular);
        Gfx.Text(g, cap, capFont, th.Label, right - Gfx.Measure(g, cap, capFont).Width, 16f);

        // ---- 四行 ----
        float rowY = 38f;
        const float rowH = 52f;
        DrawRow(g, 0, "CPU", $"{s.CpuGhz:0.0} GHz · {Environment.ProcessorCount} 线程", s.CpuPercent,
            s.CpuPercent.ToString("0"), "%", "cpu", th.Cpu, th, pad, right, rowY, rowH);
        DrawRow(g, 1, "内存", $"{s.RamUsedGb:0.0} / {s.RamTotalGb:0} GB", s.RamPercent,
            s.RamPercent.ToString("0"), "%", "ram", th.Ram, th, pad, right, rowY + rowH, rowH);
        DrawRow(g, 2, "GPU", s.HasGpu ? s.GpuName : "未检测到", s.GpuPercent,
            s.GpuPercent.ToString("0"), "%", "gpu", th.Gpu, th, pad, right, rowY + rowH * 2, rowH);
        DrawRow(g, 3, "显存", s.HasVramTotal ? $"{s.VramGb:0.0} / {s.VramTotalGb:0.0} GB" : "—", s.VramPercent,
            s.VramGb.ToString("0.0"), "GB", "vram", th.Vram, th, pad, right, rowY + rowH * 3, rowH);

        // ---- 底部 ----
        float footY = rowY + rowH * 4 + 4;
        using (var sep = new Pen(th.Sep, 1f)) g.DrawLine(sep, pad, footY, right, footY);

        var f = Gfx.F("Segoe UI", 9.5f, FontStyle.Regular);
        float fx = pad;
        fx += DrawMeta(g, fx, footY + 12, "CPU", $"{s.CpuGhz:0.0} GHz", th.Label, th.Text, f);
        fx += DrawMeta(g, fx, footY + 12, "GPU", double.IsNaN(s.GpuTempC) ? "—" : $"{s.GpuTempC:0}°C", th.Label, th.Text, f);
        if (!double.IsNaN(s.GpuPowerW))
            DrawMeta(g, fx, footY + 12, "功耗", $"{s.GpuPowerW:0}W", th.Label, th.Text, f);

        // 齿轮（打开设置）
        var gear = new RectangleF(right - 48, footY + 8, 18, 18);
        using (var gb = new SolidBrush(th.Hover)) g.FillEllipse(gb, gear);
        DrawGear(g, gear, th.Label);
        _gearRect = gear;

        // 关闭按钮
        var close = new RectangleF(right - 18, footY + 8, 18, 18);
        using (var cb = new SolidBrush(th.Hover)) g.FillEllipse(cb, close);
        using (var cp = new Pen(th.Label, 1.2f))
        {
            g.DrawLine(cp, close.Left + 6, close.Top + 6, close.Right - 6, close.Bottom - 6);
            g.DrawLine(cp, close.Right - 6, close.Top + 6, close.Left + 6, close.Bottom - 6);
        }
        _closeRect = close;

        if (Visible) Blit(_bmp, LastPos.X, LastPos.Y);
    }

    private RectangleF _closeRect;
    private RectangleF _gearRect;

    private static void DrawGear(Graphics g, RectangleF r, Color c)
    {
        float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        using var pen = new Pen(c, 1.2f);
        g.DrawEllipse(pen, cx - 3.2f, cy - 3.2f, 6.4f, 6.4f);
        for (int i = 0; i < 6; i++)
        {
            double a = Math.PI * 2 * i / 6;
            float x1 = cx + (float)Math.Cos(a) * 5.2f, y1 = cy + (float)Math.Sin(a) * 5.2f;
            float x2 = cx + (float)Math.Cos(a) * 7.2f, y2 = cy + (float)Math.Sin(a) * 7.2f;
            g.DrawLine(pen, x1, y1, x2, y2);
        }
    }

    private static float DrawMeta(Graphics g, float x, float y, string label, string value, Color lc, Color vc, Font f)
    {
        Gfx.Text(g, label, f, lc, x, y);
        float lw = Gfx.Measure(g, label, f).Width;
        Gfx.Text(g, value, f, vc, x + lw + 4, y);
        float vw = Gfx.Measure(g, value, f).Width;
        return lw + vw + 18;
    }

    private void DrawRow(Graphics g, int idx, string name, string sub, double pct, string value, string unit,
        string icon, Color color, Theme th, float pad, float right, float y, float rowH)
    {
        float cy = y + rowH / 2;

        // 图标底
        var ib = new RectangleF(pad, cy - 13, 26, 26);
        using (var p = Gfx.RoundRect(ib, 7f))
        using (var b = new SolidBrush(Color.FromArgb(26, color))) g.FillPath(b, p);
        DrawIcon(g, icon, new RectangleF(ib.X + 6, ib.Y + 6, 14, 14), color);

        float x = pad + 26 + 11;
        const float numW = 66;

        // 名称 + 副标题
        var fName = Gfx.F("Segoe UI", 11.5f, FontStyle.Bold);
        Gfx.Text(g, name, fName, th.Text, x, cy - 15);
        var subFont = Gfx.F("Segoe UI", 9.5f, FontStyle.Regular);
        Gfx.Text(g, sub, subFont, th.Label, x + Gfx.Measure(g, name, fName).Width + 8, cy - 13.5f);

        // 曲线区域（bar 让位给它）
        var spark = new RectangleF(right - numW - 100, cy - 12, 92, 24);

        // 进度条
        float barW = spark.Left - x - 10;
        var bar = new RectangleF(x, cy + 5, Math.Max(20, barW), 3f);
        using (var bp = Gfx.RoundRect(bar, 1.5f))
        using (var b = new SolidBrush(th.Track)) g.FillPath(b, bp);
        float fillW = (float)(Math.Clamp(pct, 0, 100) / 100.0 * bar.Width);
        if (fillW > 1)
        {
            using var fp = Gfx.RoundRect(new RectangleF(bar.X, bar.Y, fillW, bar.Height), 1.5f);
            using var fb = new SolidBrush(color);
            g.FillPath(fb, fp);
        }

        DrawSpark(g, spark, _hist[idx], color);

        // 数值 + 单位（整体右对齐）
        var fNum = Gfx.F("Segoe UI", 15f, FontStyle.Bold);
        var fUnit = Gfx.F("Segoe UI", 9f, FontStyle.Regular);
        float vw = Gfx.Measure(g, value, fNum).Width;
        float uw = Gfx.Measure(g, unit, fUnit).Width;
        float sx = right - (vw + 3 + uw);
        Gfx.Text(g, value, fNum, th.Text, sx, cy - 10);
        Gfx.Text(g, unit, fUnit, th.Label, sx + vw + 3, cy + 1);
    }

    private static void DrawSpark(Graphics g, RectangleF r, List<double> vals, Color c)
    {
        if (vals.Count < 3) return;
        var pts = new PointF[vals.Count];
        for (int i = 0; i < vals.Count; i++)
        {
            float x = r.Left + r.Width * i / (vals.Count - 1f);
            double v = double.IsNaN(vals[i]) ? 0 : vals[i];
            float y = r.Bottom - (float)(Math.Clamp(v, 0, 100) / 100.0) * (r.Height - 2) - 1;
            pts[i] = new PointF(x, y);
        }

        using var path = new GraphicsPath();
        path.AddLines(pts);
        path.AddLine(pts[^1].X, r.Bottom, pts[0].X, r.Bottom);
        path.CloseFigure();
        using (var fill = new SolidBrush(Color.FromArgb(34, c))) g.FillPath(fill, path);

        using var pen = new Pen(Color.FromArgb(165, c), 1.5f) { LineJoin = LineJoin.Round };
        g.DrawLines(pen, pts);
    }

    private static void DrawIcon(Graphics g, string kind, RectangleF r, Color c)
    {
        using var pen = new Pen(c, 1.3f) { LineJoin = LineJoin.Round };
        switch (kind)
        {
            case "cpu":
                g.DrawRectangle(pen, r.X + 3, r.Y + 3, r.Width - 6, r.Height - 6);
                g.DrawRectangle(pen, r.X + 6, r.Y + 6, r.Width - 12, r.Height - 12);
                for (int i = 0; i < 2; i++)
                {
                    float px = r.X + 5 + i * 3;
                    g.DrawLine(pen, px, r.Y + 1, px, r.Y + 3);
                    g.DrawLine(pen, px, r.Bottom - 3, px, r.Bottom - 1);
                    g.DrawLine(pen, r.X + 1, px, r.X + 3, px);
                    g.DrawLine(pen, r.Right - 3, px, r.Right - 1, px);
                }
                break;
            case "ram":
                g.DrawRectangle(pen, r.X + 1, r.Y + 4, r.Width - 2, r.Height - 8);
                g.DrawLine(pen, r.X + 5, r.Y + 7, r.X + 5, r.Bottom - 7);
                g.DrawLine(pen, r.X + 8, r.Y + 7, r.X + 8, r.Bottom - 7);
                g.DrawLine(pen, r.X + 4, r.Bottom - 4, r.X + 4, r.Bottom - 1);
                g.DrawLine(pen, r.Right - 4, r.Bottom - 4, r.Right - 4, r.Bottom - 1);
                break;
            case "gpu":
                g.DrawRectangle(pen, r.X + 1, r.Y + 4, r.Width - 2, r.Height - 8);
                g.DrawEllipse(pen, r.X + 4, r.Y + 6, 5, 5);
                g.DrawLine(pen, r.X + 11, r.Y + 7, r.Right - 3, r.Y + 7);
                g.DrawLine(pen, r.X + 11, r.Y + 10, r.Right - 3, r.Y + 10);
                break;
            default:
                g.DrawRectangle(pen, r.X + 2, r.Y + 2, r.Width - 4, r.Height - 4);
                g.DrawLine(pen, r.X + 2, r.Y + r.Height / 2, r.Right - 2, r.Y + r.Height / 2);
                g.DrawLine(pen, r.X + r.Width / 2, r.Y + 2, r.X + r.Width / 2, r.Bottom - 2);
                break;
        }
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_LBUTTONUP = 0x0202;
        if (m.Msg == WM_LBUTTONUP && Visible)
        {
            int lp = m.LParam.ToInt32();
            var pt = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
            if (_closeRect.Contains(pt)) { Hide(); return; }
            if (_gearRect.Contains(pt)) { Hide(); SettingsClicked?.Invoke(); return; }
        }
        base.WndProc(ref m);
    }
}
