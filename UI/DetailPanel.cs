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
/// v0.35 起数据行下面可带「TOP 进程」区块（cfg.PanelTopProcesses）：按 CPU/GPU/显存排序的 3 行，
/// 数据来自 MonitorAppContext 惰性建的 AppMetricsHub —— 仅面板可见时采样（roadmap P1，
/// 不把进程采样开销带回常驻链路）；点行经 <see cref="TopClicked"/> 跳主面板选中该应用。
/// </summary>
public sealed class DetailPanel : LayeredWindow
{
    public const int PanelW = 344;

    /// <summary>一行的高度。</summary>
    public const int RowH = 52;

    /// <summary>标题行 + 底栏的固定高度（不含数据行）。</summary>
    public const int ChromeH = 106;

    // ---- TOP 进程区块（v0.35） ----

    /// <summary>区块标题行（分隔线 + 标题/段控）高度。</summary>
    public const int TopHeadH = 26;

    /// <summary>TOP 行高（名称 + 数值 + 2px 迷你条）。</summary>
    public const int TopRowH = 26;

    /// <summary>TOP 行数。</summary>
    public const int TopCount = 3;

    /// <summary>区块总高（固定，数据不足 3 行时留白，面板高度不跳变）。</summary>
    public const int TopH = TopHeadH + TopCount * TopRowH;

    private Bitmap? _bmp;

    /// <summary>历史关闭时的 60 点实时缓冲，下标 = <see cref="HistoryMetric"/>。</summary>
    private readonly List<double>[] _hist = new List<double>[7];

    /// <summary>历史数据源。为 null 或 <c>!Enabled</c> 时退回 60 点实时缓冲。</summary>
    public HistoryStore? History { get; set; }

    /// <summary>点击 TOP 行 → 跳主面板选中该应用（key 与主面板表格行 Key 同构，"a:path" 形式）。</summary>
    public event Action<string>? TopClicked;

    private static readonly (string Label, int Minutes)[] Windows =
    {
        ("1m", 1), ("5m", 5), ("1h", 60), ("24h", 1440)
    };
    private const float ChipW = 30f, ChipH = 18f, ChipGap = 4f;   // C9：文字 12px 后 26×16 装不下「24h」
    private const float TopChipW = 40f;                           // 「CPU」「显存」比时间窗宽，40 扣得下
    private readonly RectangleF[] _winRects = new RectangleF[Windows.Length];
    private readonly RectangleF[] _topSortRects = new RectangleF[3];
    private readonly List<PanelTopSort> _topSortItems = new(3);
    private readonly List<(RectangleF Rect, string Key)> _topRows = new();

    private readonly float[]?[] _cache = new float[]?[7];
    private long _cacheAt;
    private int _cacheWin = -1;
    private AppConfig? _cfgRef;

    /// <summary>Update 最近一次的实参（排序段控点击后原地重画用，不等下个采样 tick）。</summary>
    private MetricsSnapshot _lastS = new();
    private Theme? _lastTh;
    private AppMetricsSnapshot? _lastApps;

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

    /// <summary>面板应有高度（打开定位用）。TOP 区块只由 cfg 决定，GPU 可用与否不改高度。</summary>
    public static int HeightFor(AppConfig cfg) =>
        ChromeH + RowsFor(cfg).Count * RowH + (cfg.PanelTopProcesses ? TopH : 0);

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
    public void OpenNear(Rectangle stripBounds, MetricsSnapshot s, AppConfig cfg, Theme th, int deafMs = 350,
        AppMetricsSnapshot? apps = null)
    {
        var screen = Screen.FromRectangle(stripBounds).WorkingArea;
        int ph = HeightFor(cfg);
        int x = stripBounds.Right - PanelW;
        int y = stripBounds.Top - ph - 8;
        if (y < screen.Top + 8) y = stripBounds.Bottom + 8;   // 任务栏在顶部时翻到下面
        x = Math.Clamp(x, screen.Left + 8, screen.Right - PanelW - 8);
        y = Math.Clamp(y, screen.Top + 8, screen.Bottom - ph - 8);

        Update(s, cfg, th, apps);
        _deafUntil = DateTime.UtcNow.AddMilliseconds(deafMs);
        Show();
        Activate();
        _deafUntil = DateTime.UtcNow.AddMilliseconds(deafMs);
        LastPos = new Point(x, y);
        if (_bmp != null) Blit(_bmp, x, y);
    }

