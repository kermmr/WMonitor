using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace WMonitor.UI;

/// <summary>菜单行类型。渲染、命中、交互都按这个分派。</summary>
public enum MenuRowKind
{
    /// <summary>顶部标题区：主标题 + 一行小字摘要（不可点）。</summary>
    Header,
    /// <summary>分组小标题（不可点）。</summary>
    Title,
    /// <summary>普通动作项，点了就关菜单。</summary>
    Item,
    /// <summary>开关项，右侧拨动开关，点完菜单留在原地。</summary>
    Toggle,
    /// <summary>单选项目，左侧打勾，点了关菜单。</summary>
    Radio,
    /// <summary>带子菜单，右侧 V 形箭头。</summary>
    Submenu,
    /// <summary>行内分段控件（贴片卡那种 Segmented 的菜单版）。</summary>
    Segment,
    /// <summary>分割线。</summary>
    Separator
}

/// <summary>一行菜单的模型。<see cref="FlyoutMenu"/> 每次打开时重建，勾选状态直接读配置，不需要同步逻辑。</summary>
public sealed class MenuRow
{
    public MenuRowKind Kind = MenuRowKind.Item;
    public string Text = "";
    public string Sub = "";
    public bool Checked;
    public bool Enabled = true;

    /// <summary>危险项（退出）：hover 用告警色。</summary>
    public bool Danger;

    public Action? Click;
    /// <summary>开关项的写回（先翻转 <see cref="Checked"/> 再调它）。</summary>
    public Action<bool>? Set;

    public List<MenuRow>? Children;

    // Segment 用
    public string[]? Options;
    public int Selected;
    public Action<int>? OnSelect;

    // 布局结果（位图坐标，含阴影偏移）
    internal RectangleF Rect;
    internal RectangleF[] OptionRects = Array.Empty<RectangleF>();

    public static MenuRow Sep() => new() { Kind = MenuRowKind.Separator };
    public static MenuRow Title(string text) => new() { Kind = MenuRowKind.Title, Text = text };
}

/// <summary>
/// 现代风格自绘弹出菜单（替掉系统 ContextMenuStrip）。
///
/// 为什么不用 ToolStripRenderer：系统菜单的圆角、阴影、勾选槽、hover 块全由 DWM/主题渲染，
/// 深浅色跟着「应用模式」走，和贴片/面板/设置窗口的配色对不上；而且子菜单的左右留白、
/// 图标槽都是 Win32 时代的样子。这里直接用 <see cref="LayeredWindow"/> 画 —— 真 alpha、
/// 圆角无锯齿、阴影自己做，和详情面板同一套视觉语言。
///
/// 交互约定：
/// - 主菜单 <c>NoActivate = false</c>，靠 <c>Deactivate</c> 关闭（点外面即收起）；
/// - 子菜单 <c>NoActivate = true</c>，不抢焦点，所以父菜单不会因为开了子菜单而失活；
///   鼠标消息照样送达 NOACTIVATE 窗口，点击有效。
/// </summary>
public sealed class FlyoutMenu : LayeredWindow
{
    public Theme Th = Theme.Dark;

    /// <summary>行模型工厂。开关项点完会重新调它，勾选状态才不会和配置分叉。</summary>
    public Func<List<MenuRow>>? Build;

    private const int W = 252;
    private const int PadY = 6;
    private const int Shadow = 14;
    private const int Radius = 10;
    private const int RowH = 34;

    private List<MenuRow> _rows = new();
    private Bitmap? _bmp;
    private int _panelH;
    private int _hover = -1;
    private Point _pos;

    private FlyoutMenu? _parent;
    private FlyoutMenu? _sub;
    private int _subRow = -1;

    public FlyoutMenu()
    {
        Name = "WMonitorMenu";
        NoActivate = false;
        KeyPreview = true;
        ShowInTaskbar = false;
    }

    private FlyoutMenu Root => _parent ?? this;

