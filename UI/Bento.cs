using System.Drawing.Drawing2D;

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
        return Primary ? Th.Accent : Th.Soft;
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
        int lift = _hover && !_down ? 4 : 0;

        // 投影：hover 抬高（更实、更靠下、带主色晕），按下收回贴回底面
        var shade = Primary ? Mix(Th.Accent, Color.Black, 0.68f) : Color.Black;
        int layers = _down ? 1 : _hover ? 8 : 3;
        for (int i = layers; i >= 1; i--)
        {
            float inset = i * 0.7f;
            var sr = new RectangleF(inset, 1.5f + lift + i * 1.0f, Width - inset * 2, h);
            using var sp = Gfx.RoundRect(sr, Radius);
            int alpha = _down ? 18 : _hover ? 3 + (int)(i * 2.4f) : 4 + i * 2;
            if (!Th.IsDark) alpha = (int)(alpha * 0.72f);   // 浅色底上同样的 alpha 会显脏
            using var sb = new SolidBrush(Color.FromArgb(alpha, shade));
            g.FillPath(sb, sp);
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