    public Point LastPos { get; private set; }

    public event Action? SettingsClicked;

    /// <summary>点击标题旁的四格图标：打开资源管控主面板。</summary>
    public event Action? DashboardClicked;

    public void Update(MetricsSnapshot s, AppConfig cfg, Theme th, AppMetricsSnapshot? apps = null)
    {
        _cfgRef = cfg;
        _lastS = s;
        _lastTh = th;
        _lastApps = apps;
        var rows = RowsFor(cfg);
        int w = PanelW;
        int h = ChromeH + rows.Count * RowH + (cfg.PanelTopProcesses ? TopH : 0);
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
        Gfx.Text(g, "系统监视", Gfx.F("Segoe UI", 14f, FontStyle.Bold), th.Text, pad, 13f);   // A3：Title 14（原 12.5 废弃）
        float titleW = Gfx.Measure(g, "系统监视", Gfx.F("Segoe UI", 14f, FontStyle.Bold)).Width;
        DrawWindowChips(g, cfg, th, right);

        // 资源面板入口（主面板 v0.18）：标题右侧的四格图标；总开关关闭时不画。
        _dashRect = RectangleF.Empty;
        if (cfg.DashboardEnabled)
        {
            var dashBtn = new RectangleF(pad + titleW + 10, 11f, 18, 18);
            using (var db = new SolidBrush(th.Hover)) g.FillEllipse(db, dashBtn);
            DrawGrid(g, dashBtn, th.Label);
            _dashRect = dashBtn;
        }

        // ---- 数据行（按显示项）----
        const float rowY = 38f;
        const float rowH = RowH;
        for (int i = 0; i < rows.Count; i++)
            DrawRow(g, rows[i], s, th, pad, right, rowY + rowH * i, rowH, cfg);

        // ---- TOP 进程（v0.35）+ 底部 ----
        float footY;
        if (cfg.PanelTopProcesses)
        {
            float topY = rowY + rowH * rows.Count;
            DrawTopSection(g, cfg, th, apps, pad, right, topY);
            footY = topY + TopH + 4;
        }
        else
        {
            footY = rowY + rowH * rows.Count + 4;
        }

        using (var sep = new Pen(th.Sep, 1f)) g.DrawLine(sep, pad, footY, right, footY);

        var f = Gfx.F("Segoe UI", 12f, FontStyle.Regular);   // A3 最小 12（原 9.5 废弃）
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
    private RectangleF _dashRect;

    /// <summary>四格图标：打开资源管控主面板。</summary>
    private static void DrawGrid(Graphics g, RectangleF r, Color c)
    {
        using var pen = new Pen(c, 1.2f);
        float cell = r.Width * 0.36f;
        foreach (var (dx, dy) in new[] { (0f, 0f), (1f, 0f), (0f, 1f), (1f, 1f) })
        {
            var cellRect = new RectangleF(
                r.X + dx * (r.Width - cell), r.Y + dy * (r.Height - cell), cell, cell);
            g.DrawRectangle(pen, cellRect.X, cellRect.Y, cellRect.Width, cellRect.Height);
        }
    }

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
                // v0.35 网速分接口:锁定单块网卡时把接口名亮出来,让过滤生效这件事看得见
                sub = (s.NetSource.Length > 0 ? s.NetSource + " · " : "") +
                      $"↓ {TrayIconRenderer.RateText(s.NetDownBps)}/s · ↑ {TrayIconRenderer.RateText(s.NetUpBps)}/s";
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

        // 名称 + 副标题（A3：行名 Title 14 Bold（原 11.5）、副题 Body 12（原 9.5））
        var fName = Gfx.F("Segoe UI", 14f, FontStyle.Bold);
        Gfx.Text(g, name, fName, th.Text, x, cy - 17);
        var subFont = Gfx.F("Segoe UI", 12f, FontStyle.Regular);
        Gfx.Text(g, sub, subFont, th.Label, x + Gfx.Measure(g, name, fName).Width + 8, cy - 15);

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

        // 数值 + 单位（整体右对齐；A3：数值 Value 16 Bold（原 15）、单位 Body 12（原 9））
        var fNum = Gfx.F("Segoe UI", 16f, FontStyle.Bold);
        var fUnit = Gfx.F("Segoe UI", 12f, FontStyle.Regular);
        float vw = Gfx.Measure(g, value, fNum).Width;
        float uw = Gfx.Measure(g, unit, fUnit).Width;
        float sx = right - (vw + 3 + uw);
        Gfx.Text(g, value, fNum, th.Text, sx, cy - 11);
        Gfx.Text(g, unit, fUnit, th.Label, sx + vw + 3, cy - 2);
    }

