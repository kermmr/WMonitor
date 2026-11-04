using System.Diagnostics;
using System.Globalization;
using WMonitor.Metrics;

namespace WMonitor.UI;

/// <summary>
/// 资源管控主面板（plan §2）：普通 Form —— 可缩放、有任务栏项、正常焦点策略；
/// 不继承 LayeredWindow（plan §10.5），不复用 DetailPanel。
///
/// 数据流（plan §3）：自己的 WinForms Timer（DashboardRefreshMs，0 = 跟随全局）驱动
/// AppMetricsHub.Combine(_global())；全局快照引用 MonitorAppContext._last，不重复建全局 PDH。
/// 过滤 / 排序在这里完成，DashboardTable 只收排好序的行。
/// 生命周期（plan §4.3）：打开时创建 Hub、关闭时 Dispose，不常驻。
/// </summary>
public sealed class MainDashboardForm : Form
{
    private readonly AppConfig _cfg;
    private readonly Func<MetricsSnapshot> _global;
    private readonly AppMetricsHub _hub;
    private readonly System.Windows.Forms.Timer _timer;
    private Theme _th;
    private float _s = 1f;

    private readonly MetricCard _cardCpu, _cardRam, _cardGpu, _cardVram;
    private readonly Segmented _viewSwitch;
    private readonly Segmented _refreshSwitch;
    private readonly Segmented _minSwitch;
    private readonly Segmented _gpuFilter;
    private readonly Chip _bgChip;
    private readonly Chip _sysChip;
    private readonly BentoButton _btnSettings;
    private readonly SearchBox _search;
    private readonly DashboardTable _table;
    private readonly DetailBar _detail;
    private readonly IconCache _icons;

    private AppMetricsSnapshot _snap = new();
    private DashboardView _view;
    private string _searchText = "";
    private string _selKey = "";
    private RectangleF _statusRect;

    /// <summary>多卡下拉当前分段对应的 LUID 前缀（index 0 = 全部，存 ""）；与 _gpuFilter.Items 同序。</summary>
    private List<string> _luidBases = new();
    private string _luidItemsKey = "";

    /// <summary>选中行挂起状态缓存：pid → (是否挂起, TickCount64)。IsSuspended 单进程 1–3ms，
    /// 只查选中行 + 2s TTL，绝不放进每帧采样（docs/14 §3.4）。</summary>
    private readonly Dictionary<int, (bool Suspended, long At)> _suspendCache = new();

    /// <summary>（跟随, 0.5s, 1s, 2s）；0 表示用全局 RefreshMs。</summary>
    private static readonly (string Text, int Ms)[] RefreshItems = { ("跟随", 0), ("0.5s", 500), ("1s", 1000), ("2s", 2000) };

    /// <summary>最小占用阈值（plan §2.2）：CPU 与 GPU 都低于阈值才隐藏。</summary>
    private static readonly (string Text, double Val)[] MinItems = { ("阈值 关", 0), ("≥0.5", 0.5), ("≥1", 1), ("≥5", 5) };

    public event Action? SettingsRequested;

