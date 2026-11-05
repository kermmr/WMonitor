using System.Drawing.Drawing2D;
using WMonitor.Interop;
using WMonitor.Metrics;

namespace WMonitor.UI;

/// <summary>
/// 主面板总览指标卡：标签 + 大数值 + 进度条 + 次要信息（草图顶部四张卡）。
/// 自绘圆角，规则同 <see cref="BentoCard"/>：先铺窗口底色再画圆角路径。
/// </summary>
public sealed class MetricCard : Control
{
    public Theme Th = Theme.Dark;
    public float UiScale = 1f;
    public Color Accent = Color.Empty;
    public string LabelText = "";
    public string ValueText = "";
    public string SubText = "";
    public float Percent;

    private float _fontScale;
    private Font _fLabel = null!, _fValue = null!, _fSub = null!;

    public MetricCard()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        DoubleBuffered = true;
    }

    public void Set(string value, string sub, float percent)
    {
        ValueText = value;
        SubText = sub;
        Percent = percent;
        Invalidate();
    }

    private void EnsureFonts(float s)
    {
        if (_fontScale == s && _fLabel != null) return;
        _fontScale = s;
        _fLabel?.Dispose(); _fValue?.Dispose(); _fSub?.Dispose();
        _fLabel = new Font("Segoe UI", 14f * s, FontStyle.Regular, GraphicsUnit.Pixel);   // A3：卡片标题 Title 14（原 12.5 废弃）
        _fValue = new Font("Segoe UI", 30f * s, FontStyle.Bold, GraphicsUnit.Pixel);      // Display 30
        _fSub = new Font("Segoe UI", 12f * s, FontStyle.Regular, GraphicsUnit.Pixel);     // Body 12（原 11 废弃）
    }

    protected override void OnPaintBackground(PaintEventArgs e) =>
        e.Graphics.Clear(Parent?.BackColor ?? Th.WindowBg);

    protected override void OnPaint(PaintEventArgs e)
    {
        EnsureFonts(UiScale);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

        float s = UiScale;
        var full = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using (var path = Gfx.RoundRect(full, 12 * s))   // C6：卡片级圆角统一 R12（原 14 废弃）
        {
            using var bg = new SolidBrush(Th.CardBg);
            g.FillPath(bg, path);
            using var pen = new Pen(Th.CardBorder, 1f);
            g.DrawPath(pen, path);
        }

        float pad = 16 * s;
        float y = 13 * s;

        if (Accent != Color.Empty)
        {
            using var dot = new SolidBrush(Accent);
            g.FillEllipse(dot, pad, y + 3 * s, 6 * s, 6 * s);
        }
        using (var tb = new SolidBrush(Th.Label))
            g.DrawString(LabelText, _fLabel, tb, pad + 12 * s, y, StringFormat.GenericTypographic);

        y = 34 * s;
        using (var vb = new SolidBrush(Th.Text))
            g.DrawString(ValueText, _fValue, vb, pad, y, StringFormat.GenericTypographic);

        // 进度条
        float barY = y + 38 * s;
        var track = new RectangleF(pad, barY, Width - pad * 2, 6 * s);
        using (var tp = Gfx.RoundRect(track, 3 * s))
        using (var tb = new SolidBrush(Th.Track))
            g.FillPath(tb, tp);
        float fillW = Math.Clamp(Percent, 0, 100) / 100f * track.Width;
        if (fillW > 1 && Accent != Color.Empty)
        {
            using var fp = Gfx.RoundRect(new RectangleF(track.X, track.Y, fillW, track.Height), 3 * s);
            using var fb = new SolidBrush(Accent);
            g.FillPath(fb, fp);
        }

        using (var sb = new SolidBrush(Th.Label))
            g.DrawString(SubText, _fSub, sb, pad, barY + 12 * s, StringFormat.GenericTypographic);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fLabel?.Dispose();
            _fValue?.Dispose();
            _fSub?.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// 搜索框：自绘胶囊底 + 内嵌无边框 TextBox。系统 TextBox 不在 AGENTS.md §7 禁用列表里
/// （禁的是 Button / NumericUpDown），输入法、选区、剪贴板这些自己画不划算。
/// 占位提示走 EM_SETCUEBANNER（系统画在 TextBox 内部）：以前在父控件 OnPaint 里画、
/// 会被不透明的 TextBox 底色整个盖住（只露 2px 边，v0.18 起的隐藏 bug，阶段 3 截图验收发现）。
/// </summary>
public sealed class SearchBox : Control
{
    public Theme Th = Theme.Dark;
    public string Placeholder = "搜索应用 / 进程 / PID / 路径";

    /// <summary>TextChanged 已被 Control 占用，用这个名字。</summary>
    public event Action<string>? SearchChanged;

    private readonly TextBox _box = new();
    private bool _focused;

    /// <summary>C10：输入文字 A3 Body 12px（原 10pt≈13.3px 真变值）；随 UiScale 重建。</summary>
    public float UiScale
    {
        get => _uiScale;
        set
        {
            _uiScale = value;
            _box.Font = Gfx.F("Segoe UI", 12f * value, FontStyle.Regular);
        }
    }
    private float _uiScale = 1f;

    public SearchBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        DoubleBuffered = true;
        Cursor = Cursors.IBeam;

        _box.BorderStyle = BorderStyle.None;
        _box.Font = Gfx.F("Segoe UI", 12f, FontStyle.Regular);
        _box.TextChanged += (_, _) => SearchChanged?.Invoke(_box.Text);
        _box.GotFocus += (_, _) => { _focused = true; Invalidate(); };
        _box.LostFocus += (_, _) => { _focused = false; Invalidate(); };
        _box.HandleCreated += (_, _) => SendCue();
        Controls.Add(_box);

        Click += (_, _) => _box.Focus();
        Height = 32;      // A6（C11）：交互控件统一视觉高 32（原 34 废弃）
        Width = 300;
    }

    public void ApplyTheme(Theme th)
    {
        Th = th;
        _box.BackColor = th.CardBg;
        _box.ForeColor = th.Text;
        SendCue();
        Invalidate();
    }

    /// <summary>wParam=TRUE：有焦点也显示提示（输入文字后系统自动消失）。</summary>
    private void SendCue()
    {
        if (_box.IsHandleCreated && Placeholder.Length > 0)
            Native.SendMessage(_box.Handle, Native.EM_SETCUEBANNER, (IntPtr)1, Placeholder);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutBox();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        LayoutBox();
    }

    private void LayoutBox()
    {
        int x = (int)(32 * UiScale);
        _box.SetBounds(x, Math.Max(2, (Height - _box.Height) / 2 + 1), Math.Max(20, Width - x - (int)(14 * UiScale)), _box.Height);
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
        using (var p = Gfx.RoundRect(full, Height / 2f))
        {
            using var bg = new SolidBrush(Th.CardBg);
            g.FillPath(bg, p);
            using var pen = new Pen(_focused ? Color.FromArgb(140, Th.Accent) : Th.CardBorder, 1f);
            g.DrawPath(pen, p);
        }

        // 放大镜
        float cx = 15 * s, cy = Height / 2f;
        using (var pen = new Pen(Th.Label, 1.4f * s))
        {
            g.DrawEllipse(pen, cx - 5 * s, cy - 5 * s, 7 * s, 7 * s);
            g.DrawLine(pen, cx, cy + 2.4f * s, cx + 4.2f * s, cy + 6.6f * s);
        }
    }
}

