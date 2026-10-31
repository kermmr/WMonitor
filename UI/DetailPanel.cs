using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using WMonitor.Metrics;

namespace WMonitor.UI;

/// <summary>
/// 点贴片 / 托盘图标弹出的详情面板：按「显示项」开关列数据行 + 曲线（可切时间窗）+ 温度功耗。
///
/// 行不是写死的四行：CPU / 内存 / GPU / 显存 / 网速 / 磁盘 各对应设置里的一个开关，
/// 关掉哪项就不画哪行，面板高度跟着变（每行 <see cref="RowH"/>）。
/// </summary>
public sealed class DetailPanel : LayeredWindow
{
    public const int PanelW = 344;

    /// <summary>一行的高度。</summary>
    public const int RowH = 52;

    /// <summary>标题行 + 底栏的固定高度（不含数据行）。</summary>
    public const int ChromeH = 106;

    private Bitmap? _bmp;

    /// <summary>历史关闭时的 60 点实时缓冲，下标 = <see cref="HistoryMetric"/>。</summary>
    private readonly List<double>[] _hist = new List<double>[7];

    /// <summary>历史数据源。为 null 或 <c>!Enabled</c> 时退回 60 点实时缓冲。</summary>
    public HistoryStore? History { get; set; }

    private static readonly (string Label, int Minutes)[] Windows =
    {
        ("1m", 1), ("5m", 5), ("1h", 60), ("24h", 1440)
    };
    private const float ChipW = 26f, ChipH = 16f, ChipGap = 4f;
    private readonly RectangleF[] _winRects = new RectangleF[Windows.Length];

    private readonly float[]?[] _cache = new float[]?[7];
    private long _cacheAt;
    private int _cacheWin = -1;
    private AppConfig? _cfgRef;

    /// <summary>一行画什么。</summary>
    private enum RowKind { Cpu, Ram, Gpu, Vram, Net, Disk }

    public DetailPanel()
    {
        Name = "WMonitorPanel";
        NoActivate = false;
        Visible = false;
        for (int i = 0; i < _hist.Length; i++) _hist[i] = new List<double>();

        Deactivate += (_, _) =>
        {
            // 刚弹出时的焦点抖动会立刻触发 Deactivate，宽限期内忽略
            if (DateTime.UtcNow < _deafUntil) return;
            Hide();
        };
    }

    private DateTime _deafUntil;

    /// <summary>按「显示项」开关决定面板要有哪几行（顺序固定）。全关时至少留 CPU，不留空面板。</summary>
    private static List<RowKind> RowsFor(AppConfig cfg)
    {
        var rows = new List<RowKind>(6);
        if (cfg.ShowCpu) rows.Add(RowKind.Cpu);
        if (cfg.ShowRam) rows.Add(RowKind.Ram);
        if (cfg.ShowGpu) rows.Add(RowKind.Gpu);
        if (cfg.ShowVram) rows.Add(RowKind.Vram);
        if (cfg.ShowNet) rows.Add(RowKind.Net);
        if (cfg.ShowDisk) rows.Add(RowKind.Disk);
        if (rows.Count == 0) rows.Add(RowKind.Cpu);
        return rows;
    }

    private static HistoryMetric Map(RowKind k) => k switch
    {
        RowKind.Ram => HistoryMetric.Ram,
        RowKind.Gpu => HistoryMetric.Gpu,
        RowKind.Vram => HistoryMetric.Vram,
        RowKind.Net => HistoryMetric.Net,
        RowKind.Disk => HistoryMetric.Disk,
        _ => HistoryMetric.Cpu,
    };

    /// <summary>面板应有高度（打开定位用）。</summary>
    public static int HeightFor(AppConfig cfg) => ChromeH + RowsFor(cfg).Count * RowH;

    public void PushHistory(MetricsSnapshot s)
    {
        Add(HistoryMetric.Cpu, s.CpuPercent);
        Add(HistoryMetric.Ram, s.RamPercent);
        Add(HistoryMetric.Gpu, s.GpuPercent);
        Add(HistoryMetric.Vram, s.VramPercent);
        Add(HistoryMetric.Net, s.NetDownBps / 1024.0);      // 存原始 KB/s，画的时候再归一化
        Add(HistoryMetric.Temp, s.GpuTempC);
        Add(HistoryMetric.Disk, (s.DiskReadBps + s.DiskWriteBps) / 1024.0);
    }

