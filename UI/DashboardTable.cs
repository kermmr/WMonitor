using System.Drawing.Drawing2D;
using WMonitor.Metrics;

namespace WMonitor.UI;

/// <summary>
/// 主面板列表：整块自绘（DataGridView 深色主题不可控且重，符合项目「能自绘不自绘系统控件」的惯例）。
///
/// 职责边界：过滤 / 排序由 <see cref="MainDashboardForm"/> 完成，本控件收到的是已排好序的行，
/// 只负责渲染 + 命中（列头排序点击、行选中、滚轮 / 滚动条）。
/// 六列：应用 | PID | CPU | 内存 | GPU | 显存（GPU 两列数据来自 GpuProcessSampler，无数据显示 —）。
/// </summary>
public sealed class DashboardTable : Control
{
    public enum Column { Name = 0, Pid = 1, Cpu = 2, Memory = 3, Gpu = 4, Vram = 5 }

    /// <summary>行视图模型。由 Form 从 AppMetric / ProcessMetric 组装，每帧重建。</summary>
    public sealed class Row
    {
        public string Key = "";               // 选中恢复键："a:<聚合键>" / "p:<pid>"（GPU 视图复用 p:）
        public bool IsApp;                    // 按应用聚合行
        public string Name = "";
        public string? Path;
        public string? Description;
        public string? Company;
        public int Pid;                       // 主 PID
        public int MemberCount = 1;
        public double Cpu;
        public long WorkingSet;
        public long PrivateBytes;
        public double Gpu;
        public long GpuDedicated;
        public long GpuShared;
        public string? GpuEngine;
        public bool Foreground, HasWindow, Limited, System;
        public string? Architecture;
        public DateTime? StartedAt;
        public string? WindowTitle;
        public IReadOnlyList<ProcessMetric>? Members;
    }

    public Theme Th = Theme.Dark;
    public float UiScale = 1f;

    /// <summary>图标缓存（阶段 3）：绘制时按行 Path 查；null = 未接（回退首字母圆点）。</summary>
    public IconCache? Icons;

    /// <summary>第一列的列头标题：按应用视图=应用，按进程视图=进程。</summary>
    public string NameTitle = "应用";

    public List<Row> Rows = new();
    public Column SortCol = Column.Cpu;
    public bool SortDesc = true;
    public int Selected = -1;
    public string EmptyText = "无匹配进程";

    /// <summary>空态副提示（过滤把行全隐藏时给「试试开启…」），空串不画。</summary>
    public string EmptyHint = "";

    public event Action<Column>? SortClicked;
    public event Action? SelectionChanged;

    private const int HeadH0 = 38, RowH0 = 48, BarW0 = 10, IconD0 = 28;
    private const float NameFrac = 0.36f;      // 应用列占比（六列后数值区变宽，相应调小）

    private int _scroll;
    private int _hover = -1;
    private int _headHover = -1;
    private bool _dragBar;
    private readonly RectangleF[] _headRects = new RectangleF[6];

    private static readonly Color[] IconPalette =
    {
        Color.FromArgb(0x4C, 0x8D, 0xFF), Color.FromArgb(0x37, 0xA1, 0x69),
        Color.FromArgb(0xB4, 0x53, 0x09), Color.FromArgb(0x7C, 0x5C, 0xE0),
        Color.FromArgb(0x0E, 0x9C, 0x94), Color.FromArgb(0xC2, 0x25, 0x5C),
        Color.FromArgb(0x0A, 0x84, 0xFF), Color.FromArgb(0xD1, 0x84, 0x00)
    };