    // ---------------- 全局鼠标 / 键盘钩子 ----------------
    // 菜单用 WS_EX_NOACTIVATE（不抢焦点，右键托盘不该把用户当前窗口踢到后台），
    // 所以不能靠 Deactivate 关闭：改成低级钩子盯着全局点击，点在菜单外面就收起，Esc 也收起。

    private static FlyoutMenu? s_open;
    private static IntPtr s_mouseHook, s_keyHook;
    private static HookProc? s_mouseProc, s_keyProc;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr hMod, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? name);

    private const int WH_MOUSE_LL = 14, WH_KEYBOARD_LL = 13;
    private const int WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204, WM_MBUTTONDOWN = 0x0207;
    private const int WM_KEYDOWN = 0x0100, VK_ESCAPE = 0x1B;

    private static void InstallHooks()
    {
        if (s_mouseHook != IntPtr.Zero) return;
        s_mouseProc = MouseHook;
        s_keyProc = KeyHook;
        IntPtr mod = GetModuleHandle(null);
        s_mouseHook = SetWindowsHookEx(WH_MOUSE_LL, s_mouseProc, mod, 0);
        s_keyHook = SetWindowsHookEx(WH_KEYBOARD_LL, s_keyProc, mod, 0);
    }

    private static void UninstallHooks()
    {
        if (s_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(s_mouseHook); s_mouseHook = IntPtr.Zero; }
        if (s_keyHook != IntPtr.Zero) { UnhookWindowsHookEx(s_keyHook); s_keyHook = IntPtr.Zero; }
        s_mouseProc = null;
        s_keyProc = null;
    }

    private static IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && s_open != null)
        {
            int msg = wParam.ToInt32();
            if (msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN)
            {
                // MSLLHOOKSTRUCT 前 8 字节就是 POINT
                int x = Marshal.ReadInt32(lParam, 0);
                int y = Marshal.ReadInt32(lParam, 4);
                if (!s_open.HitTree(new Point(x, y))) s_open.PostClose();
            }
        }
        return CallNextHookEx(s_mouseHook, code, wParam, lParam);
    }

    private static IntPtr KeyHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && s_open != null && wParam.ToInt32() == WM_KEYDOWN &&
            Marshal.ReadInt32(lParam) == VK_ESCAPE)
            s_open.PostClose();
        return CallNextHookEx(s_keyHook, code, wParam, lParam);
    }

    /// <summary>钩子回调里不要直接 Hide（正处在别的窗口消息流里），投递回自己的消息队列再关。</summary>
    private void PostClose()
    {
        try { if (IsHandleCreated) BeginInvoke(new Action(() => Root.CloseAll())); }
        catch { }
    }

    /// <summary>屏幕坐标是否落在本菜单（含各级子菜单）范围内。</summary>
    private bool HitTree(Point screenPt)
    {
        if (Visible && WindowRect().Contains(screenPt)) return true;
        return _sub != null && _sub.HitTree(screenPt);
    }

    private Rectangle WindowRect()
    {
        int w = _bmp?.Width ?? W + Shadow * 2;
        int h = _bmp?.Height ?? _panelH + Shadow * 2;
        return new Rectangle(_pos, new Size(w, h));
    }

    // ---------------- 打开 / 关闭 ----------------

    /// <summary>在光标（托盘图标）处打开主菜单：向右上展开，桌面边缘自动翻转。</summary>
    public void ShowMain(Point cursor)
    {
        _rows = Build?.Invoke() ?? new List<MenuRow>();
        LayoutRows();

        var scr = Screen.FromPoint(cursor).WorkingArea;
        int bh = _panelH + Shadow * 2;

        int x = cursor.X - W - Shadow + 4;
        int y = cursor.Y - _panelH - Shadow - 6;          // 菜单底边贴着光标往上长
        if (y < scr.Top) y = cursor.Y + Shadow;           // 上方放不下 → 往下开
        x = Math.Clamp(x, scr.Left - Shadow + 4, scr.Right - W - Shadow - 4);
        y = Math.Clamp(y, scr.Top - Shadow + 4, scr.Bottom - bh + Shadow - 4);

        _pos = new Point(x, y);
        s_open = this;
        InstallHooks();
        Show();
        Render();
    }

    private void ShowAsSubmenu(List<MenuRow> rows, FlyoutMenu parent, RectangleF parentRowRect)
    {
        _parent = parent;
        NoActivate = true;                                // 不抢焦点，父菜单保持激活
        _rows = rows;
        LayoutRows();

        int bw = W + Shadow * 2, bh = _panelH + Shadow * 2;
        var scr = Screen.FromPoint(Cursor.Position).WorkingArea;

        Point pp = parent._pos;                            // 拷贝一份，避免直接访问字段成员（CS1690）
        int x = pp.X + W + 2;
        int y = pp.Y + (int)parentRowRect.Y - Shadow - PadY;
        if (x + bw > scr.Right) x = pp.X - bw + 2;         // 右边放不下 → 往左开
        x = Math.Clamp(x, scr.Left - Shadow + 4, scr.Right - bw + Shadow - 4);
        y = Math.Clamp(y, scr.Top - Shadow + 4, scr.Bottom - bh + Shadow - 4);

        _pos = new Point(x, y);
        Show();
        Render();
    }

    public void CloseAll()
    {
        CloseSubmenu();
        if (Visible) Hide();

        if (_parent != null) return;
        if (ReferenceEquals(s_open, this)) s_open = null;
        UninstallHooks();
    }

    private void CloseSubmenu()
    {
        if (_sub == null) return;
        var s = _sub;
        _sub = null;
        _subRow = -1;
        s.CloseAll();
    }

    /// <summary>诊断钩子（--menu + WMONITOR_MENU_SUB）：按行号把某个子菜单展开，供截图验证。</summary>
    public void DebugOpenSubmenu(int idx)
    {
        if (idx < 0 || idx >= _rows.Count) return;
        if (_rows[idx].Kind != MenuRowKind.Submenu) return;
        OpenSubmenu(idx);
    }

    private void OpenSubmenu(int idx)
    {
        var row = _rows[idx];
        if (row.Children == null || row.Children.Count == 0 || _subRow == idx) return;
        CloseSubmenu();

        var sub = new FlyoutMenu { Th = Th };
        _sub = sub;
        _subRow = idx;
        sub.ShowAsSubmenu(row.Children, this, row.Rect);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) CloseAll();
        base.OnKeyDown(e);
    }

    // ---------------- 布局 ----------------

    private static int HeightOf(MenuRow r) => r.Kind switch
    {
        MenuRowKind.Header => 54,
        MenuRowKind.Title => 26,
        MenuRowKind.Separator => 11,
        MenuRowKind.Segment => 40,
        _ => RowH
    };

    private void LayoutRows()
    {
        float y = Shadow + PadY;
        float left = Shadow + 6;
        float width = W - 12;

        foreach (var r in _rows)
        {
            r.Rect = new RectangleF(left, y, width, HeightOf(r));
            if (r.Kind == MenuRowKind.Segment) r.OptionRects = SegmentRects(r);
            y += r.Rect.Height;
        }
        _panelH = (int)y - Shadow + PadY;
    }

    private static RectangleF[] SegmentRects(MenuRow r)
    {
        const float optW = 48f, optH = 24f;
        int n = r.Options?.Length ?? 0;
        if (n == 0) return Array.Empty<RectangleF>();

        float total = n * optW;
        float x = r.Rect.Right - 8 - total;
        float y = r.Rect.Y + (r.Rect.Height - optH) / 2f;

        var arr = new RectangleF[n];
        for (int i = 0; i < n; i++) arr[i] = new RectangleF(x + i * optW, y, optW, optH);
        return arr;
    }

    // ---------------- 绘制 ----------------

    private void Render()
    {
        int bw = W + Shadow * 2, bh = _panelH + Shadow * 2;
        if (_bmp == null || _bmp.Width != bw || _bmp.Height != bh)
        {
            _bmp?.Dispose();
            _bmp = new Bitmap(bw, bh, PixelFormat.Format32bppArgb);
        }

        using var g = Graphics.FromImage(_bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        g.Clear(Color.Transparent);

        DrawShadow(g);
        DrawPanel(g);

        bool hasRadio = _rows.Exists(r => r.Kind == MenuRowKind.Radio);
        var fText = Gfx.F("Segoe UI", 13f, FontStyle.Regular);
        var fTitle = Gfx.F("Segoe UI", 11f, FontStyle.Bold);
        var fHead = Gfx.F("Segoe UI", 13.5f, FontStyle.Bold);
        var fSub = Gfx.F("Segoe UI", 10.5f, FontStyle.Regular);

        for (int i = 0; i < _rows.Count; i++)
        {
            var r = _rows[i];
            bool hot = i == _hover && r.Enabled && r.Kind != MenuRowKind.Separator &&
                       r.Kind != MenuRowKind.Title && r.Kind != MenuRowKind.Header;

            if (hot)
            {
                var hr = new RectangleF(r.Rect.X, r.Rect.Y + 1, r.Rect.Width, r.Rect.Height - 2);
                using var hp = Gfx.RoundRect(hr, 6f);
                using var hb = new SolidBrush(r.Danger ? Color.FromArgb(38, Th.Alert) : Th.Hover);
                g.FillPath(hb, hp);
            }

            switch (r.Kind)
            {
                case MenuRowKind.Separator:
                    using (var pen = new Pen(Th.Sep, 1f))
                        g.DrawLine(pen, r.Rect.X + 4, r.Rect.Y + r.Rect.Height / 2f,
                            r.Rect.Right - 4, r.Rect.Y + r.Rect.Height / 2f);
                    break;

                case MenuRowKind.Title:
                    DrawText(g, r.Text, fTitle, Th.Label,
                        new RectangleF(r.Rect.X + 8, r.Rect.Y, r.Rect.Width - 8, r.Rect.Height));
                    break;

                case MenuRowKind.Header:
                    DrawHeader(g, r, fHead, fSub);
                    break;

                case MenuRowKind.Segment:
                    Color tc = r.Enabled ? Th.Text : Color.FromArgb(110, Th.Label);
                    DrawText(g, r.Text, fText, tc,
                        new RectangleF(r.Rect.X + 12, r.Rect.Y, r.Rect.Width - 12, r.Rect.Height));
                    DrawSegment(g, r, fSub);
                    break;

                case MenuRowKind.Toggle:
                    DrawText(g, r.Text, fText, r.Enabled ? Th.Text : Color.FromArgb(110, Th.Label),
                        new RectangleF(r.Rect.X + 12, r.Rect.Y, r.Rect.Width - 12 - 46, r.Rect.Height));
                    DrawSwitch(g, r);
                    break;

                case MenuRowKind.Submenu:
                    DrawText(g, r.Text, fText, r.Enabled ? Th.Text : Color.FromArgb(110, Th.Label),
                        new RectangleF(r.Rect.X + 12, r.Rect.Y, r.Rect.Width - 12 - 22, r.Rect.Height));
                    DrawChevron(g, r.Rect);
                    break;

                default:
                    float indent = hasRadio ? 24f : 12f;
                    if (hasRadio && r.Checked) DrawCheck(g, new RectangleF(r.Rect.X + 8, r.Rect.Y, 14, r.Rect.Height));
                    Color c = !r.Enabled ? Color.FromArgb(110, Th.Label)
                            : r.Danger ? Th.Alert
                            : Th.Text;
                    DrawText(g, r.Text, fText, c,
                        new RectangleF(r.Rect.X + indent, r.Rect.Y, r.Rect.Width - indent - 8, r.Rect.Height));
                    break;
            }
        }

        Blit(_bmp, _pos.X, _pos.Y);
    }

    private void DrawHeader(Graphics g, MenuRow r, Font fHead, Font fSub)
    {
        float x = r.Rect.X + 12;
        DrawText(g, r.Text, fHead, Th.Text, new RectangleF(x, r.Rect.Y + 6, r.Rect.Width - 24, 22));

        string ver = "v" + (typeof(FlyoutMenu).Assembly.GetName().Version?.ToString(2) ?? "");
        using (var vb = new SolidBrush(Th.Label))
        using (var sf = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center })
            g.DrawString(ver, fSub, vb, new RectangleF(r.Rect.X, r.Rect.Y + 6, r.Rect.Width - 12, 22), sf);

        if (!string.IsNullOrEmpty(r.Sub))
            DrawText(g, r.Sub, fSub, Th.Label, new RectangleF(x, r.Rect.Y + 28, r.Rect.Width - 24, 18));
    }

    private void DrawSegment(Graphics g, MenuRow r, Font f)
    {
        if (r.OptionRects.Length == 0 || r.Options == null) return;

        var track = new RectangleF(r.OptionRects[0].X - 2, r.OptionRects[0].Y - 2,
            r.OptionRects[^1].Right - r.OptionRects[0].X + 4, r.OptionRects[0].Height + 4);
        using (var tp = Gfx.RoundRect(track, 7f))
        using (var tb = new SolidBrush(Th.IsDark ? Color.FromArgb(48, 255, 255, 255) : Color.FromArgb(26, 0, 0, 0)))
            g.FillPath(tb, tp);

        for (int i = 0; i < r.OptionRects.Length && i < r.Options.Length; i++)
        {
            var opt = r.OptionRects[i];
            if (i == r.Selected)
            {
                using var sp = Gfx.RoundRect(opt, 5f);
                using var sb = new SolidBrush(Th.IsDark ? Th.Soft : Color.White);
                g.FillPath(sb, sp);
            }
            using var tb = new SolidBrush(i == r.Selected ? Th.Text : Th.Label);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(r.Options[i], f, tb, opt, sf);
        }
    }

    private void DrawSwitch(Graphics g, MenuRow r)
    {
        var sw = new RectangleF(r.Rect.Right - 10 - 32, r.Rect.Y + (r.Rect.Height - 18) / 2f, 32, 18);
        using (var tp = Gfx.RoundRect(sw, 9f))
        using (var tb = new SolidBrush(r.Checked
                   ? Th.Accent
                   : Th.IsDark ? Color.FromArgb(52, 255, 255, 255) : Color.FromArgb(34, 0, 0, 0)))
            g.FillPath(tb, tp);

        float kx = r.Checked ? sw.Right - 15 : sw.X + 3;
        using var kb = new SolidBrush(Color.White);
        g.FillEllipse(kb, kx, sw.Y + 3, 12, 12);
    }

    private void DrawChevron(Graphics g, RectangleF r)
    {
        float cy = r.Y + r.Height / 2f;
        using var pen = new Pen(Th.Label, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, new[]
        {
            new PointF(r.Right - 16, cy - 4),
            new PointF(r.Right - 12, cy),
            new PointF(r.Right - 16, cy + 4)
        });
    }

    private static void DrawCheck(Graphics g, RectangleF r)
    {
        float cy = r.Y + r.Height / 2f;
        using var pen = new Pen(Color.FromArgb(255, 0x0A, 0x84, 0xFF), 1.7f)
        { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, new[]
        {
            new PointF(r.X, cy),
            new PointF(r.X + 4, cy + 4),
            new PointF(r.X + 10, cy - 4.5f)
        });
    }

    private static void DrawText(Graphics g, string s, Font f, Color c, RectangleF r)
    {
        using var b = new SolidBrush(c);
        using var sf = new StringFormat
        {
            LineAlignment = StringAlignment.Center,
            Alignment = StringAlignment.Near,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };
        g.DrawString(s, f, b, r, sf);
    }

    private void DrawShadow(Graphics g)
    {
        // 由外向内叠：外圈最淡，内圈最深。只画在面板四周，面板自己会盖住中间。
        for (int i = Shadow; i >= 1; i--)
        {
            float k = i / (float)Shadow;
            var r = new RectangleF(Shadow - i + 0.5f, Shadow - i + 3f, W + i * 2 - 1f, _panelH + i * 2 - 1f);
            using var sp = Gfx.RoundRect(r, Radius + i * 0.7f);
            int a = (int)(30 * (1 - k) * (1 - k)) + 1;
            using var sb = new SolidBrush(Color.FromArgb(a, 0, 0, 0));
            g.FillPath(sb, sp);
        }
    }

    private void DrawPanel(Graphics g)
    {
        var panel = new RectangleF(Shadow + 0.5f, Shadow + 0.5f, W - 1f, _panelH - 1f);
        using var pp = Gfx.RoundRect(panel, Radius);
        using (var bg = new SolidBrush(Th.MenuBg)) g.FillPath(bg, pp);
        using (var pen = new Pen(Th.MenuBorder, 1f)) g.DrawPath(pen, pp);
    }

    // ---------------- 交互 ----------------

    private static bool Clickable(MenuRowKind k) => k is MenuRowKind.Item or MenuRowKind.Toggle
        or MenuRowKind.Radio or MenuRowKind.Submenu or MenuRowKind.Segment;

    private int RowAt(Point p)
    {
        for (int i = 0; i < _rows.Count; i++)
            if (_rows[i].Rect.Contains(p) && Clickable(_rows[i].Kind)) return i;
        return -1;
    }

    private static int OptionAt(MenuRow r, Point p)
    {
        for (int i = 0; i < r.OptionRects.Length; i++)
            if (r.OptionRects[i].Contains(p)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int idx = RowAt(e.Location);
        if (idx == _hover) return;

        _hover = idx;
        Render();

        if (idx >= 0 && _rows[idx].Kind == MenuRowKind.Submenu && _rows[idx].Enabled)
            OpenSubmenu(idx);
        else
            CloseSubmenu();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover >= 0) { _hover = -1; Render(); }

        // 指针既不在本菜单也不在子菜单上 → 收起子菜单（鼠标直接滑到桌面上的情况）
        var p = Cursor.Position;
        if (_sub != null && !_sub.HitTree(p) && !WindowRect().Contains(p)) CloseSubmenu();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;

        int idx = RowAt(e.Location);
        if (idx < 0) return;

        var r = _rows[idx];
        if (!r.Enabled) return;

        switch (r.Kind)
        {
            case MenuRowKind.Submenu:
                OpenSubmenu(idx);
                break;

            case MenuRowKind.Segment:
            {
                int o = OptionAt(r, e.Location);
                if (o < 0 || o == r.Selected) break;
                r.Selected = o;
                Root.CloseAll();
                r.OnSelect?.Invoke(o);
                break;
            }

            case MenuRowKind.Toggle:
                r.Checked = !r.Checked;
                r.Set?.Invoke(r.Checked);
                Rebuild();                 // 勾选状态重新从配置读一遍
                break;

            case MenuRowKind.Radio:
                Root.CloseAll();
                r.Click?.Invoke();
                break;

            case MenuRowKind.Item:
                Root.CloseAll();
                r.Click?.Invoke();
                break;
        }
    }

    private void Rebuild()
    {
        if (Build == null) { Render(); return; }

        _rows = Build();
        LayoutRows();

        // 行数变了高度就变；保持底边不动（菜单从托盘往上长，底边才是视觉锚点）
        int bh = _panelH + Shadow * 2;
        var scr = Screen.FromPoint(Cursor.Position).WorkingArea;
        int bottom = _pos.Y + _bmp!.Height;
        int y = bottom - bh;
        _pos = new Point(_pos.X, Math.Clamp(y, scr.Top - Shadow + 4, scr.Bottom - bh + Shadow - 4));

        Render();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (ReferenceEquals(s_open, this))
            {
                s_open = null;
                UninstallHooks();
            }
            _sub?.Dispose();
            _sub = null;
            _bmp?.Dispose();
            _bmp = null;
        }
        base.Dispose(disposing);
    }
}