    private void Add(HistoryMetric m, double v)
    {
        var q = _hist[(int)m];
        q.Add(v);
        while (q.Count > 60) q.RemoveAt(0);
    }

    /// <summary>
    /// 弹出面板。<paramref name="deafMs"/> 是「失焦保护期」：从托盘图标打开时要长一点 ——
    /// 点托盘会让任务栏/溢出面板重新拿到焦点，紧接着的 Deactivate 会把刚弹出的面板关掉。
    /// </summary>
    public void OpenNear(Rectangle stripBounds, MetricsSnapshot s, AppConfig cfg, Theme th, int deafMs = 350)
    {
        var screen = Screen.FromRectangle(stripBounds).WorkingArea;
        int ph = HeightFor(cfg);
        int x = stripBounds.Right - PanelW;
        int y = stripBounds.Top - ph - 8;
        if (y < screen.Top + 8) y = stripBounds.Bottom + 8;   // 任务栏在顶部时翻到下面
        x = Math.Clamp(x, screen.Left + 8, screen.Right - PanelW - 8);
        y = Math.Clamp(y, screen.Top + 8, screen.Bottom - ph - 8);

        Update(s, cfg, th);
        _deafUntil = DateTime.UtcNow.AddMilliseconds(deafMs);
        Show();
        Activate();
        _deafUntil = DateTime.UtcNow.AddMilliseconds(deafMs);
        LastPos = new Point(x, y);
        if (_bmp != null) Blit(_bmp, x, y);
    }

    public Point LastPos { get; private set; }

    public event Action? SettingsClicked;

