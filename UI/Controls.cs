using System.Drawing.Drawing2D;

namespace WMonitor.UI;

/// <summary>分段选择器（3~4 个选项一行）。</summary>
public sealed class Segmented : Control
{
    public string[] Items = Array.Empty<string>();
    public int SelectedIndex;
    public Theme Th = Theme.Dark;
    public event EventHandler? SelectionChanged;

    private int _hover = -1;

    public Segmented()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 34;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        g.Clear(Parent?.BackColor ?? Th.PanelBg);

        var full = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using (var path = Gfx.RoundRect(full, 8f))
        using (var bg = new SolidBrush(Th.IsDark ? Color.FromArgb(255, 32, 36, 42) : Color.FromArgb(255, 236, 238, 242)))
            g.FillPath(bg, path);

        if (Items.Length == 0) return;
        float seg = Width / (float)Items.Length;
        var font = Gfx.F("Segoe UI", 11.5f, FontStyle.Regular);

        for (int i = 0; i < Items.Length; i++)
        {
            var r = new RectangleF(i * seg + 3, 3, seg - 6, Height - 6);
            bool sel = i == SelectedIndex;
            if (sel || i == _hover)
            {
                using var p = Gfx.RoundRect(r, 6f);
                using var b = new SolidBrush(sel
                    ? (Th.IsDark ? Color.FromArgb(255, 62, 68, 78) : Color.White)
                    : (Th.IsDark ? Color.FromArgb(90, 70, 76, 86) : Color.FromArgb(90, 220, 223, 228)));
                g.FillPath(b, p);
            }

            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using var tb = new SolidBrush(sel ? Th.Text : Th.Label);
            g.DrawString(Items[i], font, tb, r, sf);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int i = HitTest(e.X);
        if (i != _hover) { _hover = i; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = -1;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        int i = HitTest(e.X);
        if (i >= 0 && i != SelectedIndex)
        {
            SelectedIndex = i;
            Invalidate();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
        base.OnMouseDown(e);
    }

    private int HitTest(int x)
    {
        if (Items.Length == 0 || Width == 0) return -1;
        int i = (int)(x / (Width / (float)Items.Length));
        return Math.Clamp(i, 0, Items.Length - 1);
    }
}

/// <summary>可勾选的胶囊标签。</summary>
public sealed class Chip : Control
{
    public bool Checked;
    public Theme Th = Theme.Dark;
    public event EventHandler? CheckedChanged;

    public Chip(string text)
    {
        Text = text;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 30;
        Width = TextRenderer.MeasureText(text, Font).Width + 30;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        g.Clear(Parent?.BackColor ?? Th.PanelBg);

        var full = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using (var p = Gfx.RoundRect(full, Height / 2f))
        {
            var fill = Checked
                ? (Th.IsDark ? Color.FromArgb(46, 76, 194, 255) : Color.FromArgb(38, 10, 132, 255))
                : (Th.IsDark ? Color.FromArgb(255, 34, 38, 44) : Color.FromArgb(255, 240, 241, 244));
            using var b = new SolidBrush(fill);
            g.FillPath(b, p);
            using var pen = new Pen(Checked
                ? (Th.IsDark ? Color.FromArgb(150, 76, 194, 255) : Color.FromArgb(120, 10, 132, 255))
                : (Th.IsDark ? Color.FromArgb(38, 255, 255, 255) : Color.FromArgb(26, 0, 0, 0)), 1f);
            g.DrawPath(pen, p);
        }

        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var tb = new SolidBrush(Checked ? Th.Text : Th.Label);
        g.DrawString(Text, Gfx.F("Segoe UI", 11f, FontStyle.Regular), tb,
            new RectangleF(0, 0, Width, Height), sf);
    }

    protected override void OnMouseEnter(EventArgs e) { Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Checked = !Checked;
        Invalidate();
        CheckedChanged?.Invoke(this, EventArgs.Empty);
        base.OnMouseDown(e);
    }
}

/// <summary>0–100 的水平滑块（自绘，跟主题走）。</summary>
public sealed class Slider : Control
{
    private int _value = 100;
    private bool _drag;

    public Theme Th = Theme.Dark;
    public event EventHandler? ValueChanged;

    public Slider()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 28;
        Cursor = Cursors.Hand;
    }

    public int Value
    {
        get => _value;
        set
        {
            int v = Math.Clamp(value, 0, 100);
            if (v == _value) return;
            _value = v;
            Invalidate();
        }
    }

    private const float Pad = 8f;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Th.PanelBgOpaque);

        float x0 = Pad, x1 = Width - Pad, y = Height / 2f;
        if (x1 <= x0) return;

        using (var track = new Pen(Th.Track, 4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(track, x0, y, x1, y);

        float vx = x0 + (x1 - x0) * (_value / 100f);
        using (var fill = new Pen(Th.IsDark ? Color.FromArgb(255, 76, 194, 255) : Color.FromArgb(255, 10, 132, 255), 4f)
               { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(fill, x0, y, Math.Max(vx, x0 + 2f), y);

        using (var dot = new SolidBrush(Color.White))
        using (var edge = new Pen(Color.FromArgb(70, 0, 0, 0), 1f))
        {
            g.FillEllipse(dot, vx - 7, y - 7, 14, 14);
            g.DrawEllipse(edge, vx - 7, y - 7, 14, 14);
        }
    }

    private void SetFromX(int x)
    {
        float x0 = Pad, x1 = Width - Pad;
        if (x1 <= x0) return;
        int v = (int)Math.Round(Math.Clamp((x - x0) / (x1 - x0), 0f, 1f) * 100);
        if (v == _value) return;
        _value = v;
        Invalidate();
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        _drag = true;
        SetFromX(e.X);
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag) SetFromX(e.X);
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _drag = false;
        base.OnMouseUp(e);
    }
}
