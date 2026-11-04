using System.Drawing.Drawing2D;
using WMonitor.Interop;
using WMonitor.Metrics;

namespace WMonitor.UI;

/// <summary>
/// 管控动作二次确认对话框（plan §7 阶段 4 + 安全规则 4）：无边框自绘小窗。
/// 内容 = 动作标题 / 目标（名称·PID·路径）/ 影响范围 / 受限与关键进程提示 / 错误回显行 /
/// 优先级五档段控（仅优先级动作）/ 批量勾选（仅应用行结束任务，明确显示数量）/ 必勾确认框。
/// 确认按钮在勾选前禁用 ——「不要只用 OK」。
/// 失败不关对话框：ShowError 把中文原因显示在红字行，可重试或取消。
/// </summary>
public sealed class ActionConfirmDialog : Form
{
    private readonly Theme _th;
    private readonly DashboardAction _kind;
    private readonly string _name;
    private readonly int _pid;
    private readonly string? _path;
    private readonly bool _limited;
    private readonly int _memberCount;
    private readonly uint _currentPriority;
    private readonly float _s;

    private readonly Segmented _priority;
    private readonly ConfirmCheck _batchCheck;
    private readonly ConfirmCheck _confirmCheck;
    private readonly BentoButton _btnCancel;
    private readonly BentoButton _btnConfirm;

    private string _error = "";

    private static readonly (string Text, uint Cls)[] PriorityItems =
    {
        ("空闲", Native.IDLE_PRIORITY_CLASS),
        ("低于标准", Native.BELOW_NORMAL_PRIORITY_CLASS),
        ("标准", Native.NORMAL_PRIORITY_CLASS),
        ("高于标准", Native.ABOVE_NORMAL_PRIORITY_CLASS),
        ("高", Native.HIGH_PRIORITY_CLASS)
    };