    public void Update(MetricsSnapshot s, AppConfig cfg, Theme th)
    {
        _cfgRef = cfg;
        var rows = RowsFor(cfg);
        int w = PanelW;
        int h = ChromeH + rows.Count * RowH;
        if (_bmp == null || _bmp.Width != w || _bmp.Height != h)
        {
            _bmp?.Dispose();
            _bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        }

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

        // ---- 标题 + 时间窗 ----
        Gfx.Text(g, "系统监视", Gfx.F("Segoe UI", 12.5f, FontStyle.Bold), th.Text, pad, 13f);
        DrawWindowChips(g, cfg, th, right);

        // ---- 数据行（按显示项）----
        const float rowY = 38f;
        const float rowH = RowH;
        for (int i = 0; i < rows.Count; i++)
            DrawRow(g, rows[i], s, th, pad, right, rowY + rowH * i, rowH, cfg);

        // ---- 底部 ----
        float footY = rowY + rowH * rows.Count + 4;
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

    private void DrawRow(Graphics g, RowKind kind, MetricsSnapshot s, Theme th,
        float pad, float right, float y, float rowH, AppConfig cfg)
    {
        string name, sub, value, unit, icon;
        double pct;
        Color color;

        switch (kind)
        {
            case RowKind.Ram:
                name = "内存";
                sub = $"{s.RamUsedGb:0.0} / {s.RamTotalGb:0} GB";
                pct = s.RamPercent;
                value = s.RamPercent.ToString("0");
                unit = "%"; icon = "ram"; color = th.Ram;
                break;

            case RowKind.Gpu:
                name = "GPU";
                sub = s.HasGpu ? s.GpuName : "未检测到";
                pct = s.GpuPercent;
                value = s.GpuPercent.ToString("0");
                unit = "%"; icon = "gpu"; color = th.Gpu;
                break;

            case RowKind.Vram:
                name = "显存";
                sub = s.HasVramTotal ? $"{s.VramGb:0.0} / {s.VramTotalGb:0.0} GB" : "—";
                pct = s.VramPercent;
                value = s.VramGb.ToString("0.0");
                unit = "GB"; icon = "vram"; color = th.Vram;
                break;

            case RowKind.Net:
                name = "网速";
                sub = $"↓ {TrayIconRenderer.RateText(s.NetDownBps)}/s · ↑ {TrayIconRenderer.RateText(s.NetUpBps)}/s";
                pct = RateToPct(s.NetDownBps / 1024.0);
                value = TrayIconRenderer.RateText(s.NetDownBps);
                unit = "/s"; icon = "net"; color = th.Net;
                break;

            case RowKind.Disk:
            {
                double kb = (s.DiskReadBps + s.DiskWriteBps) / 1024.0;
                name = "磁盘";
                sub = $"读 {TrayIconRenderer.RateText(s.DiskReadBps)}/s · 写 {TrayIconRenderer.RateText(s.DiskWriteBps)}/s";
                pct = RateToPct(kb);
                value = TrayIconRenderer.RateText(s.DiskReadBps + s.DiskWriteBps);
                unit = "/s"; icon = "disk"; color = th.Disk;
                break;
            }

            default:
                name = "CPU";
                sub = $"{s.CpuGhz:0.0} GHz · {Environment.ProcessorCount} 线程";
                pct = s.CpuPercent;
                value = s.CpuPercent.ToString("0");
                unit = "%"; icon = "cpu"; color = th.Cpu;
                break;
        }

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

        DrawSpark(g, spark, Series(kind, cfg), color);

        // 数值 + 单位（整体右对齐）
        var fNum = Gfx.F("Segoe UI", 15f, FontStyle.Bold);
        var fUnit = Gfx.F("Segoe UI", 9f, FontStyle.Regular);
        float vw = Gfx.Measure(g, value, fNum).Width;
        float uw = Gfx.Measure(g, unit, fUnit).Width;
        float sx = right - (vw + 3 + uw);
        Gfx.Text(g, value, fNum, th.Text, sx, cy - 10);
        Gfx.Text(g, unit, fUnit, th.Label, sx + vw + 3, cy + 1);
    }

    // ---------------- 时间窗 ----------------

    /// <summary>标题行右侧的 1m/5m/1h/24h 段。未开「跨重启保留」时不画 24h（没有数据源，不给死链）。</summary>
    private void DrawWindowChips(Graphics g, AppConfig cfg, Theme th, float right)
    {
        // 历史关闭：退回「1s · 60 点」说明 —— 几个时间窗都只有同一份 60 点缓冲，给出来是骗人
        if (History is not { Enabled: true })
        {
            string cap = $"{Math.Max(1, cfg.RefreshMs / 1000.0):0.#}s · 60 点";
            var cf = Gfx.F("Segoe UI", 9.5f, FontStyle.Regular);
            Gfx.Text(g, cap, cf, th.Label, right - Gfx.Measure(g, cap, cf).Width, 16f);
            for (int i = 0; i < _winRects.Length; i++) _winRects[i] = RectangleF.Empty;
            return;
        }

        int count = LongWindowAvailable() ? Windows.Length : Windows.Length - 1;
        float x = right - count * ChipW - (count - 1) * ChipGap;
        var f = Gfx.F("Segoe UI", 9f, FontStyle.Regular);
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        for (int i = 0; i < count; i++)
        {
            var r = new RectangleF(x + i * (ChipW + ChipGap), 11f, ChipW, ChipH);
            bool sel = cfg.ChartWindowMinutes == Windows[i].Minutes;
            if (sel)
            {
                using var p = Gfx.RoundRect(r, 5f);
                using var b = new SolidBrush(th.Hover);
                g.FillPath(b, p);
            }
            using var tb = new SolidBrush(sel ? th.Text : th.Label);
            g.DrawString(Windows[i].Label, f, tb, r, sf);
            _winRects[i] = r;
        }
        for (int i = count; i < Windows.Length; i++) _winRects[i] = RectangleF.Empty;
    }

    private bool LongWindowAvailable() => History is { Enabled: true, CanPersist: true };

    /// <summary>某一行的曲线数据。带缓存：24h 窗口每次要读 3.4 MB 文件，不能每秒读。</summary>
    private float[] Series(RowKind kind, AppConfig cfg)
    {
        var metric = Map(kind);
        int idx = (int)metric;
        float[] arr;

        if (History is not { Enabled: true } h)
        {
            arr = _hist[idx].Select(v => (float)v).ToArray();
        }
        else
        {
            long now = Stopwatch.GetTimestamp();
            long ttl = cfg.ChartWindowMinutes > 60 ? Stopwatch.Frequency * 10 : Stopwatch.Frequency;
            if (_cacheWin != cfg.ChartWindowMinutes || now - _cacheAt > ttl)
            {
                _cacheWin = cfg.ChartWindowMinutes;
                _cacheAt = now;
                for (int i = 0; i < _cache.Length; i++) _cache[i] = null;
            }
            arr = _cache[idx] ??= h.Query(metric, TimeSpan.FromMinutes(cfg.ChartWindowMinutes), 60).Values;
        }

        // 网速 / 磁盘是速率（KB/s），曲线要归一化到 0–100，跟托盘图标同一套对数刻度
        if (kind != RowKind.Net && kind != RowKind.Disk) return arr;

        var norm = new float[arr.Length];
        for (int i = 0; i < arr.Length; i++) norm[i] = RateToPct(arr[i]);
        return norm;
    }

    /// <summary>KB/s → 0–100（对数）：20 KB/s ≈ 26%，1 MB/s ≈ 60%，100 MB/s 满。与托盘图标同刻度。</summary>
    private static float RateToPct(double kb) =>
        (float)Math.Clamp(Math.Log10(Math.Max(0, kb) + 1) / Math.Log10(100 * 1024 + 1) * 100, 0, 100);

    private static void DrawSpark(Graphics g, RectangleF r, float[] vals, Color c)
    {
        if (vals.Length < 3) return;
        var pts = new PointF[vals.Length];
        for (int i = 0; i < vals.Length; i++)
        {
            float x = r.Left + r.Width * i / (vals.Length - 1f);
            double v = double.IsNaN(vals[i]) ? 0 : vals[i];
            float y = r.Bottom - (float)(Math.Clamp(v, 0, 100) / 100.0) * (r.Height - 2) - 1;
            // NaN/Inf 坐标会让 GDI+ 的 DrawLines 抛 OverflowException（v0.04 踩过），先兜住
            if (!float.IsFinite(y)) y = r.Bottom;
            if (!float.IsFinite(x)) x = r.Left;
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
            case "vram":
                g.DrawRectangle(pen, r.X + 1, r.Y + 3, r.Width - 2, r.Height - 8);
                g.DrawLine(pen, r.X + 4, r.Bottom - 5, r.X + 4, r.Bottom - 2);
                g.DrawLine(pen, r.Right - 4, r.Bottom - 5, r.Right - 4, r.Bottom - 2);
                g.DrawLine(pen, r.X + 4, r.Y + 6, r.Right - 4, r.Y + 6);
                break;
            case "net":
                // 上行 / 下行箭头
                g.DrawLine(pen, r.X + 4, r.Y + 1, r.X + 4, r.Bottom - 1);
                g.DrawLine(pen, r.X + 1.5f, r.Y + 3.5f, r.X + 4, r.Y + 1);
                g.DrawLine(pen, r.X + 6.5f, r.Y + 3.5f, r.X + 4, r.Y + 1);
                g.DrawLine(pen, r.Right - 3, r.Bottom - 1, r.Right - 3, r.Y + 1);
                g.DrawLine(pen, r.Right - 5.5f, r.Bottom - 3.5f, r.Right - 3, r.Bottom - 1);
                g.DrawLine(pen, r.Right - 0.5f, r.Bottom - 3.5f, r.Right - 3, r.Bottom - 1);
                break;
            case "disk":
                g.DrawEllipse(pen, r.X + 1, r.Y + 1, r.Width - 2, 4.5f);
                g.DrawLine(pen, r.X + 1, r.Y + 3.2f, r.X + 1, r.Bottom - 3);
                g.DrawLine(pen, r.Right - 1, r.Y + 3.2f, r.Right - 1, r.Bottom - 3);
                g.DrawArc(pen, r.X + 1, r.Bottom - 6.5f, r.Width - 2, 4.5f, 0, 180);
                g.DrawEllipse(pen, r.X + 1, r.Bottom - 6.5f, r.Width - 2, 4.5f);
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

            // 时间窗段
            for (int i = 0; i < _winRects.Length; i++)
            {
                if (_winRects[i].IsEmpty || !_winRects[i].Contains(pt)) continue;
                if (_cfgRef != null)
                {
                    _cfgRef.ChartWindowMinutes = Windows[i].Minutes;
                    _cfgRef.Save();
                    _cacheWin = -1;               // 强制重算
                }
                return;
            }

            if (_closeRect.Contains(pt)) { Hide(); return; }
            if (_gearRect.Contains(pt)) { Hide(); SettingsClicked?.Invoke(); return; }
        }
        base.WndProc(ref m);
    }
}