/// <summary>字节格式化：主面板各处共用。</summary>
internal static class Fmt
{
    public static string Bytes(long b)
    {
        double abs = Math.Abs(b);
        if (abs >= 1L << 30) return (b / 1024.0 / 1024 / 1024).ToString("0.0") + " GB";
        if (abs >= 1L << 20) return (b / 1024.0 / 1024).ToString("0") + " MB";
        if (abs >= 1024) return (b / 1024.0).ToString("0") + " KB";
        return b + " B";
    }

    /// <summary>显示名兜底：空名字用「PID n」，受限进程有独立标识。</summary>
    public static string NameOr(DashboardTable.Row r) =>
        string.IsNullOrWhiteSpace(r.Name) ? "PID " + r.Pid : r.Name;
}

/// <summary>
/// 底部详情条：选中行详情 + 动作按钮行（plan §2.4 / §7 阶段 4）。
/// 按钮：打开位置（非管控，不确认）/ 优先级 / 挂起·恢复 / 结束任务。
/// 后三个是管控动作 —— 点击只弹二次确认对话框（ActionConfirmDialog），执行在 MainDashboardForm。
/// 系统进程的管控按钮禁用（安全规则 2）；受限/提权进程不禁用，操作失败走错误提示（安全规则 3）。
/// </summary>
public sealed class DetailBar : Control
{
    public Theme Th = Theme.Dark;
    public float UiScale = 1f;

    /// <summary>图标缓存（阶段 3）：按选中行 Path 显示大图标；null 不画。</summary>
    public IconCache? Icons;

    /// <summary>挂起状态（MainDashboardForm 按 TTL 缓存维护，只对选中行检测）。</summary>
    public bool Suspended;

    /// <summary>动作入口：后三个管控动作由表单接手做二次确认。</summary>
    public event Action<DashboardAction>? ActionRequested;

    private DashboardTable.Row? _row;

    private readonly BentoButton _btnOpen;
    private readonly BentoButton _btnPriority;
    private readonly BentoButton _btnSuspend;
    private readonly BentoButton _btnTerminate;

