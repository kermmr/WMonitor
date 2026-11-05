using System.Drawing.Drawing2D;

namespace WMonitor.UI;

/// <summary>分段选择器（3~4 个选项一行）。FontScale：高 DPI 容器里按 DeviceDpi/96 放大字号。</summary>
public sealed class Segmented : Control
{
    public string[] Items = Array.Empty<string>();
    public int SelectedIndex;
    public Theme Th = Theme.Dark;
    public float FontScale = 1f;
    public event EventHandler? SelectionChanged;

    private int _hover = -1;

    public Segmented()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 32;    // A6（C11）：统一视觉高 32（原 34 废弃）
        TabStop = true; // C18：可与 BentoButton 一同 Tab 导航
    }

    // 自绘控件必须自己刷底色：默认的 OnPaintBackground 用 BackColor（白），
    // 控件刚从隐藏变可见时会先白一帧 —— 切换四段条时那几行新出现的控件就是这么闪的。
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Th.PanelBg);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        g.Clear(Parent?.BackColor ?? Th.PanelBg);

        // A8（C2/C7）：容器 = SecondaryBg + 1px InputBorder，圆角 R8；选中块 SelectedBg；hover 块 Hover 叠加
        var full = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using (var path = Gfx.RoundRect(full, 8f))
        {
            using (var bg = new SolidBrush(Th.SecondaryBg)) g.FillPath(bg, path);
            using (var pen = new Pen(Th.InputBorder, 1f)) g.DrawPath(pen, path);
        }

        // C18/A7：键盘 Tab 焦点环 —— 内缩 2.5px 处 1px Text@47%（与 BentoButton 同规格）
        if (Focused && Enabled)
        {
            using var fp = Gfx.RoundRect(new RectangleF(2.5f, 2.5f, Width - 5f, Height - 5f), 5f);
            using var pen = new Pen(Color.FromArgb(120, Th.Text), 1f);
            g.DrawPath(pen, fp);
        }

        if (Items.Length == 0) return;
        float seg = Width / (float)Items.Length;
        var font = Gfx.F("Segoe UI", 12f * FontScale, FontStyle.Regular);

        for (int i = 0; i < Items.Length; i++)
        {
            var r = new RectangleF(i * seg + 3, 3, seg - 6, Height - 6);
            bool sel = i == SelectedIndex;
            if (sel || i == _hover)
            {
                using var p = Gfx.RoundRect(r, 6f);
                using var b = new SolidBrush(sel ? Th.SelectedBg : Th.Hover);
                g.FillPath(b, p);
            }

            var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            };
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

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // C18：方向键切换选项（单选段控惯例，立即生效并触发 SelectionChanged）
        int d = e.KeyCode switch
        {
            Keys.Left or Keys.Up => -1,
            Keys.Right or Keys.Down => 1,
            _ => 0
        };
        if (d != 0 && Items.Length > 0)
        {
            int i = Math.Clamp(SelectedIndex + d, 0, Items.Length - 1);
            if (i != SelectedIndex)
            {
                SelectedIndex = i;
                Invalidate();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
            e.Handled = true;
        }
        base.OnKeyDown(e);
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
    public float FontScale = 1f;
    public event EventHandler? CheckedChanged;

    private bool _hover;

    public Chip(string text)
    {
        Text = text;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 32;    // A6（C11）：统一视觉高 32（原 30 废弃）
        TabStop = true; // C18：可与 BentoButton 一同 Tab 导航
        // A8（C11 备注）：默认宽随 12px 字号新算（原用控件默认 Font ≈11px 会偏窄）
        Width = TextRenderer.MeasureText(text, Gfx.F("Segoe UI", 12f, FontStyle.Regular)).Width + 30;
    }

    // 自绘控件必须自己刷底色：默认的 OnPaintBackground 用 BackColor（白），
    // 控件刚从隐藏变可见时会先白一帧 —— 切换四段条时那几行新出现的控件就是这么闪的。
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Th.PanelBg);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        g.Clear(Parent?.BackColor ?? Th.PanelBg);

        // A8（C2/C4/C7）：未选 = SecondaryBg + InputBorder；选中 = Accent 淡底 + Accent 描边；
        // hover：未选叠 Hover，选中提亮到 Accent@24%
        var full = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using (var p = Gfx.RoundRect(full, Height / 2f))
        {
            var fill = Checked
                ? Color.FromArgb(_hover ? 61 : Th.IsDark ? 46 : 38, Th.Accent)
                : Th.SecondaryBg;
            using var b = new SolidBrush(fill);
            g.FillPath(b, p);
            if (!Checked && _hover)
                using (var hb = new SolidBrush(Th.Hover))
                    g.FillPath(hb, p);
            using var pen = new Pen(Checked
                ? Color.FromArgb(Th.IsDark ? 150 : 120, Th.Accent)
                : Th.InputBorder, 1f);
            g.DrawPath(pen, p);
        }

        // C18/A7：键盘 Tab 焦点环 —— 内缩 2.5px 处 1px Text@47%（与 BentoButton 同规格）
        if (Focused && Enabled)
        {
            using var fp = Gfx.RoundRect(new RectangleF(2.5f, 2.5f, Width - 5f, Height - 5f), (Height - 5f) / 2f);
            using var pen = new Pen(Color.FromArgb(120, Th.Text), 1f);
            g.DrawPath(pen, fp);
        }

        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var tb = new SolidBrush(Checked ? Th.Text : Th.Label);
        g.DrawString(Text, Gfx.F("Segoe UI", 12f * FontScale, FontStyle.Regular), tb,
            new RectangleF(0, 0, Width, Height), sf);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Checked = !Checked;
        Invalidate();
        CheckedChanged?.Invoke(this, EventArgs.Empty);
        base.OnMouseDown(e);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // C18：Space 切换开关（Enter 不拦 —— 对话框里 Enter 是确认键）
        if (e.KeyCode == Keys.Space)
        {
            Checked = !Checked;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
        base.OnKeyDown(e);
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

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Th.PanelBgOpaque);
    }

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