    public MainDashboardForm(AppConfig cfg, Theme th, Func<MetricsSnapshot> global)
    {
        _cfg = cfg;
        _th = th;
        _global = global;
        _hub = new AppMetricsHub(cfg.DashboardGpuRefreshMs);   // GPU 子采样间隔（默认 2s，plan §3）

        Text = "WMonitor 资源面板";
        Font = new Font("Segoe UI", 9f);
        AutoScaleMode = AutoScaleMode.None;
        MinimumSize = new Size(1040, 660);
        Size = new Size(1280, 800);
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = true;
        BackColor = th.WindowBg;
        DoubleBuffered = true;
        KeyPreview = true;
        try { if (Icon == null) Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        // ---- 总览卡 ----
        _cardCpu = new MetricCard { Accent = th.Cpu, LabelText = "CPU" };
        _cardRam = new MetricCard { Accent = th.Ram, LabelText = "内存" };
        _cardGpu = new MetricCard { Accent = th.Gpu, LabelText = "GPU" };
        _cardVram = new MetricCard { Accent = th.Vram, LabelText = "显存" };
        Controls.AddRange(new Control[] { _cardCpu, _cardRam, _cardGpu, _cardVram });

        // ---- 顶栏控制 ----
        int refreshIdx = Array.FindIndex(RefreshItems, r => r.Ms == cfg.DashboardRefreshMs);
        _refreshSwitch = new Segmented
        {
            Items = RefreshItems.Select(r => r.Text).ToArray(),
            SelectedIndex = refreshIdx >= 0 ? refreshIdx : 0
        };
        Controls.Add(_refreshSwitch);

        _bgChip = new Chip("显示后台")
        {
            Checked = cfg.DashboardShowBackground
        };
        Controls.Add(_bgChip);

        _sysChip = new Chip("系统进程")
        {
            Checked = cfg.DashboardShowSystemProcesses
        };
        Controls.Add(_sysChip);

        _btnSettings = new BentoButton { Text = "设置", Shadow = 0 };
        Controls.Add(_btnSettings);

        // ---- 工具栏 ----
        _viewSwitch = new Segmented
        {
            Items = new[] { "按应用", "按进程", "GPU" },
            SelectedIndex = Math.Clamp((int)cfg.DashboardDefaultView, 0, 2)
        };
        Controls.Add(_viewSwitch);

        _search = new SearchBox();
        _search.ApplyTheme(th);        // 内嵌 TextBox 的底色必须在首帧前套主题，否则白底
        Controls.Add(_search);

        // 阈值段控（阶段 3：DashboardMinimumPercent 生效）
        int minIdx = 0;
        for (int i = 0; i < MinItems.Length; i++)
            if (Math.Abs(MinItems[i].Val - cfg.DashboardMinimumPercent) < 0.01) { minIdx = i; break; }
        _minSwitch = new Segmented
        {
            Items = MinItems.Select(m => m.Text).ToArray(),
            SelectedIndex = minIdx
        };
        Controls.Add(_minSwitch);

        // 多卡筛选下拉（阶段 3）：单卡隐藏，Items 每 tick 按 GpuProcessSnapshot.Luids 重建
        _gpuFilter = new Segmented
        {
            Items = new[] { "全部显卡" },
            SelectedIndex = 0,
            Visible = false
        };
        Controls.Add(_gpuFilter);

        // ---- 列表 + 详情 ----
        _table = new DashboardTable
        {
            SortCol = MapCol(cfg.DashboardSort),
            SortDesc = cfg.DashboardSortDesc
        };
        Controls.Add(_table);

        _detail = new DetailBar();
        Controls.Add(_detail);

        _icons = new IconCache();
        _table.Icons = _icons;
        _detail.Icons = _icons;

        // 采样 timer 先建好，事件处理器里才能安全引用（不会在构造期间触发）
        _timer = new System.Windows.Forms.Timer { Interval = EffectiveInterval() };

        // ---- 事件 ----
        _refreshSwitch.SelectionChanged += (_, _) =>
        {
            _cfg.DashboardRefreshMs = RefreshItems[Math.Clamp(_refreshSwitch.SelectedIndex, 0, RefreshItems.Length - 1)].Ms;
            _cfg.Save();
            _timer.Interval = EffectiveInterval();
        };
        _bgChip.CheckedChanged += (_, _) =>
        {
            _cfg.DashboardShowBackground = _bgChip.Checked;
            _cfg.Save();
            RebuildRows();
        };
        _sysChip.CheckedChanged += (_, _) =>
        {
            _cfg.DashboardShowSystemProcesses = _sysChip.Checked;
            _cfg.Save();
            RebuildRows();
        };
        _minSwitch.SelectionChanged += (_, _) =>
        {
            _cfg.DashboardMinimumPercent = MinItems[Math.Clamp(_minSwitch.SelectedIndex, 0, MinItems.Length - 1)].Val;
            _cfg.Save();
            RebuildRows();
        };
        _gpuFilter.SelectionChanged += (_, _) =>
        {
            int idx = Math.Clamp(_gpuFilter.SelectedIndex, 0, _luidBases.Count - 1);
            _cfg.DashboardGpuLuid = idx <= 0 ? "" : _luidBases[idx];
            _cfg.Save();
            RebuildRows();
        };
        _btnSettings.Click += (_, _) => SettingsRequested?.Invoke();
        _viewSwitch.SelectionChanged += (_, _) =>
        {
            _view = (DashboardView)Math.Clamp(_viewSwitch.SelectedIndex, 0, 2);
            _cfg.DashboardDefaultView = _view;
            _cfg.Save();
            ApplyViewVisibility();
            RebuildRows();
        };
        _search.SearchChanged += text =>
        {
            _searchText = text.Trim();
            RebuildRows();
        };
        _table.SortClicked += OnSortClicked;
        _table.SelectionChanged += () =>
        {
            var row = _table.Selected >= 0 && _table.Selected < _table.Rows.Count
                ? _table.Rows[_table.Selected]
                : null;
            _selKey = row?.Key ?? "";
            // 挂起状态先查好再 Show（按钮 label 依赖它）
            _detail.Suspended = row != null && GetSuspended(row.Pid);
            _detail.Show(row);
        };
        _detail.ActionRequested += OnDetailAction;

        _view = (DashboardView)Math.Clamp((int)cfg.DashboardDefaultView, 0, 2);
        ApplyViewVisibility();

        RestoreWindowBounds();

        // 首次同步采样，避免空列表首帧（plan §3.3.8）
        try
        {
            _snap = _hub.Combine(_global());
            UpdateGpuFilter();
            UpdateCards();
            RebuildRows();
        }
        catch { }

        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        DpiChanged += (_, _) => OnScaleChanged();
    }

    private int EffectiveInterval()
    {
        int ms = _cfg.DashboardRefreshMs == 0 ? _cfg.RefreshMs : _cfg.DashboardRefreshMs;
        return Math.Clamp(ms, 250, 10000);
    }

    private static DashboardTable.Column MapCol(DashboardSort s) => s switch
    {
        DashboardSort.Name => DashboardTable.Column.Name,
        DashboardSort.Pid => DashboardTable.Column.Pid,
        DashboardSort.Memory => DashboardTable.Column.Memory,
        DashboardSort.Gpu => DashboardTable.Column.Gpu,
        DashboardSort.Vram => DashboardTable.Column.Vram,
        _ => DashboardTable.Column.Cpu
    };

    private static DashboardSort MapSort(DashboardTable.Column c) => c switch
    {
        DashboardTable.Column.Name => DashboardSort.Name,
        DashboardTable.Column.Pid => DashboardSort.Pid,
        DashboardTable.Column.Memory => DashboardSort.Memory,
        DashboardTable.Column.Gpu => DashboardSort.Gpu,
        DashboardTable.Column.Vram => DashboardSort.Vram,
        _ => DashboardSort.Cpu
    };

    // ---------------- 采样 tick ----------------

    private void Tick()
    {
        try { _snap = _hub.Combine(_global()); }
        catch { return; }
        var selRow = SelectedRow();
        if (selRow != null) _detail.Suspended = GetSuspended(selRow.Pid);   // TTL 缓存，0.5Hz 最多一次 ~2ms
        UpdateGpuFilter();
        UpdateCards();
        RebuildRows();
        Invalidate();     // 表头/状态文字（子控件自己 Invalidate）
    }

    private DashboardTable.Row? SelectedRow() =>
        _table.Selected >= 0 && _table.Selected < _table.Rows.Count ? _table.Rows[_table.Selected] : null;

    /// <summary>
    /// 多卡筛选下拉（阶段 3）：按 GpuProcessSnapshot.Luids 的 LUID 前缀去重建段控；
    /// 单卡（≤1 组）隐藏。Items 只在集合变化时重建，避免每帧失效重绘。
    /// </summary>
    private void UpdateGpuFilter()
    {
        var bases = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_snap.Gpu.Available)
            foreach (var l in _snap.Gpu.Luids)
                bases.Add(GpuProcessSampler.LuidBase(l.Luid));

        string key = string.Join("|", bases);
        if (key != _luidItemsKey)
        {
            _luidItemsKey = key;
            _luidBases = new List<string>(bases.Count + 1) { "" };
            var items = new List<string>(bases.Count + 1) { "全部显卡" };
            int i = 1;
            foreach (var b in bases)
            {
                _luidBases.Add(b);
                items.Add($"GPU{i}");
                i++;
            }
            _gpuFilter.Items = items.ToArray();

            int sel = _cfg.DashboardGpuLuid.Length == 0 ? 0 : _luidBases.IndexOf(_cfg.DashboardGpuLuid);
            if (sel < 0)
            {
                sel = 0;
                _cfg.DashboardGpuLuid = "";   // 记忆的卡不在了：回全部
                _cfg.Save();
            }
            _gpuFilter.SelectedIndex = sel;
            _gpuFilter.Invalidate();
        }
        _gpuFilter.Visible = bases.Count > 1;
    }

