using System.Drawing.Drawing2D;
using WMonitor.Interop;

namespace WMonitor.UI;

/// <summary>
/// 手动更新对话框（v0.34）：发现新版本 → 弹窗展示「当前 → 新版」与更新说明 →
/// 按「立即更新」才真正下载替换（用户点要求：点击弹窗更新内容按确定更新）。
/// 模式复用 MessageDialog（无边框自绘 + Body 12 真实行距）+ ActionConfirmDialog 双钮布局。
/// 说明超长时截断到 MaxNoteLines 行，尾部提示完整版在 GitHub Releases 页。
/// </summary>
public sealed class UpdateDialog : Form
{
    private const int MaxNoteLines = 14;

    private readonly Theme _th;
    private readonly string _titleText;
    private readonly string _subText;
    private readonly List<string> _noteLines;
    private readonly bool _truncated;
    private readonly float _s;
    private readonly int _lineH;

    private readonly BentoButton _btnCancel;
    private readonly BentoButton _btnUpdate;

    /// <summary>用户按了「立即更新」为 true。</summary>
    public bool Confirmed { get; private set; }

    public UpdateDialog(Theme th, string currentVersion, string newVersion, long sizeBytes, string notes, float scale)
    {
        _th = th;
        _s = scale;
        _titleText = $"发现新版本 v{newVersion}";
        _subText = $"当前 v{currentVersion} → 新版 v{newVersion} · {sizeBytes / 1024 / 1024:0} MB";

        var fBody = Gfx.F("Segoe UI", 12f * scale, FontStyle.Regular);
        _lineH = (int)Math.Ceiling(fBody.GetHeight(Gfx.MeasureG));
        int noteWidth = (int)(440 * scale) - (int)(40 * scale);   // 与 ClientSize/内边距一致

        (_noteLines, _truncated) = WrapAndCap(CleanLines(notes), fBody, noteWidth, MaxNoteLines);

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        ShowIcon = false;
        KeyPreview = true;
        BackColor = th.WindowBg;
        DoubleBuffered = true;
        Text = _titleText;

        _btnCancel = new BentoButton { Text = "取消", DialogResult = DialogResult.Cancel };
        _btnCancel.Th = th;
        Controls.Add(_btnCancel);

        _btnUpdate = new BentoButton { Text = "立即更新", Primary = true };
        _btnUpdate.Th = th;
        _btnUpdate.Click += (_, _) => { Confirmed = true; DialogResult = DialogResult.OK; };
        Controls.Add(_btnUpdate);

        CancelButton = _btnCancel;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel;
            else if (e.KeyCode == Keys.Enter) { Confirmed = true; DialogResult = DialogResult.OK; }
        };

        ClientSize = ComputeSize();
        LayoutChildren();
    }

    /// <summary>清理 + 按控件宽换行 + 截断（截断时补一行提示，算进返回列表）。</summary>
    private (List<string> Lines, bool Truncated) WrapAndCap(List<string> src, Font font, int width, int cap)
    {
        var wrapped = new List<string>();
        foreach (var raw in src)
        {
            if (raw.Length == 0) { wrapped.Add(""); continue; }
            int start = 0;
            while (start < raw.Length)
            {
                int len = Math.Min(raw.Length - start, 120);
                // 用真实测量往回收紧到一行装得下（退化按字符数，防死循环）
                while (len > 1 &&
                       Gfx.MeasureG.MeasureString(raw.Substring(start, len), font).Width > width)
                    len = Math.Max(1, len * 3 / 4);
                if (len >= raw.Length - start) { wrapped.Add(raw[start..]); break; }
                wrapped.Add(raw.Substring(start, len));
                start += len;
            }
        }
        if (wrapped.Count <= cap) return (wrapped, false);
        var kept = wrapped.Take(cap).ToList();
        kept.Add("… 完整说明见 GitHub Releases 页");
        return (kept, true);
    }

    private List<string> CleanLines(string notes) =>
        // 防御性再清一次：Updater.Check 已清过，这里保证任何入口喂进来的 markdown 都渲染成纯文本
        Updater.CleanNotes(notes).Split('\n').Select(l => l.TrimEnd('\r')).ToList();

    private Size ComputeSize()
    {
        int h = (int)((30    // 标题
                       + 20  // 副题（当前 → 新版）
                       + 10) * _s);
        h += _noteLines.Count * _lineH + (int)(10 * _s);
        h += (int)(50 * _s);   // 按钮行（C17：38 视觉高 + 间距）
        h += (int)(16 * _s);   // 底边距
        return new Size((int)(440 * _s), h);
    }

    private void LayoutChildren()
    {
        float s = _s;
        int pad = (int)(20 * s);
        int group = Math.Max(BentoButton.GroupWidth(s, "取消", "立即更新"), (int)(120 * s));
        int bh = (int)(38 * s);
        int y = ClientSize.Height - (int)(14 * s) - bh;
        _btnUpdate.SetBounds(ClientSize.Width - pad - group, y, group, bh);
        _btnCancel.SetBounds(_btnUpdate.Left - (int)(8 * s) - group, y, group, bh);
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
        using (var tb = new SolidBrush(_th.Accent))
            g.DrawString(_titleText, Gfx.F("Segoe UI", 14f * s, FontStyle.Bold), tb, pad, 20 * s, StringFormat.GenericTypographic);
        using (var sb = new SolidBrush(_th.Label))
            g.DrawString(_subText, Gfx.F("Segoe UI", 12f * s, FontStyle.Regular), sb, pad, 44 * s, StringFormat.GenericTypographic);

        var f = Gfx.F("Segoe UI", 12f * s, FontStyle.Regular);
        using var bb = new SolidBrush(_th.Text);
        float y = 44 * s + 22 * s;
        foreach (string line in _noteLines)
        {
            if (line.Length > 0) g.DrawString(line, f, bb, pad, y, StringFormat.GenericTypographic);
            y += _lineH;
        }
    }
}