    /// <summary>可操作但误伤代价大的外壳/体验组件 —— 对话框里特别提示（安全规则 5）。
    /// dwm/winlogon/svchost 等在系统名单里，入口直接禁用，到不了这里。</summary>
    private static readonly HashSet<string> CriticalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "sihost", "taskhostw", "textinputhost", "searchhost",
        "startmenuexperiencehost", "runtimebroker", "shellhost"
    };

    /// <summary>用户点「确认执行」后为 true。</summary>
    public bool Confirmed { get; private set; }

    /// <summary>结束任务是否作用于全部成员（默认 false = 只主 PID，plan §7 阶段 4.3）。</summary>
    public bool Batch { get; private set; }

    /// <summary>优先级动作选中的 class（PriorityItems 之一）。</summary>
    public uint PriorityClass { get; private set; }

    public ActionConfirmDialog(Theme th, DashboardAction kind, string name, int pid, string? path,
        bool limited, int memberCount, uint? currentPriority, float scale)
    {
        _th = th;
        _kind = kind;
        _name = name;
        _pid = pid;
        _path = path;
        _limited = limited;
        _memberCount = memberCount;
        _currentPriority = currentPriority ?? Native.NORMAL_PRIORITY_CLASS;
        _s = scale;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        ShowIcon = false;
        MinimizeBox = false;
        MaximizeBox = false;
        KeyPreview = true;
        BackColor = th.WindowBg;
        DoubleBuffered = true;
        Text = TitleText;

        bool priority = kind == DashboardAction.Priority;

        _priority = new Segmented
        {
            Items = PriorityItems.Select(p => p.Text).ToArray(),
            SelectedIndex = PriorityIndex(_currentPriority),
            Visible = priority
        };
        _priority.Th = th;
        _priority.FontScale = scale;
        _priority.SelectionChanged += (_, _) => PriorityClass = PriorityItems[Math.Clamp(_priority.SelectedIndex, 0, PriorityItems.Length - 1)].Cls;
        Controls.Add(_priority);
        PriorityClass = PriorityItems[Math.Clamp(_priority.SelectedIndex, 0, PriorityItems.Length - 1)].Cls;

        bool batchable = kind == DashboardAction.Terminate && memberCount > 1;
        _batchCheck = new ConfirmCheck(th, scale) { Text = $"同时结束全部 {memberCount} 个成员进程（默认只结束主 PID）", Visible = batchable };
        Controls.Add(_batchCheck);

        _confirmCheck = new ConfirmCheck(th, scale) { Text = ConfirmText };
        Controls.Add(_confirmCheck);

        _btnCancel = new BentoButton { Text = "取消", DialogResult = DialogResult.Cancel };
        _btnCancel.Th = th;
        Controls.Add(_btnCancel);

        _btnConfirm = new BentoButton
        {
            Text = kind == DashboardAction.Terminate ? "结束任务" : "确认执行",
            Enabled = false,
            Primary = true,
            PrimaryColor = kind == DashboardAction.Terminate ? th.Alert : th.Accent
        };
        _btnConfirm.Th = th;
        _btnConfirm.Click += (_, _) =>
        {
            if (!_confirmCheck.Checked) return;
            Confirmed = true;
            Batch = batchable && _batchCheck.Checked;
            DialogResult = DialogResult.OK;
        };
        Controls.Add(_btnConfirm);

        // 确认勾选 → 解锁确认按钮（按钮先建好再订阅，CS8602）
        _confirmCheck.CheckedChanged += (_, _) => _btnConfirm.Enabled = _confirmCheck.Checked;

        CancelButton = _btnCancel;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; }
            else if (e.KeyCode == Keys.Enter && _btnConfirm.Enabled) { _btnConfirm.PerformClick(); e.Handled = true; }
        };

        ClientSize = ComputeSize();
        LayoutChildren();
    }

    private string TitleText => _kind switch
    {
        DashboardAction.Terminate => "结束任务确认",
        DashboardAction.Suspend => "挂起进程确认",
        DashboardAction.Resume => "恢复进程确认",
        _ => "调整优先级确认"
    };

    private string Verb => _kind switch
    {
        DashboardAction.Terminate => "结束",
        DashboardAction.Suspend => "挂起",
        DashboardAction.Resume => "恢复",
        _ => "调整优先级："
    };

    private string ConfirmText => _kind switch
    {
        DashboardAction.Terminate => $"我确认结束 {_name}（PID {_pid}）",
        DashboardAction.Suspend => $"我确认挂起 {_name}（PID {_pid}），程序将冻结无响应",
        DashboardAction.Resume => $"我确认恢复 {_name}（PID {_pid}）",
        _ => $"我确认调整 {_name}（PID {_pid}）的优先级"
    };

    private bool IsCritical
    {
        get
        {
            if (_kind == DashboardAction.Resume) return false;
            string fileName = "";
            try { fileName = Path.GetFileName(_path ?? ""); } catch { }
            string candidate = fileName.Length > 0 ? fileName : _name;
            return CriticalNames.Contains(AppMetricsHub.NameWithoutExt(candidate));
        }
    }

    /// <summary>动作失败后回到对话框（不关闭），红字显示原因。</summary>
    public void ShowError(string message)
    {
        _error = message;
        Invalidate();
    }

    private Size ComputeSize()
    {
        int w = (int)(470 * _s);
        int h = (int)((30   // 标题
                       + 8 + 22        // 目标
                       + 20            // 路径
                       + 20            // 影响范围
                       + 18            // 错误行（常驻槽位，空则不画）
                       + 6) * _s);
        if (IsCritical) h += (int)(36 * _s);
        if (_limited) h += (int)(18 * _s);
        if (_priority.Visible) h += (int)(56 * _s);
        if (_batchCheck.Visible) h += (int)(30 * _s);
        h += (int)(30 * _s);            // 确认勾选
        h += (int)(52 * _s);            // 按钮行
        h += (int)(16 * _s);            // 底边距
        return new Size(w, h);
    }

    private void LayoutChildren()
    {
        float s = _s;
        int pad = (int)(20 * s);
        int w = ClientSize.Width - pad * 2;
        int y = ClientSize.Height - (int)(14 * s) - (int)(40 * s);
        _btnConfirm.SetBounds(ClientSize.Width - pad - (int)(120 * s), y, (int)(120 * s), (int)(40 * s));
        _btnCancel.SetBounds(_btnConfirm.Left - (int)(10 * s) - (int)(86 * s), y, (int)(86 * s), (int)(40 * s));

        y -= (int)(8 * s) + _confirmCheck.Height;
        _confirmCheck.SetBounds(pad, y, w, _confirmCheck.Height);

        if (_batchCheck.Visible)
        {
            y -= (int)(6 * s) + _batchCheck.Height;
            _batchCheck.SetBounds(pad, y, w, _batchCheck.Height);
        }
        if (_priority.Visible)
        {
            y -= (int)(10 * s) + (int)(34 * s);
            _priority.SetBounds(pad, y, w, (int)(34 * s));
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(_th.WindowBg);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

        float s = _s;
        var full = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using (var path = Gfx.RoundRect(full, 12 * s))
        {
            using var bg = new SolidBrush(_th.CardBg);
            g.FillPath(bg, path);
            using var pen = new Pen(_th.CardBorder, 1f);
            g.DrawPath(pen, path);
        }

        int pad = (int)(20 * s);
        int w = ClientSize.Width - pad * 2;
        float y = 22 * s;

        using (var tb = new SolidBrush(_kind == DashboardAction.Terminate ? _th.Alert : _th.Text))
            g.DrawString(TitleText, Gfx.F("Segoe UI", 13.5f * s, FontStyle.Bold), tb, pad, y, StringFormat.GenericTypographic);
        y += 28 * s;

        var fBody = Gfx.F("Segoe UI", 10.5f * s, FontStyle.Regular);
        var fLabel = Gfx.F("Segoe UI", 10f * s, FontStyle.Regular);

        using (var lb = new SolidBrush(_th.Text))
        {
            string target = _name;
            if (_memberCount > 1) target += $"（应用 · {_memberCount} 个进程 · 主 PID {_pid}）";
            else target += $"（PID {_pid}）";
            g.DrawString(Elide(g, "目标：" + target, fBody, w), fBody, lb, pad, y, StringFormat.GenericTypographic);
        }
        y += 22 * s;

        using (var lb = new SolidBrush(_th.Label))
        {
            string p = _path ?? "路径不可读（受限进程）";
            g.DrawString(Elide(g, "路径：" + p, fLabel, w), fLabel, lb, pad, y, StringFormat.GenericTypographic);
        }
        y += 20 * s;

        string scope = _kind switch
        {
            DashboardAction.Priority => "影响范围：仅调度优先级，不中断进程（当前：" + PriorityName(_currentPriority) + "）",
            DashboardAction.Suspend => "影响范围：该进程的所有线程被冻结（窗口可能显示「无响应」）",
            DashboardAction.Resume => "影响范围：该进程的所有线程恢复调度",
            _ => _memberCount > 1 ? "影响范围：默认仅主 PID；勾选下方后才作用于全部成员" : "影响范围：仅此进程"
        };
        using (var lb = new SolidBrush(_th.Label))
            g.DrawString(scope, fLabel, lb, pad, y, StringFormat.GenericTypographic);
        y += 20 * s;

        if (IsCritical)
        {
            using var rb = new SolidBrush(_th.Alert);
            g.DrawString("⚠ 这是 Windows 外壳 / 系统体验组件，误伤可能导致任务栏、开始菜单或资源管理器异常（注销后可恢复）。",
                fLabel, rb, new RectangleF(pad, y, w, 30 * s), StringFormat.GenericTypographic);
            y += 36 * s;
        }
        if (_limited)
        {
            using var lb2 = new SolidBrush(_th.Label);
            g.DrawString("该进程受访问限制（提权 / 保护），操作可能被拒绝 —— 失败会在此显示原因。", fLabel, lb2, pad, y,
                StringFormat.GenericTypographic);
            y += 18 * s;
        }

        if (_priority.Visible)
        {
            using var lb2 = new SolidBrush(_th.Label);
            g.DrawString("新优先级", fLabel, lb2, pad, y, StringFormat.GenericTypographic);
            y += 20 * s;
        }

        if (_error.Length > 0)
        {
            using var rb = new SolidBrush(_th.Alert);
            g.DrawString(Elide(g, "✕ " + _error, fBody, w), fBody, rb, pad, y, StringFormat.GenericTypographic);
        }
    }

    private static string PriorityName(uint cls) => cls switch
    {
        Native.IDLE_PRIORITY_CLASS => "空闲",
        Native.BELOW_NORMAL_PRIORITY_CLASS => "低于标准",
        Native.NORMAL_PRIORITY_CLASS => "标准",
        Native.ABOVE_NORMAL_PRIORITY_CLASS => "高于标准",
        Native.HIGH_PRIORITY_CLASS => "高",
        _ => "0x" + cls.ToString("X")
    };

    /// <summary>
    /// 当前优先级不在五档里时（REALTIME / HIGHEST），不要静默落到「空闲」——
    /// 那会让用户确认一次就意外把进程降成最低档。默认选「高」，用户仍可改选。
    /// </summary>
    private static int PriorityIndex(uint cls)
    {
        int i = Array.FindIndex(PriorityItems, p => p.Cls == cls);
        return i >= 0 ? i : Array.FindIndex(PriorityItems, p => p.Cls == Native.HIGH_PRIORITY_CLASS);
    }

    private static string Elide(Graphics g, string s, Font f, float maxW)
    {
        if (Gfx.Measure(g, s, f).Width <= maxW) return s;
        int len = s.Length;
        while (len > 1 && Gfx.Measure(g, s[..len] + "…", f).Width > maxW) len--;
        return s[..len] + "…";
    }

    /// <summary>probe 用：程序化勾选确认（与鼠标点击同一条 CheckedChanged 路径）。</summary>
    internal void ProbeSetConfirm() => _confirmCheck.SetChecked(true);

    /// <summary>probe 用：触发确认按钮（与鼠标点击同一条 OnClick 路径：Confirmed=true + DialogResult）。</summary>
    internal void ProbeConfirmClick() => _btnConfirm.PerformClick();
}

