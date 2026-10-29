using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using WMonitor.Interop;
using WMonitor.Metrics;

namespace WMonitor.UI;

/// <summary>任务栏贴片：无边框分层窗口，吸附在托盘区左侧。</summary>
public sealed class TaskbarStrip : LayeredWindow
{
    private const int BaseHeight = 30;      // 96dpi 下的贴片高度
    private const float BaseRadius = 7f;
    private const float BasePad = 10f;
    private const int HistLen = 5;

    private readonly Queue<double> _hCpu = new();
    private readonly Queue<double> _hRam = new();
    private readonly Queue<double> _hGpu = new();
    private readonly Queue<double> _hVram = new();

    private Bitmap? _bmp;
    private Graphics? _g;
    private int _lastW = -1, _lastH = -1;
    private int _lastX = int.MinValue, _lastY = int.MinValue;

    public event EventHandler? Clicked;

    public TaskbarStrip()
    {
        Name = "WMonitorStrip";
        Visible = false;
    }

    private readonly record struct Cell(string Label, string Value, string Unit, Color Color, double Pct, bool Bar);

    public void Update(MetricsSnapshot s, AppConfig cfg, Theme th)
    {
        // 用户关掉「显示贴片」：不画、不定位，只留托盘图标
        if (!cfg.ShowStrip) { if (Visible) Hide(); return; }

        Push(_hCpu, s.CpuPercent);
        Push(_hRam, s.RamPercent);
        Push(_hGpu, s.GpuPercent);
        Push(_hVram, s.VramPercent);

        var cells = BuildCells(s, cfg, th);
        if (cfg.Strip == StripStyle.Rings) cells = cells.Where(c => c.Bar).ToList();
        if (cells.Count == 0) { if (Visible) Hide(); return; }

        float k = DeviceDpi / 96f;

        int w, h;
        var measure = MeasureG;
        if (cfg.Strip == StripStyle.Rings)
        {
            w = (int)Math.Ceiling(BasePad * 2 + cells.Count * 26 * k + (cells.Count - 1) * 8 * k);
            h = (int)Math.Ceiling(BaseHeight * k);
        }
        else
        {
            w = (int)Math.Ceiling(MeasureRow(measure, cells, cfg.Strip, k) + BasePad * 2 * k);
            h = (int)Math.Ceiling(BaseHeight * k);
        }

        w = Math.Max(w, 24);
        if (_bmp == null || _lastW != w || _lastH != h)
        {
            _g?.Dispose();
            _bmp?.Dispose();
            _bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            _g = Graphics.FromImage(_bmp);
            _lastW = w; _lastH = h;
        }

        var g = _g!;
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            g.Clear(Color.Transparent);

            var rect = new RectangleF(0.5f, 0.5f, w - 1, h - 1);
            using var path = Gfx.RoundRect(rect, BaseRadius * k);
            int op = Math.Clamp(cfg.StripOpacity, 0, 100);
            using (var bg = new SolidBrush(WithAlpha(th.PillBg, op))) g.FillPath(bg, path);
            using (var bd = new Pen(WithAlpha(th.PillBorder, op), Math.Max(1f, k))) g.DrawPath(bd, path);

            if (cfg.Strip == StripStyle.Rings)
                DrawRings(g, cells, k, w, h);
            else
                DrawRow(g, cells, cfg.Strip, k, BasePad * k, h, th);
        }

        if (_dragging) return;   // 拖动中由鼠标消息驱动，不要拉回锚点

        if (!TryAnchor(cfg, w, h, out int x, out int y))
        {
            if (Visible) Hide();
            return;
        }

