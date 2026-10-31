using System.Drawing.Drawing2D;
using WMonitor.Metrics;

namespace WMonitor.UI;

/// <summary>
/// Bento 卡片：圆角底板 + 标题 + 内容区。
///
/// 圆角不用 <c>Region</c>（Region 是硬裁切，边缘一圈锯齿），而是：
/// 1) <see cref="OnPaintBackground"/> 整块刷成「卡片外」的窗口底色；
/// 2) <see cref="OnPaint"/> 再画一层抗锯齿圆角底板 + 1px 描边。
/// 角落没有子控件，所以看上去就是干净的圆角。
///
/// <c>BackColor</c> 必须保持卡片色：子控件（Chip / Segmented / Slider / Stepper）
/// 都用 <c>g.Clear(Parent.BackColor)</c> 取底色，取错了会出现方块补丁。
/// </summary>
public sealed class BentoCard : Panel
{
    public Theme Th = Theme.Dark;

    /// <summary>卡片之外的底色（窗口底色），用来盖掉圆角外的四个角。</summary>
    public Color Outside = Color.Black;

    public string Title = "";
    public string Note = "";

    /// <summary>标题前的小圆点（配色语义：CPU 蓝、内存紫、GPU 绿…）。</summary>
    public Color Accent = Color.Empty;

    public int Radius = 14;
    public int PadX = 18;

    /// <summary>标题区高度，内容从这一行往下排。</summary>
    public int Head = 50;

    public int PadBottom = 18;

    public int InnerW => Width - PadX * 2;

    private Font _titleFont = null!;
    private Font _noteFont = null!;

    public BentoCard()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        DoubleBuffered = true;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        EnsureFonts();
    }

    private void EnsureFonts()
    {
        _titleFont ??= new Font("Segoe UI", 10.5f, FontStyle.Bold);
        _noteFont ??= new Font("Segoe UI", 8.5f, FontStyle.Regular);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // 整块先铺窗口底色，圆角外侧就自然是窗口色
        e.Graphics.Clear(Outside);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        EnsureFonts();
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

        var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        using (var path = Gfx.RoundRect(r, Radius))
        {
            using (var bg = new SolidBrush(Th.CardBg)) g.FillPath(bg, path);
            using (var pen = new Pen(Th.CardBorder, 1f)) g.DrawPath(pen, path);
        }

        float x = PadX;
        if (Accent != Color.Empty)
        {
            using var dot = new SolidBrush(Accent);
            g.FillEllipse(dot, x, 21f, 6f, 6f);
            x += 13f;
        }

        using (var tb = new SolidBrush(Th.Text))
            g.DrawString(Title, _titleFont, tb, x, 14f, StringFormat.GenericTypographic);

        if (!string.IsNullOrEmpty(Note))
        {
            var sf = new StringFormat
            {
                Alignment = StringAlignment.Far,
                LineAlignment = StringAlignment.Near,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            };
            using var nb = new SolidBrush(Th.Label);
            g.DrawString(Note, _noteFont, nb, new RectangleF(0, 17f, Width - PadX, 16f), sf);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _titleFont?.Dispose();
            _noteFont?.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// 托盘图标预览：把当前样式 + 当前指标槽位按 40 px 画出来。
/// 设置里改样式/指标时立刻重画，用户不用盯着托盘看效果。
/// 数值是示意数据（设置窗口没有采样器），只反映"长什么样"。
/// </summary>
public sealed class TrayPreview : Control
{
    public Theme Th = Theme.Dark;
    public AppConfig Cfg = new();

    public TrayPreview()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        DoubleBuffered = true;
        Width = 72;
        Height = 72;
    }

    private static readonly MetricsSnapshot Sample = new()
    {
        CpuPercent = 54, GpuPercent = 17, GpuTempC = 62,
        NetDownBps = 3.4 * 1024 * 1024, NetUpBps = 0.6 * 1024 * 1024,
        DiskReadBps = 1.2 * 1024 * 1024, DiskWriteBps = 0.4 * 1024 * 1024,
        RamTotalBytes = 64UL << 30, RamUsedBytes = (ulong)(25.0 * (1UL << 30)),
        VramTotalBytes = 12UL << 30, VramUsedBytes = (ulong)(5.0 * (1UL << 30)),
        HasGpu = true, HasVramTotal = true, HasTemp = true
    };

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Th.CardBg);

        // 模拟任务栏按钮底：深色主题用比卡片更亮一点的方块，浅色用白块
        var tile = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        using (var path = Gfx.RoundRect(tile, 12f))
        {
            using var bg = new SolidBrush(Th.IsDark ? Color.FromArgb(255, 32, 36, 43) : Color.FromArgb(255, 0xF3, 0xF5, 0xF8));
            g.FillPath(bg, path);
            using var pen = new Pen(Th.CardBorder, 1f);
            g.DrawPath(pen, path);
        }

        int size = Math.Min(Width, Height) - 24;
        using var ico = TrayIconRenderer.Render(Cfg, Sample, Th, size);
        using var bmp = ico.ToBitmap();
        g.DrawImage(bmp, (Width - size) / 2f, (Height - size) / 2f, size, size);
    }
}