    // ---------------- TOP 进程（v0.35） ----------------

    /// <summary>
    /// TOP 进程区块：分隔线 + 标题 + 排序段控（右）+ 最多 3 行（名称、数值、2px 迷你条）。
    /// 数据为 null（面板打开瞬间 Hub 尚未出数）时只画骨架，下一个采样 tick 填上。
    /// 段控没有 GPU 进程数据时只留 CPU；配置里残留的 GPU/显存键此时按 CPU 显示，不回写配置。
    /// </summary>
    private void DrawTopSection(Graphics g, AppConfig cfg, Theme th,
        AppMetricsSnapshot? apps, float pad, float right, float topY)
    {
        _topRows.Clear();
        _topSortItems.Clear();

        using (var sep = new Pen(th.Sep, 1f)) g.DrawLine(sep, pad, topY, right, topY);

        Gfx.Text(g, "TOP 进程", Gfx.F("Segoe UI", 12f, FontStyle.Bold), th.Text, pad, topY + 6);

        // 排序段控：与时间窗 chips 同款画法（内缩圆角块 + 12px 文字）
        bool gpu = apps?.Gpu.Available == true;
        var items = gpu
            ? new[] { (PanelTopSort.Cpu, "CPU"), (PanelTopSort.Gpu, "GPU"), (PanelTopSort.Vram, "显存") }
            : new[] { (PanelTopSort.Cpu, "CPU") };
        var sort = gpu ? cfg.PanelTopSort : PanelTopSort.Cpu;
        float cx = right - items.Length * TopChipW - (items.Length - 1) * ChipGap;
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        var fChip = Gfx.F("Segoe UI", 12f, FontStyle.Regular);
        for (int i = 0; i < items.Length; i++)
        {
            var r = new RectangleF(cx + i * (TopChipW + ChipGap), topY + 5, TopChipW, ChipH);
            bool sel = items[i].Item1 == sort;
            if (sel)
            {
                using var p = Gfx.RoundRect(r, 5f);
                using var b = new SolidBrush(th.Hover);
                g.FillPath(b, p);
            }
            using var tb = new SolidBrush(sel ? th.Text : th.Label);
            g.DrawString(items[i].Item2, fChip, tb, r, sf);
            _topSortRects[i] = r;
            _topSortItems.Add(items[i].Item1);
        }
        for (int i = items.Length; i < _topSortRects.Length; i++) _topSortRects[i] = RectangleF.Empty;

        var top = apps == null ? new List<AppMetric>() : PanelTopList.Pick(apps.Apps, sort, TopCount);
        var fName = Gfx.F("Segoe UI", 12f, FontStyle.Regular);
        var fVal = Gfx.F("Segoe UI", 12f, FontStyle.Bold);
        Color accent = sort switch { PanelTopSort.Gpu => th.Gpu, PanelTopSort.Vram => th.Vram, _ => th.Cpu };

        // 迷你条归一：CPU/GPU 按百分比；显存按总量，总量未知（注册表兜底失败）按榜内最大
        double full;
        if (sort == PanelTopSort.Vram)
        {
            long total = (long)(apps?.Global.VramTotalBytes ?? 0);
            long max = top.Count > 0 ? (long)PanelTopList.Value(top[0], sort) : 0;
            full = total > 0 ? total : Math.Max(1, max);
        }
        else
        {
            full = 100.0;
        }

        float y = topY + TopHeadH;
        for (int i = 0; i < TopCount; i++)
        {
            var rowRect = new RectangleF(pad, y, right - pad, TopRowH);
            if (i < top.Count)
            {
                var a = top[i];
                string val = PanelTopList.ValueText(a, sort);
                float vw = Gfx.Measure(g, val, fVal).Width;
                Gfx.Text(g, Ellipsize(g, a.Name, fName, rowRect.Width - vw - 10), fName, th.Text, pad, y + 3);
                Gfx.Text(g, val, fVal, th.Label, right - vw, y + 3);

                float frac = (float)Math.Clamp(PanelTopList.Value(a, sort) / full, 0, 1);
                var bar = new RectangleF(pad, y + TopRowH - 5, rowRect.Width, 2f);
                using (var bp = Gfx.RoundRect(bar, 1f))
                using (var bb = new SolidBrush(th.Track)) g.FillPath(bb, bp);
                if (frac > 0.01f)
                {
                    using var fp = Gfx.RoundRect(new RectangleF(bar.X, bar.Y, bar.Width * frac, bar.Height), 1f);
                    using var fb = new SolidBrush(Color.FromArgb(150, accent));
                    g.FillPath(fb, fp);
                }
                _topRows.Add((rowRect, "a:" + a.Key));
            }
            y += TopRowH;
        }
    }