/// <summary>自绘复选框：方块 + 文本，点击整块切换。对话框确认 / 批量勾选用。</summary>
public sealed class ConfirmCheck : Control
{
    private readonly Theme _th;
    private readonly float _s;
    public event EventHandler? CheckedChanged;

    public ConfirmCheck(Theme th, float scale)
    {
        _th = th;
        _s = scale;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = th.CardBg;
        Cursor = Cursors.Hand;
        Height = (int)(24 * scale);
    }

    public bool Checked { get; private set; }

    public void SetChecked(bool v)
    {
        if (Checked == v) return;
        Checked = v;
        Invalidate();
        CheckedChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        g.Clear(Parent?.BackColor ?? _th.CardBg);

        float s = _s;
        float box = 15 * s;
        var r = new RectangleF(0, (Height - box) / 2f, box, box);
        using (var p = Gfx.RoundRect(r, 4 * s))
        {
            using var bg = new SolidBrush(Checked
                ? Color.FromArgb(255, _th.Accent.R, _th.Accent.G, _th.Accent.B)
                : (_th.IsDark ? Color.FromArgb(255, 32, 36, 42) : Color.FromArgb(255, 236, 238, 242)));
            g.FillPath(bg, p);
            using var pen = new Pen(Checked ? _th.Accent : (_th.IsDark ? Color.FromArgb(90, 255, 255, 255) : Color.FromArgb(70, 0, 0, 0)), 1f);
            g.DrawPath(pen, p);
        }
        if (Checked)
        {
            using var pen = new Pen(Color.White, 1.8f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            float cx = r.X + box / 2f, cy = r.Y + box / 2f;
            g.DrawLine(pen, cx - 3.4f * s, cy + 0.2f * s, cx - 1.0f * s, cy + 2.8f * s);
            g.DrawLine(pen, cx - 1.0f * s, cy + 2.8f * s, cx + 3.6f * s, cy - 2.6f * s);
        }

        using var tb = new SolidBrush(_th.Text);
        g.DrawString(Text, Gfx.F("Segoe UI", 10.5f * s, FontStyle.Regular), tb,
            box + 8 * s, Height / 2f - 8 * s, StringFormat.GenericTypographic);
    }

    protected override void OnClick(EventArgs e)
    {
        SetChecked(!Checked);
        base.OnClick(e);
    }
}

/// <summary>动作结果提示框（错误 / 批量结果）：标题 + 正文 + 确定，自绘同主题。</summary>
public sealed class MessageDialog : Form
{
    private readonly Theme _th;
    private readonly string _body;
    private readonly bool _error;
    private readonly float _s;