    public DashboardTable()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        DoubleBuffered = true;
    }

    private int HeadH => (int)(HeadH0 * UiScale);
    private int RowH => (int)(RowH0 * UiScale);
    private float BarW => BarW0 * UiScale;

    private int VisibleRows => Math.Max(1, (Height - HeadH) / RowH);
    private int MaxScroll => Math.Max(0, Rows.Count - VisibleRows);

    public void SetRows(List<Row> rows, int selectedIndex)
    {
        Rows = rows;
        Selected = selectedIndex;
        _scroll = Math.Clamp(_scroll, 0, MaxScroll);
        Invalidate();
    }

    /// <summary>程序化选中（probe / 自动化验收用）：与鼠标点击同一条 SelectionChanged 路径。</summary>
    public void SelectRow(int index)
    {
        Selected = Math.Clamp(index, -1, Rows.Count - 1);
        if (Selected >= 0 && (Selected < _scroll || Selected >= _scroll + VisibleRows))
            _scroll = Math.Clamp(Selected - VisibleRows / 2, 0, MaxScroll);
        Invalidate();
        SelectionChanged?.Invoke();
    }

    protected override void OnPaintBackground(PaintEventArgs e) =>
        e.Graphics.Clear(Parent?.BackColor ?? Th.WindowBg);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

        float s = UiScale;
        var full = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using (var card = Gfx.RoundRect(full, 12 * s))
        {
            using var bg = new SolidBrush(Th.CardBg);
            g.FillPath(bg, card);
            using var pen = new Pen(Th.CardBorder, 1f);
            g.DrawPath(pen, card);
        }

        g.SetClip(new RectangleF(1, 1, Width - 2, Height - 2));

        var cols = ColumnRects();
        DrawHeader(g, cols);

        if (Rows.Count == 0)
        {
            var f = Gfx.F("Segoe UI", 12f * s, FontStyle.Regular);
            using var b = new SolidBrush(Th.Label);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(EmptyText, f, b, new RectangleF(0, HeadH, Width, Height - HeadH), sf);
            if (EmptyHint.Length > 0)
            {
                var f2 = Gfx.F("Segoe UI", 12f * s, FontStyle.Regular);
                g.DrawString(EmptyHint, f2, b, new RectangleF(0, HeadH + 26 * s, Width, Height - HeadH), sf);
            }
            g.ResetClip();
            return;
        }

        int first = _scroll;
        int last = Math.Min(Rows.Count, first + VisibleRows + 1);
        for (int i = first; i < last; i++)
            DrawRow(g, Rows[i], i, HeadH + (i - first) * (float)RowH, cols);

        g.ResetClip();
        DrawScrollbar(g);
    }

    private RectangleF[] ColumnRects()
    {
        float s = UiScale;
        float pidW = 88 * s, cpuW = 92 * s, memW = 106 * s, gpuW = 88 * s, vramW = 106 * s;
        float left = 12 * s, right = Width - 12 * s;
        float valueW = pidW + cpuW + memW + gpuW + vramW;
        float nameW = Math.Max(170 * s, (right - left) * NameFrac);
        if (left + nameW + valueW > right - BarW)
            nameW = Math.Max(120 * s, right - BarW - left - valueW);

        var r = new RectangleF[6];
        float x = left;
        r[(int)Column.Name] = new RectangleF(x, 0, nameW, HeadH); x += nameW;
        r[(int)Column.Pid] = new RectangleF(x, 0, pidW, HeadH); x += pidW;
        r[(int)Column.Cpu] = new RectangleF(x, 0, cpuW, HeadH); x += cpuW;
        r[(int)Column.Memory] = new RectangleF(x, 0, memW, HeadH); x += memW;
        r[(int)Column.Gpu] = new RectangleF(x, 0, gpuW, HeadH); x += gpuW;
        r[(int)Column.Vram] = new RectangleF(x, 0, vramW, HeadH);
        return r;
    }

    private void DrawHeader(Graphics g, RectangleF[] cols)
    {
        float s = UiScale;

        // 表头底（C14：token 化，内联 rgb(30,33,39)/rgb(246,247,250) 废弃）
        var headBg = new RectangleF(1, 1, Width - 2, HeadH - 1);
        using (var hb = new SolidBrush(Th.HeadBg))
            g.FillRectangle(hb, headBg);
        using (var sep = new Pen(Th.Sep, 1f))
            g.DrawLine(sep, 1, HeadH, Width - 1, HeadH);

        var f = Gfx.F("Segoe UI", 12f * s, FontStyle.Bold);   // A3：表头 Body Bold 12（原 11 废弃）
        string[] titles = { NameTitle, "PID", "CPU", "内存", "GPU", "显存" };
        var colsEnum = new[] { Column.Name, Column.Pid, Column.Cpu, Column.Memory, Column.Gpu, Column.Vram };

        for (int i = 0; i < titles.Length; i++)
        {
            var r = cols[i];
            _headRects[i] = r;
            bool active = SortCol == colsEnum[i];
            bool headHot = i == _headHover;

            if (active || headHot)
            {
                // A8 Segmented 语言（用户拍板）：互斥单选 = 圆角块 + Text 常驻；未选 hover 块 Hover、文字 Label→Text。
                // 块「紧贴列头内容」左右对称（v0.28 复检：整列块左空当远大于右，用户拍板「减少左边宽度和右边一样」，
                // 列宽/文字/数据位置全部不动）。内容盒：排序列 = 文字 + 三角（右缘 = 列右 -10s，文字右缘 = 列右 -16s），
                // 非排序列 = 文字（右缘 = 列右 -16s），名称列 = 文字（左缘 = 列左）。左右 pad 7s 与排序列右侧现状一致。
                // 块高 38-6=32。
                float textW = Gfx.Measure(g, titles[i], f).Width;
                float contentLeft, contentRight;
                if (colsEnum[i] == Column.Name)
                {
                    contentLeft = r.X;
                    contentRight = r.X + textW;
                }
                else
                {
                    contentRight = r.Right - (active ? 10f : 16f) * s;
                    contentLeft = contentRight - textW - (active ? 6f * s : 0f);
                }
                float padL = 16 * s, padR = 12 * s;   // v0.28 用户定稿:在 7/4(光学对齐)基础上 左+9 右+8
                var block = new RectangleF(contentLeft - padL, 3 * s, (contentRight - contentLeft) + padL + padR, HeadH - 6 * s);
                using var bp = Gfx.RoundRect(block, 6 * s);
                using var bb = new SolidBrush(active ? Th.SelectedBg : Th.Hover);
                g.FillPath(bb, bp);
            }

            // 数值列右对齐，列头留出箭头空间
            var rect = colsEnum[i] == Column.Name
                ? new RectangleF(r.X, 0, r.Width, HeadH)
                : new RectangleF(r.X, 0, r.Width - 16 * s, HeadH);
            var sf = new StringFormat
            {
                Alignment = colsEnum[i] == Column.Name ? StringAlignment.Near : StringAlignment.Far,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.None,
                FormatFlags = StringFormatFlags.NoWrap
            };
            using var tb = new SolidBrush(active || headHot ? Th.Text : Th.Label);
            g.DrawString(titles[i], f, tb, rect, sf);

            if (active)
            {
                // 小三角（自绘，别用 ▼ 字形——部分字体回退会发虚）
                float cx = colsEnum[i] == Column.Name
                    ? r.X + Gfx.Measure(g, titles[i], f).Width + 6 * s
                    : r.Right - 14 * s;
                float cy = HeadH / 2f;
                float d = 4 * s;
                var dir = SortDesc ? 1 : -1;
                var pts = new[]
                {
                    new PointF(cx - d, cy - dir * d * 0.7f),
                    new PointF(cx + d, cy - dir * d * 0.7f),
                    new PointF(cx, cy + dir * d * 0.7f)
                };
                using var fb = new SolidBrush(Th.Label);
                g.FillPolygon(fb, pts);
            }
        }
    }

    private void DrawRow(Graphics g, Row r, int index, float y, RectangleF[] cols)
    {
        float s = UiScale;
        float insetX = 3 * s, insetY = 2.5f * s;
        var rowRect = new RectangleF(insetX, y + insetY, Width - insetX * 2, RowH - insetY * 2);

        if (index == Selected || index == _hover)
        {
            using var p = Gfx.RoundRect(rowRect, 8 * s);
            using var b = new SolidBrush(index == Selected
                ? Color.FromArgb(Th.IsDark ? 60 : 26, Th.Accent)
                : Th.Hover);
            g.FillPath(b, p);
        }

        float cy = y + RowH / 2f;
        bool dim = r.System;

        // ---- 图标 + 名称 + 路径（阶段 3：真图标优先，抽不到 / 预算外回退首字母圆点）----
        float iconX = 12 * s;
        var iconRect = new RectangleF(iconX, cy - IconD0 * s / 2, IconD0 * s, IconD0 * s);
        Image? appIcon = Icons?.Get(r.Path);
        if (appIcon != null)
        {
            // 占满 28px 逻辑盒（v0.27 决定，与回退首字母块一致）；高质量缩放。
            // v0.27 曾把 DrawImage 误删成只画压暗层 —— 行图标整个消失，本次修复。
            var prevInterp = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(appIcon, iconRect);
            g.InterpolationMode = prevInterp;
            if (dim) using (var veil = new SolidBrush(Color.FromArgb(150, Th.CardBg)))
                g.FillRectangle(veil, iconRect);
        }
        else
        {
            var iconColor = IconPalette[(r.Name.GetHashCode() & 0x7FFFFFFF) % IconPalette.Length];
            using (var ib = new SolidBrush(Color.FromArgb(dim ? 70 : 255, iconColor)))
            {
                using var ip = Gfx.RoundRect(iconRect, 7 * s);
                g.FillPath(ib, ip);
            }
            string letter = Fmt.NameOr(r);
            letter = letter.Length == 0 ? "?" : char.ToUpperInvariant(letter[0]).ToString();
            var fIcon = Gfx.F("Segoe UI", 14f * s, FontStyle.Bold);   // A11：首字母 14 Bold（原 13 废弃）
            var sfC = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using (var wbr = new SolidBrush(Color.White))
                g.DrawString(letter, fIcon, wbr, iconRect, sfC);
        }

        float textX = iconRect.Right + 10 * s;
        var nameRect = cols[(int)Column.Name];
        var fName = Gfx.F("Segoe UI", 14f * s, FontStyle.Bold);     // A3：表格行名 Title 14（原 12.5 废弃）
        var fPath = Gfx.F("Segoe UI", 12f * s, FontStyle.Regular);  // A3 最小 12（原 9.5 废弃）

        string displayName = Fmt.NameOr(r);
        if (r.Limited) displayName += "（受限）";

        using (var nb = new SolidBrush(dim ? Th.Label : Th.Text))
            g.DrawString(Elide(g, displayName, fName, nameRect.Right - textX), fName, nb,
                textX, cy - RowH * 0.30f, StringFormat.GenericTypographic);

        string path = r.Path ?? (r.Limited ? "路径不可读" : "");
        if (r.IsApp && r.MemberCount > 1 && path.Length > 0) path += "";   // 路径下不再叠加，成员数在 PID 列
        if (path.Length > 0)
        {
            using var pb = new SolidBrush(Th.Label);
            g.DrawString(Elide(g, path, fPath, nameRect.Right - textX), fPath, pb,
                textX, cy + RowH * 0.04f, StringFormat.GenericTypographic);
        }

        // 前台标记：名称左侧圆点已在图标里体现，这里给名称加粗即可（ fName 已 Bold）
        // ---- PID ----
        var fVal = Gfx.F("Segoe UI", 12f * s, FontStyle.Regular);   // A3：表格数值 Body 12（原 11.5 废弃）
        string pidText = r.MemberCount > 1 ? $"{r.Pid} ×{r.MemberCount}" : r.Pid.ToString();
        DrawCell(g, pidText, cols[(int)Column.Pid], cy, fVal, dim ? Th.Label : Th.Text, bold: false);

        // ---- CPU ----
        string cpuText = r.Cpu.ToString("0.0") + "%";
        Color cpuColor;
        if (r.Cpu >= 1) cpuColor = Th.Cpu;
        else if (r.Cpu >= 0.05) cpuColor = Th.Text;
        else cpuColor = Th.Label;
        DrawCell(g, cpuText, cols[(int)Column.Cpu], cy, fVal, dim ? Th.Label : cpuColor,
                 bold: !dim && r.Cpu >= 1);

        // ---- 内存 ----
        DrawCell(g, Fmt.Bytes(r.WorkingSet), cols[(int)Column.Memory], cy, fVal, dim ? Th.Label : Th.Text, false);

        // ---- GPU ----（无 GPU 活动显示 —；GpuProcessSampler 不可用时 Form 不会填值，同为 —）
        string gpuText = r.Gpu >= 0.05 ? r.Gpu.ToString("0.0") + "%" : "—";
        Color gpuColor;
        if (r.Gpu >= 1) gpuColor = Th.Gpu;
        else if (r.Gpu >= 0.05) gpuColor = Th.Text;
        else gpuColor = Th.Label;
        DrawCell(g, gpuText, cols[(int)Column.Gpu], cy, fVal, dim ? Th.Label : gpuColor,
                 bold: !dim && r.Gpu >= 1);

        // ---- 显存 ----（专用显存主值，共享显存在详情条）
        DrawCell(g, r.GpuDedicated > 0 ? Fmt.Bytes(r.GpuDedicated) : "—",
                 cols[(int)Column.Vram], cy, fVal, dim ? Th.Label : Th.Text, false);
    }

    private void DrawCell(Graphics g, string text, RectangleF col, float cy, Font f, Color color, bool bold)
    {
        if (bold) f = Gfx.F("Segoe UI", f.Size, FontStyle.Bold);
        var rect = new RectangleF(col.X, 0, col.Width - 16 * UiScale, Height);
        var sf = new StringFormat
        {
            Alignment = StringAlignment.Far,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.None,
            FormatFlags = StringFormatFlags.NoWrap
        };
        using var b = new SolidBrush(color);
        g.DrawString(text, f, b, new RectangleF(col.X, cy - RowH / 2f, col.Width - 16 * UiScale, RowH), sf);
    }

    private void DrawScrollbar(Graphics g)
    {
        int content = Rows.Count * RowH;
        int viewport = Height - HeadH;
        if (content <= viewport || viewport <= 0) return;

        float s = UiScale;
        float thumbH = Math.Max(24 * s, viewport * (float)viewport / content);
        float trackH = viewport - thumbH;
        float ty = HeadH + trackH * (_scroll / (float)MaxScroll);

        using var tb = new SolidBrush(Color.FromArgb(70, Th.IsDark ? 255 : 0, Th.IsDark ? 255 : 0, Th.IsDark ? 255 : 0));
        using var tp = Gfx.RoundRect(new RectangleF(Width - BarW, ty, BarW - 2 * s, thumbH), (BarW - 2 * s) / 2f);
        g.FillPath(tb, tp);
    }

    // ---------------- 命中 ----------------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragBar)
        {
            int viewport = Height - HeadH;
            int content = Rows.Count * RowH;
            if (content > viewport && viewport > 0)
            {
                float thumbH = Math.Max(24 * UiScale, viewport * (float)viewport / content);
                float trackH = viewport - thumbH;
                if (trackH > 0)
                {
                    float t = (e.Y - HeadH - thumbH / 2) / trackH;
                    _scroll = Math.Clamp((int)Math.Round(t * MaxScroll), 0, MaxScroll);
                    Invalidate();
                }
            }
            return;
        }

        int hover = e.Y > HeadH && e.X < Width - BarW ? _scroll + (e.Y - HeadH) / RowH : -1;
        if (hover >= Rows.Count) hover = -1;
        if (hover != _hover) { _hover = hover; Invalidate(); }

        int headHover = e.Y < HeadH ? HeaderHit(e.Location) : -1;
        if (headHover != _headHover)
        {
            _headHover = headHover;
            Cursor = headHover >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = -1;
        _headHover = -1;
        Cursor = Cursors.Default;
        Invalidate();
        base.OnMouseLeave(e);
    }

    private int HeaderHit(Point p)
    {
        for (int i = 0; i < _headRects.Length; i++)
            if (_headRects[i].Contains(p)) return i;
        return -1;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) { base.OnMouseDown(e); return; }

        // 滚动条：抓 thumb 或按轨道翻页
        if (e.X >= Width - BarW && Rows.Count > 0)
        {
            int viewport = Height - HeadH;
            int content = Rows.Count * RowH;
            if (content > viewport)
            {
                float thumbH = Math.Max(24 * UiScale, viewport * (float)viewport / content);
                float trackH = viewport - thumbH;
                float ty = HeadH + trackH * (_scroll / (float)Math.Max(1, MaxScroll));
                if (e.Y >= ty && e.Y <= ty + thumbH)
                    _dragBar = true;
                else
                    _scroll = Math.Clamp(_scroll + (e.Y > ty ? VisibleRows : -VisibleRows), 0, MaxScroll);
                Invalidate();
            }
            return;
        }

        // 列头
        if (e.Y < HeadH)
        {
            for (int i = 0; i < _headRects.Length; i++)
            {
                if (!_headRects[i].Contains(e.Location)) continue;
                SortClicked?.Invoke((Column)i);
                return;
            }
            return;
        }

        // 行
        int idx = _scroll + (e.Y - HeadH) / RowH;
        if (idx >= 0 && idx < Rows.Count)
        {
            Selected = idx;
            Invalidate();
            SelectionChanged?.Invoke();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _dragBar = false;
        base.OnMouseUp(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (Rows.Count > 0)
        {
            int lines = -(e.Delta / 120) * 3;
            if (lines != 0)
            {
                _scroll = Math.Clamp(_scroll + lines, 0, MaxScroll);
                Invalidate();
            }
        }
        base.OnMouseWheel(e);
    }

    private static string Elide(Graphics g, string s, Font f, float maxW)
    {
        if (maxW <= 0 || s.Length == 0) return s;
        if (Gfx.Measure(g, s, f).Width <= maxW) return s;
        int len = s.Length;
        while (len > 1 && Gfx.Measure(g, s[..len] + "…", f).Width > maxW) len--;
        return s[..len] + "…";
    }
}