/// <summary>
/// 自绘圆角按钮：无描边，hover 提亮 + 抬高投影，按下变暗并整体下沉 1px。
/// 系统 <see cref="Button"/>（FlatStyle.Flat）画不出圆角，也压不住 hover 态，
/// 所以这里直接继承 Control 自己画；实现了 <see cref="IButtonControl"/>，
/// 仍然能当 <c>Form.AcceptButton</c> 用。
///
/// 控件高度 = 视觉按钮高度 + <see cref="Shadow"/>，多出来的几像素留给投影，
/// 否则阴影会被控件边界裁掉。
/// </summary>
public sealed class BentoButton : Control, IButtonControl
{
    public Theme Th = Theme.Dark;
    public bool Primary;
    public int Radius = 10;

    /// <summary>控件底部留给投影的高度。</summary>
    public int Shadow = 6;

    public DialogResult DialogResult { get; set; } = DialogResult.None;

    private bool _hover, _down;
    private Font _font = null!;

    public BentoButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        DoubleBuffered = true;
        Cursor = Cursors.Hand;
        TabStop = true;
        Height = 44;
        Width = 100;
    }

    private int BodyH => Height - Shadow;

    private static Color Mix(Color a, Color b, float t) => Color.FromArgb(
        a.A,
        (int)(a.R + (b.R - a.R) * t),
        (int)(a.G + (b.G - a.G) * t),
        (int)(a.B + (b.B - a.B) * t));

    private Color Body()
    {
        if (_down)
            return Primary ? Mix(Th.Accent, Color.Black, 0.24f)
                           : Mix(Th.Soft, Color.Black, Th.IsDark ? 0.30f : 0.14f);
        if (_hover)
            return Primary ? Mix(Th.Accent, Color.White, 0.20f)
                           : Mix(Th.Soft, Th.IsDark ? Color.White : Color.Black, Th.IsDark ? 0.14f : 0.07f);
        return Primary ? Th.Accent : (Th.IsDark ? Th.Soft : Th.SoftLight);
    }

    private Color TextColor() => Primary ? Color.White : Th.Text;

    protected override void OnPaint(PaintEventArgs e)
    {
        _font ??= new Font("Segoe UI", 9.5f, Primary ? FontStyle.Bold : FontStyle.Regular);

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        g.Clear(Parent?.BackColor ?? Th.WindowBg);

        float h = BodyH;

        // 投影：常态不画（平贴）；hover 抬高，按下收回。
        // 必须由外向内叠 —— 外圈最淡、内圈最深。反过来（外圈最深）会在按钮下面留一条硬边，
        // 浅色底上尤其难看。
        int layers = _down ? 2 : _hover ? 10 : 0;
        if (layers > 0)
        {
            var shade = Th.IsDark && Primary ? Mix(Th.Accent, Color.Black, 0.68f) : Color.Black;
            int peak = _down ? 14 : Th.IsDark ? 30 : 24;
            float reach = Math.Max(2f, Shadow - 1.5f);   // 控件底部留给投影的可用下沉距离
            for (int i = layers; i >= 1; i--)
            {
                float k = i / (float)layers;              // 1 = 最外圈（最淡、最靠下）
                float off = 1f + reach * k;
                float inset = 0.4f + 2.2f * k;
                var sr = new RectangleF(inset, off, Width - inset * 2, h);
                using var sp = Gfx.RoundRect(sr, Radius);
                int alpha = Math.Max(1, (int)(peak * (1f - k * 0.85f)));
                using var sb = new SolidBrush(Color.FromArgb(alpha, shade));
                g.FillPath(sb, sp);
            }
        }

        var rect = new RectangleF(0.5f, 0.5f, Width - 1f, h - 1f);
        using (var path = Gfx.RoundRect(rect, Radius))
        using (var bg = new SolidBrush(Body()))
            g.FillPath(bg, path);

        // 焦点环（键盘 Tab 过来时）
        if (Focused)
        {
            using var fp = Gfx.RoundRect(new RectangleF(2.5f, 2.5f, Width - 5f, h - 5f), Radius - 3);
            using var pen = new Pen(Color.FromArgb(120, TextColor()), 1f);
            g.DrawPath(pen, fp);
        }

        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var tb = new SolidBrush(TextColor());
        var textRect = new RectangleF(0, _down ? 1.5f : 0, Width, h);
        g.DrawString(Text, _font, tb, textRect, sf);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _down = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { _down = true; Invalidate(); }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_down) { _down = false; Invalidate(); }
        base.OnMouseUp(e);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { PerformClick(); e.Handled = true; }
        base.OnKeyDown(e);
    }

    public void NotifyDefault(bool value) { }

    /// <summary>
    /// 和 <see cref="Button"/> 一样：点击时把 DialogResult 交给 Form（模态窗口一赋值就关窗）。
    /// 自定义控件不写这段，点「完成」是关不掉窗口的。
    /// </summary>
    protected override void OnClick(EventArgs e)
    {
        if (DialogResult != DialogResult.None)
        {
            var f = FindForm();
            if (f != null) f.DialogResult = DialogResult;
        }
        base.OnClick(e);
    }

    public void PerformClick()
    {
        if (Enabled) OnClick(EventArgs.Empty);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _font?.Dispose();
        base.Dispose(disposing);
    }
}