    private void UpdateCards()
    {
        var g = _snap.Global;
        _cardCpu.Set($"{g.CpuPercent:0}%",
            $"{Environment.ProcessorCount} 线程 · {g.CpuGhz:0.0} GHz", (float)g.CpuPercent);
        _cardRam.Set($"{g.RamPercent:0}%",
            $"{g.RamUsedGb:0.0} / {g.RamTotalGb:0.0} GB", (float)g.RamPercent);
        _cardGpu.Set(g.HasGpu ? $"{g.GpuPercent:0}%" : "—",
            g.GpuName
            + (g.HasTemp ? $" · {g.GpuTempC:0}°C" : "")
            + (!double.IsNaN(g.GpuPowerW) ? $" · {g.GpuPowerW:0}W" : ""),
            g.HasGpu ? (float)g.GpuPercent : 0);
        _cardVram.Set(g.HasVramTotal ? $"{g.VramPercent:0}%" : "—",
            g.HasVramTotal ? $"{g.VramGb:0.0} / {g.VramTotalGb:0.0} GB" : "—",
            g.HasVramTotal ? (float)g.VramPercent : 0);
    }

    // ---------------- 过滤 / 排序 / 行构建 ----------------

    private void RebuildRows()
    {
        // 多卡筛选（阶段 3）：选中 LUID 前缀时行内 GPU/显存改取该卡数值（多卡进程跨卡求和）。
        // LUID 比较一律 OrdinalIgnoreCase（docs/12 §5.4：引擎小写 / 显存大写两族）。
        string selLuid = _cfg.DashboardGpuLuid;
        Dictionary<int, (double U, long D)>? perLuid = null;
        if (selLuid.Length > 0 && _snap.Gpu.Available)
        {
            perLuid = new Dictionary<int, (double, long)>();
            foreach (var l in _snap.Gpu.Luids)
            {
                if (!GpuProcessSampler.LuidBase(l.Luid).Equals(selLuid, StringComparison.OrdinalIgnoreCase)) continue;
                var cur = perLuid.TryGetValue(l.Pid, out var c) ? c : (0.0, 0L);
                perLuid[l.Pid] = (Math.Clamp(cur.Item1 + l.UtilPercent, 0, 100), cur.Item2 + l.DedicatedBytes);
            }
        }

        var rows = new List<DashboardTable.Row>(_view == DashboardView.App ? _snap.Apps.Count : _snap.Processes.Count);

        if (_view == DashboardView.App)
        {
            foreach (var a in _snap.Apps)
            {
                double gpu = a.GpuPercent;
                long ded = a.GpuDedicatedBytes;
                if (perLuid != null)
                {
                    gpu = 0; ded = 0;
                    foreach (var m in a.Members)
                        if (perLuid.TryGetValue(m.Pid, out var v))
                        {
                            gpu = Math.Clamp(gpu + v.U, 0, 100);
                            ded += v.D;
                        }
                }
                var r = new DashboardTable.Row
                {
                    Key = "a:" + a.Key,
                    IsApp = true,
                    Name = a.Name,
                    Path = a.Path,
                    Description = a.Main.Description,
                    Company = a.Main.Company,
                    Pid = a.Main.Pid,
                    MemberCount = a.Members.Count,
                    Cpu = a.CpuPercent,
                    WorkingSet = a.WorkingSet64,
                    PrivateBytes = a.PrivateMemory64,
                    Gpu = gpu,
                    GpuDedicated = ded,
                    GpuShared = a.GpuSharedBytes,
                    GpuEngine = a.GpuTopEngine,
                    Foreground = a.IsForeground,
                    HasWindow = a.HasVisibleWindow,
                    Limited = a.AccessLimited,
                    System = a.IsSystem,
                    Architecture = a.Main.Architecture,
                    StartedAt = a.Main.StartedAt,
                    WindowTitle = a.Main.WindowTitle,
                    Members = a.Members
                };
                if (Pass(r)) rows.Add(r);
            }
        }
        else if (_view == DashboardView.Process)
        {
            foreach (var p in _snap.Processes)
            {
                double gpu = p.GpuPercent;
                long ded = p.GpuDedicatedBytes;
                if (perLuid != null)
                    (gpu, ded) = perLuid.TryGetValue(p.Pid, out var v) ? v : (0.0, 0L);
                var r = new DashboardTable.Row
                {
                    Key = "p:" + p.Pid,
                    Name = p.Name,
                    Path = p.Path,
                    Description = p.Description,
                    Company = p.Company,
                    Pid = p.Pid,
                    Cpu = p.CpuPercent,
                    WorkingSet = p.WorkingSet64,
                    PrivateBytes = p.PrivateMemory64,
                    Gpu = gpu,
                    GpuDedicated = ded,
                    GpuShared = p.GpuSharedBytes,
                    GpuEngine = p.GpuTopEngine,
                    Foreground = p.IsForeground,
                    HasWindow = p.HasVisibleWindow,
                    Limited = p.AccessLimited,
                    System = p.IsSystem,
                    Architecture = p.Architecture,
                    StartedAt = p.StartedAt,
                    WindowTitle = p.WindowTitle,
                    Members = new[] { p }
                };
                if (Pass(r)) rows.Add(r);
            }
        }
        else
        {
            // GPU 视图（plan §2.2）：按 PID 列 GPU 活动进程（util > 0 或专用显存 > 0）。
            // GPU 计算进程通常无窗口，忽略「显示后台」过滤；搜索与系统进程过滤仍然生效。
            foreach (var p in _snap.Processes)
            {
                double gpu = p.GpuPercent;
                long ded = p.GpuDedicatedBytes;
                if (perLuid != null)
                    (gpu, ded) = perLuid.TryGetValue(p.Pid, out var v) ? v : (0.0, 0L);
                if (gpu < 0.05 && ded <= 0) continue;
                var r = new DashboardTable.Row
                {
                    Key = "p:" + p.Pid,            // 与按进程视图同键，跨视图选中不丢
                    Name = p.Name,
                    Path = p.Path,
                    Description = p.Description,
                    Company = p.Company,
                    Pid = p.Pid,
                    Cpu = p.CpuPercent,
                    WorkingSet = p.WorkingSet64,
                    PrivateBytes = p.PrivateMemory64,
                    Gpu = gpu,
                    GpuDedicated = ded,
                    GpuShared = p.GpuSharedBytes,
                    GpuEngine = p.GpuTopEngine,
                    Foreground = p.IsForeground,
                    HasWindow = p.HasVisibleWindow,
                    Limited = p.AccessLimited,
                    System = p.IsSystem,
                    Architecture = p.Architecture,
                    StartedAt = p.StartedAt,
                    WindowTitle = p.WindowTitle,
                    Members = new[] { p }
                };
                if (Pass(r, gpuView: true)) rows.Add(r);
            }
        }

        rows.Sort(Compare);

        _table.SortCol = MapCol(_cfg.DashboardSort);
        _table.SortDesc = _cfg.DashboardSortDesc;

        // 空态提示（plan §7 阶段 3.6）：区分「过滤隐藏了」与「真没有数据」
        if (rows.Count == 0 && _searchText.Length == 0)
        {
            if (_view == DashboardView.Gpu)
                _table.EmptyHint = _cfg.DashboardGpuLuid.Length > 0 ? "提示：切回「全部显卡」试试" : "";
            else if (!_cfg.DashboardShowBackground || !_cfg.DashboardShowSystemProcesses)
                _table.EmptyHint = "提示：顶栏开启「显示后台」和「系统进程」可看到更多";
            else
                _table.EmptyHint = "";
        }
        else
        {
            _table.EmptyHint = "";
        }

        // 选中恢复：按 Key 找回，排序变化不丢选中
        int sel = -1;
        if (_selKey.Length > 0)
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].Key == _selKey) { sel = i; break; }

        _table.SetRows(rows, sel);
        _detail.Show(sel >= 0 ? rows[sel] : null);
    }

    private bool Pass(DashboardTable.Row r, bool gpuView = false)
    {
        if (!_cfg.DashboardShowSystemProcesses && r.System) return false;
        if (!gpuView && !_cfg.DashboardShowBackground && !(r.HasWindow || r.Foreground)) return false;

        // 最小占用阈值（阶段 3）：CPU 与 GPU 都低于阈值才隐藏（零负载后台项会被过滤，任一活跃即显示）
        double min = _cfg.DashboardMinimumPercent;
        if (min > 0 && r.Cpu < min && r.Gpu < min) return false;

        if (_searchText.Length > 0)
        {
            bool hit = r.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase)
                       || (r.Path?.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ?? false)
                       || (r.Description?.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ?? false)
                       || (r.Company?.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ?? false)
                       || (r.WindowTitle?.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ?? false)
                       || r.Pid.ToString(CultureInfo.InvariantCulture).Contains(_searchText, StringComparison.Ordinal);
            if (!hit) return false;
        }
        return true;
    }

    private int Compare(DashboardTable.Row? a, DashboardTable.Row? b)
    {
        if (a == null || b == null) return 0;
        int d = _cfg.DashboardSortDesc ? 1 : -1;
        return _cfg.DashboardSort switch
        {
            DashboardSort.Memory => d * b.WorkingSet.CompareTo(a.WorkingSet),
            DashboardSort.Name => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase) * -d,
            DashboardSort.Pid => -d * a.Pid.CompareTo(b.Pid),
            DashboardSort.Gpu => d * b.Gpu.CompareTo(a.Gpu),
            DashboardSort.Vram => d * b.GpuDedicated.CompareTo(a.GpuDedicated),
            _ => d * b.Cpu.CompareTo(a.Cpu)
        };
    }

    private void OnSortClicked(DashboardTable.Column col)
    {
        var target = MapSort(col);
        if (_cfg.DashboardSort == target)
            _cfg.DashboardSortDesc = !_cfg.DashboardSortDesc;
        else
        {
            _cfg.DashboardSort = target;
            _cfg.DashboardSortDesc = target != DashboardSort.Name && target != DashboardSort.Pid;
        }
        _cfg.Save();
        RebuildRows();
    }

    // ---------------- 管控动作（阶段 4，plan §7） ----------------

    /// <summary>动作目标快照：表格每帧重建，对话框拿打开瞬间的拷贝；进程中途退出 → 执行报「已退出」。</summary>
    internal sealed record ActionTarget(string Name, int Pid, string? Path, bool Limited, List<int> MemberPids);

    private bool GetSuspended(int pid)
    {
        long now = Environment.TickCount64;
        if (_suspendCache.TryGetValue(pid, out var v) && now - v.At < 2000) return v.Suspended;
        if (_suspendCache.Count > 64) _suspendCache.Clear();   // 换行浏览累积，简单粗暴回收
        bool s = pid > 0 && ProcessActions.IsSuspended(pid);
        _suspendCache[pid] = (s, now);
        return s;
    }

    private void OnDetailAction(DashboardAction kind)
    {
        var row = SelectedRow();
        if (row == null) return;

        // 打开文件位置非管控动作（explorer /select），不确认不写日志；系统进程也放行
        if (kind == DashboardAction.OpenLocation)
        {
            if (row.Path != null) OpenLocation(row.Path);
            return;
        }

        // 双保险（安全规则 2）：按钮已禁用，这里拦状态不同步的边缘
        if (row.System) return;

        var target = BuildTarget(row);
        bool suspended = GetSuspended(target.Pid);
        if (kind == DashboardAction.Suspend && suspended) return;      // 状态在弹框前变了：按钮 label 下个 tick 自会纠正
        if (kind == DashboardAction.Resume && !suspended) return;

        using var dlg = CreateActionDialog(kind, target);
        while (true)
        {
            if (dlg.ShowDialog(this) != DialogResult.OK || !dlg.Confirmed) return;
            var result = ExecuteAction(kind, target, dlg.Batch, dlg.PriorityClass);
            if (result.Ok) return;
            dlg.ShowError(result.Error!);   // 不关对话框：可重试或取消
        }
    }

    internal ActionTarget BuildTarget(DashboardTable.Row row)
    {
        var memberPids = new List<int>();
        if (row.Members != null)
            foreach (var m in row.Members)
                if (!memberPids.Contains(m.Pid)) memberPids.Add(m.Pid);
        if (memberPids.Count == 0) memberPids.Add(row.Pid);
        return new ActionTarget(Fmt.NameOr(row), row.Pid, row.Path, row.Limited, memberPids);
    }

    private ActionConfirmDialog CreateActionDialog(DashboardAction kind, ActionTarget t)
    {
        uint? current = kind == DashboardAction.Priority ? ProcessActions.GetPriority(t.Pid) : null;
        return new ActionConfirmDialog(_th, kind, t.Name, t.Pid, t.Path, t.Limited, t.MemberPids.Count, current, _s);
    }

    /// <summary>
    /// 执行动作并维护挂起状态缓存。批量结束逐 PID 各写一行日志，部分失败按失败样例汇总（plan §7 阶段 4.3）。
    /// probe 走同一路径（ProbeExecuteConfirmed）。
    /// </summary>
    internal ActionResult ExecuteAction(DashboardAction kind, ActionTarget t, bool batch, uint priorityClass)
    {
        switch (kind)
        {
            case DashboardAction.Terminate:
            {
                var pids = batch ? t.MemberPids : new List<int> { t.Pid };
                if (pids.Count > 1)
                {
                    int ok = 0;
                    string? firstErr = null;
                    int failPid = 0;
                    foreach (int pid in pids)
                    {
                        var r = ProcessActions.Terminate(pid, t.Name, t.Path ?? "");
                        if (r.Ok) ok++;
                        else
                        {
                            firstErr ??= r.Error;
                            failPid = pid;
                        }
                    }
                    _suspendCache.Clear();
                    return ok == pids.Count
                        ? ActionResult.Success()
                        : ActionResult.Fail($"已结束 {ok}/{pids.Count} 个进程；失败示例 pid={failPid}：{firstErr}");
                }
                var single = ProcessActions.Terminate(t.Pid, t.Name, t.Path ?? "");
                if (single.Ok) _suspendCache.Remove(t.Pid);
                return single;
            }
            case DashboardAction.Suspend:
            case DashboardAction.Resume:
            {
                var result = kind == DashboardAction.Suspend
                    ? ProcessActions.Suspend(t.Pid, t.Name, t.Path ?? "")
                    : ProcessActions.Resume(t.Pid, t.Name, t.Path ?? "");
                if (result.Ok)
                {
                    _suspendCache[t.Pid] = (kind == DashboardAction.Suspend, Environment.TickCount64);
                    _detail.Suspended = kind == DashboardAction.Suspend;
                    _detail.Show(SelectedRow());
                }
                return result;
            }
            case DashboardAction.Priority:
                return ProcessActions.SetPriority(t.Pid, priorityClass, t.Name, t.Path ?? "");
            default:
                return ActionResult.Fail("未知动作");
        }
    }

    private void OpenLocation(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowMessage("打开位置失败", "无法打开文件位置：\n" + ex.Message, error: true);
        }
    }

    private void ShowMessage(string title, string body, bool error) =>
        new MessageDialog(_th, title, body, error, _s).ShowDialog(this);

    // ---------------- probe 钩子（--dashboard-action-ui-probe：与真实 UI 路径同代码） ----------------

    private ActionTarget? _probeTarget;

    /// <summary>按 PID 找行 → 程序化选中（触发真 SelectionChanged → 详情条）→ 记目标快照。</summary>
    internal bool ProbeSelectPid(int pid)
    {
        for (int i = 0; i < _table.Rows.Count; i++)
        {
            var r = _table.Rows[i];
            bool hit = r.Pid == pid || (r.Members?.Any(m => m.Pid == pid) ?? false);
            if (!hit) continue;
            _table.SelectRow(i);
            _probeTarget = BuildTarget(r);
            return true;
        }
        return false;
    }

    internal ActionConfirmDialog ProbeOpenDialog(DashboardAction kind)
    {
        var t = _probeTarget ?? throw new InvalidOperationException("ProbeSelectPid 未调用");
        return CreateActionDialog(kind, t);
    }

    /// <summary>对话框已勾选确认后执行（dlg.Batch / dlg.PriorityClass 与真实确认结果同源）。</summary>
    internal ActionResult ProbeExecuteConfirmed(ActionConfirmDialog dlg, DashboardAction kind)
    {
        var t = _probeTarget ?? throw new InvalidOperationException("ProbeSelectPid 未调用");
        return ExecuteAction(kind, t, dlg.Batch, dlg.PriorityClass);
    }

    private void ApplyViewVisibility()
    {
        // 阶段 2 起 GPU 视图复用同一张表（按 PID 列 GPU 活动进程），占位 Label 移除
        _table.Visible = true;
        _table.NameTitle = _view == DashboardView.App ? "应用" : "进程";
        _table.EmptyText = _view == DashboardView.Gpu ? "没有检测到 GPU 活动" : "无匹配进程";
    }

    // ---------------- 布局 / DPI / 主题 ----------------

    private int R(int px) => (int)Math.Round(px * _s);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        OnScaleChanged();
    }

    private void OnScaleChanged()
    {
        _s = DeviceDpi / 96f;
        DoLayout();
        Invalidate(true);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (IsHandleCreated) DoLayout();
    }

    private void DoLayout()
    {
        int W = ClientSize.Width, H = ClientSize.Height;
        int pad = R(14), gap = R(10);

        int headH = R(50);
        int cardsH = R(106);
        int toolH = R(40);
        int detailH = R(94);
        int statusH = R(24);

        // 顶栏：标题左，控制右（阶段 3 加「系统进程」Chip）
        int btnW = R(96), chipW = R(96), refW = R(280);
        _btnSettings.SetBounds(W - pad - btnW, R(9), btnW, R(32));
        _bgChip.SetBounds(W - pad - btnW - gap - chipW, R(9), chipW, R(32));
        _sysChip.SetBounds(W - pad - btnW - gap - chipW - gap - chipW, R(9), chipW, R(32));
        _refreshSwitch.SetBounds(W - pad - btnW - gap - chipW - gap - chipW - gap - refW, R(9), refW, R(32));
        _btnSettings.Th = _th;
        _btnSettings.FontScale = _s;
        _bgChip.Th = _th;
        _bgChip.FontScale = _s;
        _sysChip.Th = _th;
        _sysChip.FontScale = _s;
        _refreshSwitch.Th = _th;
        _refreshSwitch.FontScale = _s;

        // 总览卡
        int cw = (W - pad * 2 - gap * 3) / 4;
        int cy = headH + gap;
        SetCard(_cardCpu, pad, cw); SetCard(_cardRam, pad + (cw + gap), cw);
        SetCard(_cardGpu, pad + (cw + gap) * 2, cw); SetCard(_cardVram, pad + (cw + gap) * 3, cw);
        void SetCard(MetricCard c, int x, int w)
        {
            c.SetBounds(x, cy, w, cardsH);
            c.Th = _th;
            c.UiScale = _s;
        }

        // 工具栏：视图切换 | 搜索 … 阈值段控 + 多卡下拉（右对齐，多卡下拉仅多卡机器可见）
        int ty = cy + cardsH + gap;
        int segW = R(300);
        _viewSwitch.SetBounds(pad, ty, segW, toolH);
        _viewSwitch.Th = _th;
        _viewSwitch.FontScale = _s;

        int minW = R(230), gpuW = R(240);
        int rx = W - pad;
        _minSwitch.SetBounds(rx - minW, ty + (toolH - R(34)) / 2, minW, R(34));
        _minSwitch.Th = _th;
        _minSwitch.FontScale = _s;
        rx -= minW + gap;
        _gpuFilter.SetBounds(rx - gpuW, ty + (toolH - R(34)) / 2, gpuW, R(34));
        _gpuFilter.Th = _th;
        _gpuFilter.FontScale = _s;

        int rightUsed = (_gpuFilter.Visible ? gpuW + gap : 0) + minW + gap;
        int searchX = pad + segW + gap;
        int searchW = Math.Min(W - pad - searchX - rightUsed, R(430));
        _search.SetBounds(searchX, ty + (toolH - R(34)) / 2, Math.Max(R(160), searchW), R(34));
        _search.UiScale = _s;

        // 列表 + 详情 + GPU 占位
        int tableY = ty + toolH + gap;
        int tableH = Math.Max(R(120), H - statusH - detailH - gap * 2 - tableY);
        _table.SetBounds(pad, tableY, W - pad * 2, tableH);
        _table.Th = _th;
        _table.UiScale = _s;

        _detail.SetBounds(pad, tableY + tableH + gap, W - pad * 2, detailH);
        _detail.SetTheme(_th, _s);   // 按钮的 Theme/FontScale 在这里一起发

        _statusRect = new RectangleF(pad, H - statusH - 2, W - pad * 2, statusH);
    }

    /// <summary>MonitorAppContext 在主题翻转时调用；Theme 实例是静态单例，引用比较即可。</summary>
    public void ApplyTheme(Theme th)
    {
        if (ReferenceEquals(th, _th)) return;
        _th = th;
        BackColor = th.WindowBg;
        _search.ApplyTheme(th);
        DoLayout();
        Invalidate(true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

        var fTitle = Gfx.F("Segoe UI", 14f * _s, FontStyle.Bold);
        using (var tb = new SolidBrush(_th.Text))
            g.DrawString("WMonitor 资源面板", fTitle, tb, R(14), R(14), StringFormat.GenericTypographic);

        var fStatus = Gfx.F("Segoe UI", 10f * _s, FontStyle.Regular);
        string gpuPart = _snap.Gpu.Available
            ? $"GPU {_snap.Gpu.Rows.Count} 项 · GPU 采样 {_snap.GpuMs:0.0} ms"
            : "GPU 进程数据不可用";
        if (_hub.Gpu.Overloaded) gpuPart += " · GPU 暂缓刷新";   // plan §5 降级提示
        string status =
            $"进程 {_snap.ProcessCount} · 应用 {_snap.Apps.Count} · 采样 {_snap.ProcessMs:0.0} ms · {gpuPart} · 最近刷新 {_snap.At:HH:mm:ss}";
        using (var sb = new SolidBrush(_th.Label))
        {
            g.DrawString(status, fStatus, sb, _statusRect.Location, StringFormat.GenericTypographic);
            string hint = "排序：点击列头切换升降序";
            g.DrawString(hint, fStatus, sb,
                _statusRect.Right - Gfx.Measure(g, hint, fStatus).Width, _statusRect.Top,
                StringFormat.GenericTypographic);
        }
    }

    // ---------------- 窗口边界记忆 ----------------

    private void RestoreWindowBounds()
    {
        var parts = _cfg.DashboardWindowRect.Split(',');
        if (parts.Length == 4 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) &&
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y) &&
            int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int w) &&
            int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h) &&
            w >= MinimumSize.Width && h >= MinimumSize.Height)
        {
            var bounds = new Rectangle(x, y, w, h);
            foreach (var sc in Screen.AllScreens)
            {
                if (!sc.WorkingArea.IntersectsWith(bounds)) continue;
                StartPosition = FormStartPosition.Manual;
                Bounds = bounds;
                return;
            }
        }
        StartPosition = FormStartPosition.CenterScreen;   // 记忆无效 / 不可见：回默认
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_cfg.DashboardRememberLayout && WindowState == FormWindowState.Normal)
            _cfg.DashboardWindowRect = $"{Left},{Top},{Width},{Height}";
        _cfg.Save();
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        _hub.Dispose();
        _icons.Dispose();      // 位图缓存释放（磁盘 PNG 保留，下次打开秒回）
        // 一次性有界回收：hub 的进程缓存 / 版本缓存 / 快照刚全部变成垃圾，
        // 不主动收的话私有内存会比基线高 ~10MB（plan §6「关闭后回落基线」验收）。
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        base.OnFormClosed(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) Close();
        base.OnKeyDown(e);
    }
}