    /// <summary>TOP 行展示名超宽时截断加省略号（Gfx.Text 没有省略模式，手动收）。</summary>
    private static string Ellipsize(Graphics g, string text, Font f, float maxW)
    {
        if (text.Length == 0 || Gfx.Measure(g, text, f).Width <= maxW) return text;
        while (text.Length > 1 && Gfx.Measure(g, text + "…", f).Width > maxW) text = text[..^1];
        return text + "…";
    }

    /// <summary>排序段控点击：换键 + 落盘 + 用 Update 缓存的实参立刻重画（不等下个采样 tick）。</summary>
    private void OnTopSortClicked(int idx)
    {
        if (_cfgRef == null || _lastTh == null || idx >= _topSortItems.Count) return;
        var next = _topSortItems[idx];
        if (_cfgRef.PanelTopSort == next) return;
        _cfgRef.PanelTopSort = next;
        _cfgRef.Save();
        Update(_lastS, _cfgRef, _lastTh, _lastApps);
    }

    // ---------------- 时间窗 ----------------

    /// <summary>标题行右侧的 1m/5m/1h/24h 段。未开「跨重启保留」时不画 24h（没有数据源，不给死链）。</summary>
    private void DrawWindowChips(Graphics g, AppConfig cfg, Theme th, float right)
    {
        // 历史关闭：退回「1s · 60 点」说明 —— 几个时间窗都只有同一份 60 点缓冲，给出来是骗人
        if (History is not { Enabled: true })
        {
            string cap = $"{Math.Max(1, cfg.RefreshMs / 1000.0):0.#}s · 60 点";
            var cf = Gfx.F("Segoe UI", 12f, FontStyle.Regular);   // A3 最小 12（原 9.5 废弃）
            Gfx.Text(g, cap, cf, th.Label, right - Gfx.Measure(g, cap, cf).Width, 16f);
            for (int i = 0; i < _winRects.Length; i++) _winRects[i] = RectangleF.Empty;
            return;
        }

        int count = LongWindowAvailable() ? Windows.Length : Windows.Length - 1;
        float x = right - count * ChipW - (count - 1) * ChipGap;
        var f = Gfx.F("Segoe UI", 12f, FontStyle.Regular);   // A3 最小 12（原 9 废弃）
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

            // TOP 排序段控 / TOP 行（v0.35）
            for (int i = 0; i < _topSortRects.Length; i++)
            {
                if (_topSortRects[i].Width > 0 && _topSortRects[i].Contains(pt))
                {
                    OnTopSortClicked(i);
                    return;
                }
            }
            foreach (var (rect, key) in _topRows)
            {
                if (!rect.Contains(pt)) continue;
                Hide();
                TopClicked?.Invoke(key);
                return;
            }

            if (_closeRect.Contains(pt)) { Hide(); return; }
            if (_dashRect.Contains(pt)) { Hide(); DashboardClicked?.Invoke(); return; }
            if (_gearRect.Contains(pt)) { Hide(); SettingsClicked?.Invoke(); return; }
        }
        base.WndProc(ref m);
    }
}