    public DetailBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        DoubleBuffered = true;

        _btnOpen = new BentoButton { Text = "打开位置" };
        _btnOpen.Click += (_, _) => ActionRequested?.Invoke(DashboardAction.OpenLocation);
        _btnPriority = new BentoButton { Text = "优先级" };
        _btnPriority.Click += (_, _) => ActionRequested?.Invoke(DashboardAction.Priority);
        _btnSuspend = new BentoButton { Text = "挂起" };
        _btnSuspend.Click += (_, _) => ActionRequested?.Invoke(Suspended ? DashboardAction.Resume : DashboardAction.Suspend);
        _btnTerminate = new BentoButton { Text = "结束任务", Primary = true };
        _btnTerminate.Click += (_, _) => ActionRequested?.Invoke(DashboardAction.Terminate);
        Controls.AddRange(new Control[] { _btnOpen, _btnPriority, _btnSuspend, _btnTerminate });
    }

    /// <summary>主题/缩放入口：按钮的 Theme 要在这里一起发（属性赋值不会通知子控件）。</summary>
    public void SetTheme(Theme th, float scale)
    {
        Th = th;
        BackColor = th.CardBg;       // BentoButton 用 Parent.BackColor 清底色，必须和卡片一致
        UiScale = scale;
        _btnOpen.Th = th;
        _btnOpen.FontScale = scale;
        _btnPriority.Th = th;
        _btnPriority.FontScale = scale;
        _btnSuspend.Th = th;
        _btnSuspend.FontScale = scale;
        _btnTerminate.Th = th;
        _btnTerminate.FontScale = scale;
        _btnTerminate.PrimaryColor = th.Alert;
        UpdateButtons();
        LayoutButtons();
    }

    public void Show(DashboardTable.Row? row)
    {
        _row = row;
        UpdateButtons();
        Invalidate();
    }

    private void UpdateButtons()
    {
        var r = _row;
        bool any = r != null;
        // 管控三钮：系统进程禁用（安全规则 2）；受限/提权进程不禁用，失败给中文原因（安全规则 3）
        bool controlAllowed = any && !r!.System;
        _btnOpen.Enabled = any && r!.Path != null;
        _btnPriority.Enabled = controlAllowed;
        _btnTerminate.Enabled = controlAllowed;
        // 挂起/恢复互斥：按钮永远显示「当前可执行的那个动作」
        _btnSuspend.Enabled = controlAllowed;
        _btnSuspend.Text = Suspended ? "恢复" : "挂起";
        LayoutButtons();     // C5 组内等宽：文字变了宽度规则跟着算（挂起/恢复同字数，通常无变化）
    }

    private int ActionAreaWidth()
    {
        // B3/C5：四钮组内等宽 = 组内最宽者（A7 公式），间距 8，右缩 16
        int group = BentoButton.GroupWidth(UiScale, "打开位置", "优先级", "挂起", "结束任务");
        return group * 4 + (int)(8 * UiScale) * 3 + (int)(16 * UiScale);
    }

    private void LayoutButtons()
    {
        float s = UiScale;
        int group = BentoButton.GroupWidth(s, _btnOpen.Text, _btnPriority.Text, _btnSuspend.Text, _btnTerminate.Text);
        int shadow = (int)Math.Round(6 * s);          // 投影预留：Height = 视觉高 32 + Shadow（A10）
        int h = (int)(32 * s) + shadow;
        int y = (Height - h) / 2;
        int x = Width - (int)(16 * s);                // B3 右缩 16（原 14 废弃）
        _btnTerminate.SetBounds(x - group, y, group, h); x -= group + (int)(8 * s);
        _btnSuspend.SetBounds(x - group, y, group, h); x -= group + (int)(8 * s);
        _btnPriority.SetBounds(x - group, y, group, h); x -= group + (int)(8 * s);
        _btnOpen.SetBounds(x - group, y, group, h);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutButtons();
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
        using (var path = Gfx.RoundRect(full, 12 * s))
        {
            using var bg = new SolidBrush(Th.CardBg);
            g.FillPath(bg, path);
            using var pen = new Pen(Th.CardBorder, 1f);
            g.DrawPath(pen, path);
        }

        // 右侧是动作按钮区（LayoutButtons），文字行全部给它们让宽
        float pad = 16 * s;
        float w = Width - pad * 2 - ActionAreaWidth();

        if (_row == null)
        {
            var fHint = Gfx.F("Segoe UI", 12f * s, FontStyle.Regular);   // A3 最小 12（原 11.5 废弃）
            using var hb = new SolidBrush(Th.Label);
            g.DrawString("选中列表中的一行查看详情", fHint, hb, pad, Height / 2f - fHint.Height / 2,
                StringFormat.GenericTypographic);
            return;
        }

        var r = _row;

        // ---- 行 1：图标 + 名称 + 进程数 / 状态 ----
        float textPad = pad;
        if (Icons != null && r.Path != null)
        {
            Image? appIcon = Icons.Get(r.Path);
            if (appIcon != null)
            {
                const int d0 = 36;
                var dst = new RectangleF(pad, 8 * s, d0 * s, d0 * s);
                var prevInterp = g.InterpolationMode;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(appIcon, dst);
                g.InterpolationMode = prevInterp;
                textPad = dst.Right + 10 * s;
            }
        }
        var fName = Gfx.F("Segoe UI", 14f * s, FontStyle.Bold);
        string name = Fmt.NameOr(r);
        using (var tb = new SolidBrush(Th.Text))
            g.DrawString(name, fName, tb, textPad, 11 * s, StringFormat.GenericTypographic);
        float x = textPad + Math.Min(Gfx.Measure(g, name, fName).Width + 10 * s, w * 0.6f);

        var fMeta = Gfx.F("Segoe UI", 12f * s, FontStyle.Regular);   // A3：详情行 Body 12（原 10.5 废弃）
        string meta = r.IsApp
            ? $"{r.MemberCount} 个进程 · 主 PID {r.Pid}"
            : $"PID {r.Pid}" + (r.Architecture != null ? $" · {r.Architecture}" : "");
        if (r.Foreground) meta += " · 前台";
        else if (r.HasWindow) meta += " · 有窗口";
        if (r.System) meta += " · 系统进程";
        if (r.Limited) meta += " · 受限";
        if (Suspended) meta += " · 已挂起";
        using (var mb = new SolidBrush(Th.Label))
            g.DrawString(meta, fMeta, mb, x, 14 * s, StringFormat.GenericTypographic);

        // ---- 行 2：路径 + 公司/描述 ----
        var fSub = Gfx.F("Segoe UI", 12f * s, FontStyle.Regular);   // A3：路径/正文 Body 12（原 10 废弃）
        string line2 = r.Path ?? "路径不可读（受限进程）";
        string extra = JoinNonEmpty(" · ", r.Description, r.Company);
        if (extra.Length > 0) line2 += "  ·  " + extra;
        using (var sb = new SolidBrush(Th.Label))
            g.DrawString(Elide(g, line2, fSub, w - (textPad - pad)), fSub, sb, textPad, 36 * s, StringFormat.GenericTypographic);

        // ---- 行 3：指标 + GPU + 启动时间 +（按应用）成员 PID ----
        string line3 = $"CPU {r.Cpu:0.0}%  ·  内存 {Fmt.Bytes(r.WorkingSet)}（专用 {Fmt.Bytes(r.PrivateBytes)}）";
        if (r.Gpu >= 0.05 || r.GpuDedicated > 0)
            line3 += $"  ·  GPU {r.Gpu:0.0}%"
                     + (r.GpuEngine != null ? $"（{r.GpuEngine}）" : "")
                     + $"  ·  显存 {Fmt.Bytes(r.GpuDedicated)}"
                     + (r.GpuShared > 0 ? $"（共享 {Fmt.Bytes(r.GpuShared)}）" : "");
        if (r.StartedAt is { } st) line3 += $"  ·  启动 {st:MM-dd HH:mm}";
        if (r.WindowTitle is { Length: > 0 } t) line3 += $"  ·  “{Trim(t, 32)}”";
        if (r.IsApp && r.Members is { Count: > 1 } ms)
        {
            var pids = new List<string>(Math.Min(ms.Count, 8));
            for (int i = 0; i < ms.Count && i < 8; i++) pids.Add(ms[i].Pid.ToString());
            line3 += $"  ·  成员 {string.Join(", ", pids)}" + (ms.Count > 8 ? $" …共 {ms.Count}" : "");
        }
        using (var sb2 = new SolidBrush(Th.Text))
            g.DrawString(Elide(g, line3, fSub, w - (textPad - pad)), fSub, sb2, textPad, 58 * s, StringFormat.GenericTypographic);
    }

    private static string JoinNonEmpty(string sep, params string?[] xs)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var x in xs)
        {
            if (string.IsNullOrWhiteSpace(x)) continue;
            if (sb.Length > 0) sb.Append(sep);
            sb.Append(x);
        }
        return sb.ToString();
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>宽度不够时截中加省略号（Gfx.Measure + GenericTypographic）。</summary>
    private static string Elide(Graphics g, string s, Font f, float maxW)
    {
        if (Gfx.Measure(g, s, f).Width <= maxW) return s;
        int len = s.Length;
        while (len > 1 && Gfx.Measure(g, s[..len] + "…", f).Width > maxW) len--;
        return s[..len] + "…";
    }
}
