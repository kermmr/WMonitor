using System.Runtime.InteropServices;
using WMonitor.Interop;

namespace WMonitor.UI;

/// <summary>
/// 全自绘无边框窗口基类（03 §B1，C1）。
///
/// 做法：保留 WS_OVERLAPPEDWINDOW 全套样式（Aero Snap、Win11 Snap Layouts、系统菜单、
/// 原生最大化/最小化动画都由系统提供），只做两件事 ——
/// ① WM_NCCALCSIZE 把非客户区抹掉（客户区 = 整个窗口矩形，最大化时按边框厚度内缩，
///    否则内容被系统撑出的屏幕外区域裁掉）；
/// ② WM_NCHITTEST 自报标题栏（HTCAPTION）与窗口钮（HTMINBUTTON/HTMAXBUTTON/HTCLOSE）。
/// 窗口钮由表单自绘（DrawChrome），hover/pressed 走 WM_NCMOUSEMOVE + SetCapture。
/// DWM ROUNDED 提供 Win11 系统圆角；最大化时系统自动变直角。
/// </summary>
public abstract class ChromeForm : Form
{
    private const int WM_NCCALCSIZE = 0x0083;
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_NCMOUSEMOVE = 0x00A0;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int WM_NCLBUTTONUP = 0x00A2;
    private const int WM_NCRBUTTONUP = 0x00A5;
    private const int WM_NCMOUSELEAVE = 0x02A2;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_CAPTURECHANGED = 0x0215;
    private const int WM_SYSCOMMAND = 0x0112;
    private const int WM_NULL = 0x0000;

    private const int HTCLIENT = 1;
    private const int HTCAPTION = 2;
    private const int HTMINBUTTON = 8;
    private const int HTMAXBUTTON = 9;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;
    private const int HTCLOSE = 20;

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    private const int SM_CXFRAME = 32;
    private const int SM_CXPADDEDBORDER = 92;
    private const uint TME_NONCLIENT = 0x0000_0010;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_NONOTIFY = 0x0080;