        if (!Visible) Show();
        bool zOk = ApplyZOrder(cfg);
        bool blitOk = Blit(_bmp, x, y);
        if (Environment.GetEnvironmentVariable("WMONITOR_DEBUG") == "1")
            Dbg.Log("blit=" + blitOk + " z=" + zOk + " x=" + x + " y=" + y + " w=" + w + " h=" + h + " vis=" + Visible);
        _lastX = x; _lastY = y;
    }

    /// <summary>按配置把窗口压到/抬到置顶层。任务栏每次被点击都会抢 TOPMOST，必须每 tick 重申一次。</summary>
    private bool ApplyZOrder(AppConfig cfg)
    {
        return Native.SetWindowPos(Handle,
            cfg.AlwaysOnTop ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST,
            0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    /// <summary>当前贴片的屏幕矩形（用于把详情面板贴到它上方）。</summary>
    public Rectangle StripBounds => new(_lastX, _lastY, Math.Max(0, _lastW), Math.Max(0, _lastH));

    /// <summary>位置校验：任务栏位置/高度变了就重贴（Explorer 重启、换分辨率、自动隐藏）。</summary>
    public void Reanchor(AppConfig cfg)
    {
        if (_bmp == null || !Visible || _dragging || !cfg.ShowStrip) return;
        if (TryAnchor(cfg, _bmp.Width, _bmp.Height, out int x, out int y))
        {
            ApplyZOrder(cfg);
            if (x != _lastX || y != _lastY) { Blit(_bmp, x, y); _lastX = x; _lastY = y; }
        }
    }

    /// <summary>把颜色的 alpha 按百分比缩放（0 = 全透明，100 = 保持原样）。</summary>
    private static Color WithAlpha(Color c, int pct) =>
        Color.FromArgb((int)Math.Round(c.A * Math.Clamp(pct, 0, 100) / 100.0), c.R, c.G, c.B);

    private static void Push(Queue<double> q, double v)
    {
        q.Enqueue(v);
        while (q.Count > HistLen) q.Dequeue();
    }

    private List<Cell> BuildCells(MetricsSnapshot s, AppConfig cfg, Theme th)
    {
        var list = new List<Cell>(8);
        if (cfg.ShowCpu) list.Add(new("CPU", Fmt(s.CpuPercent), "%", th.Cpu, s.CpuPercent, true));
        if (cfg.ShowRam) list.Add(new("RAM", Fmt(s.RamPercent), "%", th.Ram, s.RamPercent, true));
        if (cfg.ShowGpu && s.HasGpu) list.Add(new("GPU", Fmt(s.GpuPercent), "%", th.Gpu, s.GpuPercent, true));
        if (cfg.ShowVram && s.HasGpu && s.HasVramTotal)
            list.Add(new("VRAM", s.VramGb.ToString("0.0"), "/" + s.VramTotalGb.ToString("0"), th.Vram, s.VramPercent, true));
        if (cfg.ShowNet)
        {
            list.Add(new("↓", NetFmt(s.NetDownBps), "", th.Net, 0, false));
            list.Add(new("↑", NetFmt(s.NetUpBps), "", th.Net, 0, false));
        }
        if (cfg.ShowTemp && s.HasTemp)
            list.Add(new("T", s.GpuTempC.ToString("0"), "°", th.Gpu, 0, false));
        return list;
    }

    private static string Fmt(double v) =>
        double.IsNaN(v) ? "0" : v >= 100 ? "100" : ((int)Math.Round(v)).ToString();

    private static string NetFmt(double bytesPerSec)
    {
        double kb = bytesPerSec / 1024.0;
        if (kb < 1) return "0";
        if (kb < 1000) return ((int)kb).ToString() + "K";
        double mb = kb / 1024.0;
        return mb < 10 ? mb.ToString("0.0") + "M" : ((int)mb).ToString() + "M";
    }

    // ---------------- 风格 A / B ----------------

    private float MeasureRow(Graphics g, List<Cell> cells, StripStyle style, float k)
    {
        float pad = BasePad * k, x = 0;
        var fLabel = Gfx.F(BaseFont, 8.5f * k, FontStyle.Bold);
        var fValue = Gfx.F(BaseFont, 12f * k, FontStyle.Bold);
        var fUnit = Gfx.F(BaseFont, 9f * k, FontStyle.Regular);

        for (int i = 0; i < cells.Count; i++)
        {
            var c = cells[i];
            if (i > 0) x += 16 * k;
            x += 5 * k + 5 * k;                              // dot + gap
            x += Gfx.Measure(g, c.Label, fLabel).Width + 4 * k;
            x += ValueBox(g, c, fValue, k) + 2 * k;
            if (c.Unit.Length > 0) x += Gfx.Measure(g, c.Unit, fUnit).Width;
            if (style == StripStyle.Bars && c.Bar) x += 7 * k + 5 * (3f + 2f) * k;
        }
        _ = pad;
        return x;
    }

    private static float ValueBox(Graphics g, Cell c, Font f, float k)
    {
        string sample = c.Value.Contains('.') ? "88.8" : "88";
        return Math.Max(Gfx.Measure(g, c.Value, f).Width, Gfx.Measure(g, sample, f).Width);
    }

    private void DrawRow(Graphics g, List<Cell> cells, StripStyle style, float k, float pad, int h, Theme th)
    {
        var fLabel = Gfx.F(BaseFont, 8.5f * k, FontStyle.Bold);
        var fValue = Gfx.F(BaseFont, 12f * k, FontStyle.Bold);
        var fUnit = Gfx.F(BaseFont, 9f * k, FontStyle.Regular);

        float cy = h / 2f;
        float x = pad;
        var hist = new[] { _hCpu, _hRam, _hGpu, _hVram };
        int histIdx = 0;

        for (int i = 0; i < cells.Count; i++)
        {
            var c = cells[i];
            if (i > 0)
            {
                using var sep = new Pen(th.Sep, Math.Max(1f, k));
                g.DrawLine(sep, x + 8 * k, cy - 6 * k, x + 8 * k, cy + 6 * k);
                x += 16 * k;
            }

            // dot
            using (var dot = new SolidBrush(c.Color))
                g.FillEllipse(dot, x, cy - 2.5f * k, 5 * k, 5 * k);
            x += 10 * k;

            // label
            float lw = Gfx.Measure(g, c.Label, fLabel).Width;
            Gfx.Text(g, c.Label, fLabel, Color.FromArgb(165, c.Color), x, cy - 4.5f * k);
            x += lw + 4 * k;

            // value（右对齐到固定盒，数字不跳）
            float bw = ValueBox(g, c, fValue, k);
            float vw = Gfx.Measure(g, c.Value, fValue).Width;
            Gfx.Text(g, c.Value, fValue, th.Text, x + (bw - vw), cy - 6.5f * k);
            x += bw + 2 * k;

            // unit
            if (c.Unit.Length > 0)
            {
                float uw = Gfx.Measure(g, c.Unit, fUnit).Width;
                Gfx.Text(g, c.Unit, fUnit, th.Label, x, cy - 2f * k);
                x += uw;
            }

            // mini bars
            if (style == StripStyle.Bars && c.Bar)
            {
                x += 7 * k;
                var q = histIdx < hist.Length ? hist[histIdx] : null;
                histIdx++;
                if (q != null && q.Count > 0)
                {
                    float barW = 3f * k, gap = 2f * k, maxH = 14f * k;
                    using var bb = new SolidBrush(c.Color);
                    int idx = 0;
                    foreach (var v in q)
                    {
                        float bh = Math.Max(1.5f * k, (float)(Math.Clamp(v, 0, 100) / 100.0) * maxH);
                        g.FillRectangle(bb, x + idx * (barW + gap), cy + 6.5f * k - bh, barW, bh);
                        idx++;
                    }
                    x += HistLen * (barW + gap);
                }
            }
        }
    }

    // ---------------- 风格 C：圆环 ----------------

    private void DrawRings(Graphics g, List<Cell> cells, float k, int w, int h)
    {
        float d = 26 * k;
        float gap = 8 * k;
        float total = cells.Count * d + (cells.Count - 1) * gap;
        float x = (w - total) / 2f;
        float y = (h - d) / 2f;

        foreach (var c in cells)
        {
            var rect = new RectangleF(x, y, d, d);
            using (var track = new Pen(Color.FromArgb(40, 255, 255, 255), 3f * k))
                g.DrawEllipse(track, rect);
            float sweep = (float)(Math.Clamp(c.Pct, 0, 100) / 100.0 * 360.0);
            if (sweep > 1f)
            {
                using var pen = new Pen(c.Color, 3f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(pen, RectangleF.Inflate(rect, 0, 0), -90, sweep);
            }
            x += d + gap;
        }
    }

    // ---------------- 定位 ----------------

    private static bool TryAnchor(AppConfig cfg, int w, int h, out int x, out int y)
    {
        x = y = 0;

        // 自由位置：拖动确定过就直接用绝对坐标
        if (cfg.Anchor == StripAnchor.Free)
        {
            if (cfg.FreeX < 0 || cfg.FreeY < 0) return false;
            x = cfg.FreeX;
            y = cfg.FreeY;
            ClampToScreen(ref x, ref y, w, h);
            return true;
        }

        IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return false;
        if (!Native.GetWindowRect(tray, out var tr)) return false;
        if (tr.Height < 12 || tr.Width < 200) return false;   // 自动隐藏状态

        switch (cfg.Anchor)
        {
            case StripAnchor.TaskbarLeft:
                x = tr.Left + (int)Math.Round(8 * (h / 30f));
                break;

            case StripAnchor.BeforeApps:
                GetAppsBounds(tray, tr, out int al, out _);
                x = al - w - (int)Math.Round(8 * (h / 30f));
                break;

            case StripAnchor.AfterApps:
                GetAppsBounds(tray, tr, out _, out int ar);
                x = ar + (int)Math.Round(8 * (h / 30f));
                break;

            default: // BeforeTray
                x = GetTrayLeft(tray, tr, h) - w - (int)Math.Round(6 * (h / 30f));
                break;
        }

        x += cfg.OffsetX;
        y = tr.Top + (tr.Height - h) / 2;
        return true;
    }

    /// <summary>
    /// 应用图标区的左右边界。
    /// 左界取「开始」按钮（Win11 上 Start 窗口可靠存在）；
    /// 右界 Win11 没有可用 HWND（MSTaskListWClass 的矩形只有 440px，不准；UIA 树也是空的），
    /// 取任务栏中心与托盘左界的中点 —— 本机实测误差约 30px，可用 OffsetX 微调。
    /// </summary>
    private static void GetAppsBounds(IntPtr tray, Native.RECT tr, out int left, out int right)
    {
        IntPtr start = Native.FindWindowEx(tray, IntPtr.Zero, "Start", null);
        if (start != IntPtr.Zero && Native.GetWindowRect(start, out var sr) && sr.Width > 0)
            left = sr.Left;
        else
            left = tr.Left + tr.Width / 4;

        int trayLeft = GetTrayLeft(tray, tr, 48);
        int cx = tr.Left + tr.Width / 2;
        right = (cx + trayLeft) / 2;
    }

    private static int GetTrayLeft(IntPtr tray, Native.RECT tr, int h)
    {
        IntPtr notify = Native.FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify != IntPtr.Zero && Native.GetWindowRect(notify, out var nr) && nr.Width > 0)
            return nr.Left;
        return tr.Right - (int)(180 * (h / 30f));
    }

    private static void ClampToScreen(ref int x, ref int y, int w, int h)
    {
        var vs = SystemInformation.VirtualScreen;
        x = Math.Clamp(x, vs.Left, Math.Max(vs.Left, vs.Right - w));
        y = Math.Clamp(y, vs.Top, Math.Max(vs.Top, vs.Bottom - h));
    }

    /// <summary>拖动结束：落在任务栏上就吸附垂直居中，否则保持自由坐标。</summary>
    public event Action<Point>? Moved;

    private bool _dragging;
    private bool _dragMoved;
    private Point _dragCursor0;
    private Point _dragWin0;

    private const string BaseFont = "Segoe UI";

    private static Graphics? _measureG;
    private static Graphics MeasureG
    {
        get
        {
            if (_measureG == null)
            {
                _measureG = Graphics.FromImage(new Bitmap(1, 1));
                _measureG.PageUnit = GraphicsUnit.Pixel;
            }
            return _measureG;
        }
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_LBUTTONDOWN = 0x0201;
        const int WM_LBUTTONUP = 0x0202;
        const int WM_MOUSEMOVE = 0x0200;

        switch (m.Msg)
        {
            case WM_LBUTTONDOWN:
                Native.GetCursorPos(out var p0);
                _dragCursor0 = new Point(p0.X, p0.Y);
                _dragWin0 = new Point(_lastX, _lastY);
                _dragMoved = false;
                _dragging = true;
                Native.SetCapture(Handle);
                return;

            case WM_MOUSEMOVE:
                if (_dragging)
                {
                    Native.GetCursorPos(out var cp);
                    int dx = cp.X - _dragCursor0.X;
                    int dy = cp.Y - _dragCursor0.Y;
                    if (!_dragMoved && Math.Abs(dx) + Math.Abs(dy) > 4) _dragMoved = true;
                    if (_dragMoved && _bmp != null)
                    {
                        int nx = _dragWin0.X + dx;
                        int ny = _dragWin0.Y + dy;
                        ClampToScreen(ref nx, ref ny, _bmp.Width, _bmp.Height);
                        Blit(_bmp, nx, ny);
                        _lastX = nx;
                        _lastY = ny;
                    }
                    return;
                }
                break;

            case WM_LBUTTONUP:
                if (_dragging)
                {
                    Native.ReleaseCapture();
                    _dragging = false;

                    if (!_dragMoved)
                    {
                        Clicked?.Invoke(this, EventArgs.Empty);
                        return;
                    }

                    // 落在任务栏带内就吸附垂直居中
                    IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
                    if (tray != IntPtr.Zero && Native.GetWindowRect(tray, out var tr)
                        && _lastY + _lastH > tr.Top && _lastY < tr.Bottom
                        && _lastX + _lastW > tr.Left && _lastX < tr.Right)
                    {
                        int snapped = tr.Top + (tr.Height - _lastH) / 2;
                        if (snapped != _lastY && _bmp != null)
                        {
                            Blit(_bmp, _lastX, snapped);
                            _lastY = snapped;
                        }
                    }

                    Moved?.Invoke(new Point(_lastX, _lastY));
                    return;
                }
                break;
        }
        base.WndProc(ref m);
    }
}