    public MessageDialog(Theme th, string title, string body, bool error, float scale)
    {
        _th = th;
        _body = body;
        _error = error;
        _s = scale;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        ShowIcon = false;
        KeyPreview = true;
        BackColor = th.WindowBg;
        DoubleBuffered = true;
        Text = title;

        var btn = new BentoButton { Text = "确定", DialogResult = DialogResult.OK, Primary = true };
        btn.Th = th;
        Controls.Add(btn);

        // 量正文高度：11px/行 + 余量
        int lines = body.Count(c => c == '\n') + 1;
        int w = (int)(440 * scale);
        int h = (int)((30 + 20 + lines * 17 + 52 + 16) * scale);
        ClientSize = new Size(w, h);
        btn.SetBounds(w - (int)(20 * scale) - (int)(110 * scale), h - (int)(14 * scale) - (int)(40 * scale),
            (int)(110 * scale), (int)(40 * scale));

        CancelButton = btn;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.Enter) DialogResult = DialogResult.OK;
        };
    }

    protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(_th.WindowBg);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

        float s = _s;
        var full = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using (var path = Gfx.RoundRect(full, 12 * s))
        {
            using var bg = new SolidBrush(_th.CardBg);
            g.FillPath(bg, path);
            using var pen = new Pen(_th.CardBorder, 1f);
            g.DrawPath(pen, path);
        }

        int pad = (int)(20 * s);
        using (var tb = new SolidBrush(_error ? _th.Alert : _th.Text))
            g.DrawString(Text, Gfx.F("Segoe UI", 13f * s, FontStyle.Bold), tb, pad, 20 * s, StringFormat.GenericTypographic);

        var f = Gfx.F("Segoe UI", 10.5f * s, FontStyle.Regular);
        using var bb = new SolidBrush(_th.Text);
        float y = 52 * s;
        foreach (string line in _body.Split('\n'))
        {
            g.DrawString(line.TrimEnd('\r'), f, bb, pad, y, StringFormat.GenericTypographic);
            y += 17 * s;
        }
    }
}