    /// <summary>关闭钮 hover 底色（Win11 惯例，深浅同值，03 §B1）。</summary>
    private static readonly Color CloseHover = Color.FromArgb(0xC4, 0x2B, 0x1C);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")]
    private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT tme);

    [DllImport("user32.dll")]
    private static extern IntPtr GetSystemMenu(IntPtr hwnd, bool revert);
    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y,
        int reserved, IntPtr hwnd, IntPtr prcRect);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT
    {
        public uint cbSize;
        public uint dwFlags;
        public IntPtr hwndTrack;
        public uint dwHoverTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RcRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NccalcsizeParams
    {
        public RcRect rgrc0, rgrc1, rgrc2;
        public IntPtr lppos;
    }

    private enum ChromeBtn { None, Min, Max, Close }

    private ChromeBtn _hot = ChromeBtn.None;
    private ChromeBtn _press = ChromeBtn.None;
    private bool _pressInside;
    private bool _trackedNc;
    private bool _iconTried;
    private Bitmap? _captionIcon;

    // ---------------- 探针（--dashboard-chrome-probe 只读断言用） ----------------
    internal string ProbeHot => _hot.ToString();
    internal string ProbePress => _press.ToString();
    internal bool ProbePressInside => _pressInside;
    /// <summary>hover 命中过按钮（瞬态：TME_NONCLIENT 的 LEAVE 随后立刻清除 _hot，外部轮询看不见）。</summary>
    internal bool ProbeHoverFired { get; private set; }
    internal Rectangle ProbeButtonRect(ChromeProbeBtn b) => b switch
    {
        ChromeProbeBtn.Min => ButtonRects().Min,
        ChromeProbeBtn.Max => ButtonRects().Max,
        _ => ButtonRects().Close
    };
    internal enum ChromeProbeBtn { Min, Max, Close }

    /// <summary>C20 探针开关：true 时右键标题栏只置旗标、不真弹 TrackPopupMenu（会阻塞消息循环）。</summary>
    internal bool ProbeInterceptSysMenu { get; set; }
    internal bool ProbeSysMenuFired { get; private set; }
    internal void ProbeResetSysMenu() => ProbeSysMenuFired = false;

    // ---------------- 子类可调 ----------------

    /// <summary>标题栏高度（96 DPI 基准 44，B1）。</summary>
    protected int CaptionHeight => S(44);

    /// <summary>chrome 缩放（主面板 = DeviceDpi/96 的 _s；设置窗固定布局用 DeviceDpi/96）。</summary>
    protected abstract float ChromeScale { get; }

    /// <summary>标题栏文字，默认 Form.Text。</summary>
    protected virtual string CaptionTitle => Text;

    protected virtual bool ShowMinButton => false;
    protected virtual bool ShowMaxButton => false;

    /// <summary>可缩放窗口才返回边缘 HT* 热区（FixedSingle 等固定边框不给缩放）。</summary>
    protected virtual bool ChromeResizable =>
        FormBorderStyle is FormBorderStyle.Sizable or FormBorderStyle.SizableToolWindow;

    private int S(float px) => (int)Math.Round(px * ChromeScale);

    // ---------------- 命中测试 ----------------

    private static Point GetPoint(IntPtr lParam) =>
        new((short)(long)lParam, (short)(((long)lParam) >> 16));

    private (Rectangle Min, Rectangle Max, Rectangle Close) ButtonRects()
    {
        int bw = S(46), bh = S(32);
        int y = (CaptionHeight - bh) / 2;
        var close = new Rectangle(ClientSize.Width - bw, y, bw, bh);
        var max = ShowMaxButton ? new Rectangle(close.X - bw, y, bw, bh) : Rectangle.Empty;
        var min = ShowMinButton ? new Rectangle((ShowMaxButton ? max.X : close.X) - bw, y, bw, bh) : Rectangle.Empty;
        return (min, max, close);
    }

    private ChromeBtn ButtonAt(Point p)
    {
        var (min, max, close) = ButtonRects();
        if (close.Contains(p)) return ChromeBtn.Close;
        if (ShowMaxButton && max.Contains(p)) return ChromeBtn.Max;
        if (ShowMinButton && min.Contains(p)) return ChromeBtn.Min;
        return ChromeBtn.None;
    }

    private int ChromeHitTest(Point screen)
    {
        var p = PointToClient(screen);
        int w = ClientSize.Width, h = ClientSize.Height;
        bool maximized = WindowState == FormWindowState.Maximized;

        if (!maximized && ChromeResizable)
        {
            // 系统边框厚度即可拖拽调宽的热区（样式保留，尺寸系统说了算）
            int b = Math.Max(6, GetSystemMetrics(SM_CXFRAME) + GetSystemMetrics(SM_CXPADDEDBORDER));
            if (p.X <= b && p.Y <= b) return HTTOPLEFT;
            if (p.X >= w - b && p.Y <= b) return HTTOPRIGHT;
            if (p.X <= b && p.Y >= h - b) return HTBOTTOMLEFT;
            if (p.X >= w - b && p.Y >= h - b) return HTBOTTOMRIGHT;
            if (p.X <= b) return HTLEFT;
            if (p.X >= w - b) return HTRIGHT;
            if (p.Y <= b) return HTTOP;
            if (p.Y >= h - b) return HTBOTTOM;
        }

        if (p.Y < CaptionHeight)
        {
            return ButtonAt(p) switch
            {
                ChromeBtn.Close => HTCLOSE,
                ChromeBtn.Max => HTMAXBUTTON,
                ChromeBtn.Min => HTMINBUTTON,
                _ => HTCAPTION
            };
        }
        return HTCLIENT;
    }

    // ---------------- 绘制 ----------------

    /// <summary>表单 OnPaint 顶部调用：画图标 + 标题 + 窗口钮（底色由表单自己的 WindowBg 提供，B1 无分隔线）。</summary>
    protected void DrawChrome(Graphics g, Theme th)
    {
        try { DrawChromeInner(g, th); }
        catch (Exception ex) { Dbg.Log("chrome paint fail: " + ex); throw; }
    }

    private void DrawChromeInner(Graphics g, Theme th)
    {
        float s = ChromeScale;
        int capH = CaptionHeight;

        if (!_iconTried)
        {
            // 标题栏图标只取一次（_iconTried 一次性闸门）：绝不能每帧重试 ——
            // portable 单文件 exe 上 new Icon(路径) 每次会读整个 bundle（66MB），
            // 失败被吞后下帧重来 = ~70MB/s 内存泄漏（2026-11-05 实测定位）。
            // 首选窗体 Icon（构造时 ExtractAssociatedIcon 已加载），兜底才读 exe。
            _iconTried = true;
            try { _captionIcon = Icon?.ToBitmap(); } catch (Exception ex) { Dbg.Log("chrome icon A fail: " + ex.Message); }
            if (_captionIcon == null)
            {
                try
                {
                    using var ico = new Icon(Application.ExecutablePath, 32, 32);
                    _captionIcon = ico.ToBitmap();
                }
                catch (Exception ex) { Dbg.Log("chrome icon B fail: " + ex.Message); }
            }
            Dbg.Log($"chrome icon loaded={_captionIcon != null} formIcon={(Icon != null ? Icon.Width + "x" + Icon.Height : "null")}");
        }
        int iconD = S(16);
        if (_captionIcon != null)
        {
            var prev = g.InterpolationMode;
            var prevOff = g.PixelOffsetMode;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.DrawImage(_captionIcon, new RectangleF(S(16), (capH - iconD) / 2f, iconD, iconD));
            g.InterpolationMode = prev;
            g.PixelOffsetMode = prevOff;
        }

        string title = CaptionTitle;
        var f = Gfx.F("Segoe UI", 14f * s, FontStyle.Bold);
        float th_ = Gfx.Measure(g, title, f).Height;
        using (var tb = new SolidBrush(th.Text))
            g.DrawString(title, f, tb, S(16) + iconD + S(10), (capH - th_) / 2f, StringFormat.GenericTypographic);

        var (min, max, close) = ButtonRects();
        DrawButton(g, th, min, ChromeBtn.Min);
        DrawButton(g, th, max, ChromeBtn.Max);
        DrawButton(g, th, close, ChromeBtn.Close);
    }

    private void DrawButton(Graphics g, Theme th, Rectangle r, ChromeBtn btn)
    {
        if (r.IsEmpty) return;
        bool hot = _hot == btn || (_press == btn && _pressInside);
        if (hot)
        {
            if (btn == ChromeBtn.Close)
            {
                // B1：关闭钮 hover #C42B1C + 白图标；Pressed 加深一档
                using var bg = new SolidBrush(_press == btn ? Mix(CloseHover, Color.Black, 0.25f) : CloseHover);
                g.FillRectangle(bg, r);
            }
            else
            {
                using var bg = new SolidBrush(th.Hover);
                g.FillRectangle(bg, r);
                if (_press == btn)          // pressed 加深一档：Hover 再叠一次
                    g.FillRectangle(bg, r);
            }
        }

        float s = ChromeScale;
        float d = 10f * s;                          // B1：图标 10px、线 1px
        float cx = r.Left + r.Width / 2f, cy = r.Top + r.Height / 2f;
        Color glyph = btn == ChromeBtn.Close && hot ? Color.White : th.Text;
        using var pen = new Pen(glyph, Math.Max(1f, s)) { StartCap = System.Drawing.Drawing2D.LineCap.Square, EndCap = System.Drawing.Drawing2D.LineCap.Square };

        switch (btn)
        {
            case ChromeBtn.Min:
                g.DrawLine(pen, cx - d / 2, cy + d / 4, cx + d / 2, cy + d / 4);
                break;
            case ChromeBtn.Max when WindowState == FormWindowState.Maximized:
            {
                // 还原：两个错位 8px 方框
                float o = 2f * s, w2 = d - o;
                g.DrawRectangle(pen, cx - d / 2 + o, cy - d / 2, w2, w2);
                g.DrawLine(pen, cx - d / 2, cy - d / 2 + o, cx + d / 2 - o, cy - d / 2 + o);
                g.DrawLine(pen, cx + d / 2 - o, cy - d / 2 + o, cx + d / 2 - o, cy + d / 2);
                g.DrawLine(pen, cx + d / 2 - o, cy + d / 2, cx - d / 2 + o, cy + d / 2);
                g.DrawLine(pen, cx - d / 2 + o, cy + d / 2, cx - d / 2 + o, cy - d / 2 + o);
                break;
            }
            case ChromeBtn.Max:
                g.DrawRectangle(pen, cx - d / 2, cy - d / 2, d, d);
                break;
            case ChromeBtn.Close:
                g.DrawLine(pen, cx - d / 2, cy - d / 2, cx + d / 2, cy + d / 2);
                g.DrawLine(pen, cx + d / 2, cy - d / 2, cx - d / 2, cy + d / 2);
                break;
        }
    }

    private static Color Mix(Color a, Color b, float t) => Color.FromArgb(
        a.A,
        (int)(a.R + (b.R - a.R) * t),
        (int)(a.G + (b.G - a.G) * t),
        (int)(a.B + (b.B - a.B) * t));

    private void InvalidateCaption()
    {
        Invalidate(new Rectangle(0, 0, ClientSize.Width, CaptionHeight + 1));
    }

    // ---------------- 消息 ----------------

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_NCCALCSIZE when (int)m.WParam == 1:
            {
                // 抹掉非客户区。最大化时窗口被系统撑出屏幕一个边框厚度，客户区内缩同量。
                if (WindowState == FormWindowState.Maximized)
                {
                    var ncp = Marshal.PtrToStructure<NccalcsizeParams>(m.LParam);
                    int frame = GetSystemMetrics(SM_CXFRAME) + GetSystemMetrics(SM_CXPADDEDBORDER);
                    ncp.rgrc0.Top += frame;
                    ncp.rgrc0.Bottom -= frame;
                    ncp.rgrc0.Left += frame;
                    ncp.rgrc0.Right -= frame;
                    Marshal.StructureToPtr(ncp, m.LParam, false);
                }
                m.Result = IntPtr.Zero;
                return;
            }

            case WM_NCHITTEST:
                m.Result = (IntPtr)ChromeHitTest(GetPoint(m.LParam));
                return;

            case WM_NCMOUSEMOVE when _press != ChromeBtn.None:
            {
                // 捕获失败时的兜底：NC 消息坐标是屏幕坐标（正常路径见 WM_MOUSEMOVE 分支）
                TrackPressInside(PointToClient(GetPoint(m.LParam)));
                return;
            }

            case WM_NCMOUSEMOVE:
            {
                var btn = ButtonAt(PointToClient(GetPoint(m.LParam)));
                if (btn != ChromeBtn.None) ProbeHoverFired = true;
                if (btn != _hot)
                {
                    _hot = btn;
                    InvalidateCaption();
                }
                if (!_trackedNc)
                {
                    var tme = new TRACKMOUSEEVENT
                    {
                        cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(),
                        dwFlags = TME_NONCLIENT,
                        hwndTrack = Handle
                    };
                    _trackedNc = TrackMouseEvent(ref tme);
                }
                break;
            }

            case WM_NCMOUSELEAVE:
                _trackedNc = false;
                if (_hot != ChromeBtn.None)
                {
                    _hot = ChromeBtn.None;
                    InvalidateCaption();
                }
                break;

            case WM_NCLBUTTONDOWN when (int)m.WParam is HTMINBUTTON or HTMAXBUTTON or HTCLOSE:
            {
                // 不交给 DefWindowProc（它会进模态按钮循环）：自己捕获鼠标跟踪 pressed。
                // 捕获建立后，后续移动/抬起以「客户区消息」回来（坐标已是客户区坐标，
                // 2026-11-05 修复：旧代码对它们再 PointToClient 一次 = 平移一个窗口原点，
                // 窗口不在屏幕原点时三个窗口钮永远命中不了）。
                _press = (int)m.WParam switch
                {
                    HTMINBUTTON => ChromeBtn.Min,
                    HTMAXBUTTON => ChromeBtn.Max,
                    _ => ChromeBtn.Close
                };
                _pressInside = true;
                InvalidateCaption();
                Native.SetCapture(Handle);
                return;
            }

            case WM_NCLBUTTONUP when _press != ChromeBtn.None:
                // 兜底：SetCapture 失败（窗口非前台等）时 UP 以 NC 消息（屏幕坐标）回来
                FinishPress(PointToClient(GetPoint(m.LParam)));
                return;

            case WM_MOUSEMOVE when _press != ChromeBtn.None:
                // 捕获期间光标滑出按钮：pressed 视觉取消，滑回来恢复
                TrackPressInside(GetPoint(m.LParam));
                return;

            case WM_LBUTTONUP when _press != ChromeBtn.None:
                FinishPress(GetPoint(m.LParam));
                return;

            case WM_NCRBUTTONUP when (int)m.WParam == HTCAPTION:
                // C20：无边框后 DefWindowProc 的默认菜单不可靠，自己在光标处弹系统菜单；
                // 必须在这里拦掉（不再交给 base），否则可能双菜单
                ShowCaptionMenu(GetPoint(m.LParam));
                m.Result = IntPtr.Zero;
                return;

            case WM_CAPTURECHANGED when _press != ChromeBtn.None:
                // 捕获被别人抢走（弹窗/切窗）：作废按下态，避免按钮卡在 pressed
                _press = ChromeBtn.None;
                _pressInside = false;
                InvalidateCaption();
                m.Result = IntPtr.Zero;
                return;
        }
        base.WndProc(ref m);
    }

    /// <summary>按下期间跟踪光标是否还在按钮上（p 必须已是客户区坐标）。</summary>
    private void TrackPressInside(Point p)
    {
        bool inside = ButtonAt(p) == _press;
        if (inside != _pressInside)
        {
            _pressInside = inside;
            InvalidateCaption();
        }
    }

    /// <summary>
    /// C20：标题栏右键弹系统菜单（屏幕坐标 = WM_NCRBUTTONUP 的 lParam）。
    /// 经典配方：先 SetForegroundWindow（否则菜单点外面收不起来），TPM_RETURNCMD 拿选中项，
    /// 再 PostMessage WM_SYSCOMMAND 让系统按标准路径执行 —— 最小化/最大化/还原/关闭/移动/大小
    /// 全部复用 Form 与 DefWindowProc 的现有处理（与拖拽、Snap 同一套）。
    /// </summary>
    private void ShowCaptionMenu(Point screen)
    {
        if (ProbeInterceptSysMenu) { ProbeSysMenuFired = true; return; }
        SetForegroundWindow(Handle);
        int cmd = TrackPopupMenu(GetSystemMenu(Handle, false),
            TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY,
            screen.X, screen.Y, 0, Handle, IntPtr.Zero);
        PostMessage(Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        if (cmd != 0) PostMessage(Handle, WM_SYSCOMMAND, (IntPtr)cmd, IntPtr.Zero);
    }

    /// <summary>抬起：p 为客户区坐标。命中且未滑出才执行动作。</summary>
    private void FinishPress(Point p)
    {
        var btn = _press;
        _press = ChromeBtn.None;
        Native.ReleaseCapture();    // 先释放，WM_CAPTURECHANGED 看到 _press==None 不会误清
        bool hit = _pressInside && ButtonAt(p) == btn;
        _pressInside = false;
        if (hit)
        {
            switch (btn)
            {
                case ChromeBtn.Min: WindowState = FormWindowState.Minimized; break;
                case ChromeBtn.Max:
                    WindowState = WindowState == FormWindowState.Maximized
                        ? FormWindowState.Normal : FormWindowState.Maximized;
                    break;
                case ChromeBtn.Close: Close(); break;
            }
        }
        InvalidateCaption();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!_appIconTried)
        {
            // Form.Icon 未设置时返回默认图标（不是 null）——「if (Icon == null)」永远不成立，
            // 任务栏和标题栏会一直显示 WinForms 默认双方块图标。这里统一取 exe 内嵌图标。
            // ExtractAssociatedIcon 在 portable 单文件 bundle 上可用（new Icon(路径) 不可用，见上）。
            _appIconTried = true;
            try { _appIcon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch (Exception ex) { Dbg.Log("app icon fail: " + ex.Message); }
        }
        if (_appIcon != null && !_appIconApplied)
        {
            _appIconApplied = true;
            Icon = _appIcon;
        }
        int pref = DWMWCP_ROUND;    // Win11 系统圆角（最大化时系统自动变直角，B1）
        DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
    }

    private static bool _appIconTried;
    private static Icon? _appIcon;
    private bool _appIconApplied;

    protected override void Dispose(bool disposing)
    {
        if (disposing) _captionIcon?.Dispose();
        base.Dispose(disposing);
    }
}