/// <summary>
/// 数值步进框（自绘，跟主题走）：− [值] +，按住可连续步进。
/// 系统 NumericUpDown 是原生控件，深色主题下底色/边框/箭头都和这套 UI 不搭，所以自己画。
/// </summary>
public sealed class Stepper : Control
{
    private int _value;
    private int _hot = -1;          // 0 = 减号区，1 = 加号区
    private bool _pressed;
    private readonly System.Windows.Forms.Timer _repeat;

    public int Minimum = -200;
    public int Maximum = 200;
    public Theme Th = Theme.Dark;
    public event EventHandler? ValueChanged;

    private const float BtnW = 26f;

    public Stepper()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 32;    // A6（C11）：统一视觉高 32（原 30 废弃）
        Width = 104;
        Cursor = Cursors.Hand;
        _repeat = new System.Windows.Forms.Timer { Interval = 380 };
        _repeat.Tick += (_, _) =>
        {
            if (!_pressed) { _repeat.Stop(); return; }
            _repeat.Interval = 55;
            Step(_hot == 0 ? -1 : 1);
        };
    }

    public int Value
    {
        get => _value;
        set
        {
            int v = Math.Clamp(value, Minimum, Maximum);
            if (v == _value) return;
            _value = v;
            Invalidate();
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Th.PanelBgOpaque);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        g.Clear(Parent?.BackColor ?? Th.PanelBgOpaque);

        var full = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using (var p = Gfx.RoundRect(full, 8f))
        {
            // A9（C2/C7）：容器 SecondaryBg + 1px InputBorder；± 区 hover 叠加 Hover
            using var bg = new SolidBrush(Th.SecondaryBg);
            g.FillPath(bg, p);
            using var pen = new Pen(Th.InputBorder, 1f);
            g.DrawPath(pen, p);
        }

        float cy = Height / 2f;
        if (_hot >= 0)
        {
            var r = new RectangleF(_hot == 0 ? 2.5f : Width - BtnW - 2.5f, 2.5f, BtnW, Height - 5f);
            using var hp = Gfx.RoundRect(r, 6f);
            using var hb = new SolidBrush(Th.Hover);
            g.FillPath(hb, hp);
        }

        using (var sep = new Pen(Th.Sep, 1f))
        {
            g.DrawLine(sep, BtnW + 3, 7, BtnW + 3, Height - 7);
            g.DrawLine(sep, Width - BtnW - 3, 7, Width - BtnW - 3, Height - 7);
        }

        using (var pen = new Pen(Th.Text, 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            float cxL = 2.5f + BtnW / 2f, cxR = Width - 2.5f - BtnW / 2f;
            g.DrawLine(pen, cxL - 4.5f, cy, cxL + 4.5f, cy);
            g.DrawLine(pen, cxR - 4.5f, cy, cxR + 4.5f, cy);
            g.DrawLine(pen, cxR, cy - 4.5f, cxR, cy + 4.5f);
        }

        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var tb = new SolidBrush(Th.Text);
        g.DrawString(_value.ToString(), Gfx.F("Segoe UI", 12f, FontStyle.Bold), tb,
            new RectangleF(BtnW, 0, Width - BtnW * 2, Height), sf);
    }

    private int Zone(int x) => x < BtnW + 2 ? 0 : x > Width - BtnW - 2 ? 1 : -1;

    private void Step(int delta)
    {
        int v = Math.Clamp(_value + delta, Minimum, Maximum);
        if (v == _value) { _repeat.Stop(); return; }
        _value = v;
        Invalidate();
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int z = Zone(e.X);
        if (z != _hot) { _hot = z; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hot = -1;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        int z = Zone(e.X);
        if (z >= 0)
        {
            _hot = z;
            _pressed = true;
            Step(z == 0 ? -1 : 1);
            _repeat.Interval = 380;
            _repeat.Stop();
            _repeat.Start();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _pressed = false;
        _repeat.Stop();
        base.OnMouseUp(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _repeat.Stop(); _repeat.Dispose(); }
        base.Dispose(disposing);
    }
}
