# 08 · v0.05 开发文档 — 多屏贴片 / 磁盘 IO / 历史曲线 / 阈值告警

> 状态：待实现。基线 v0.04（3.3k 行，41 MB / 0.3% CPU）。
> 前置阅读：`AGENTS.md`（环境与禁区）、`01-architecture.md`（数据流）、`03-ui-spec.md`（视觉规格）。
> 本文档是施工图：每个模块给出数据模型、接口签名、改动点、验收条件和已知风险。

---

## 0. 范围

| # | 功能 | 交付形态 | 默认 |
|---|---|---|---|
| A | 副屏任务栏贴片 | 设置里选屏 / 全部屏幕各一片 | 主屏 |
| B | 位置锁定 + 点击穿透 | 设置 + 托盘菜单两个开关 | 关 |
| C | 磁盘 IO 读写速率 | 贴片 cell + 面板第 5 行 + 托盘槽位 | 关 |
| D | 滚动历史（1 分钟 → 3 天） | 面板曲线时间窗 + 磁盘历史 | 开 |
| E | 阈值告警 | 5 条内置规则 + 4 种动作 | 关 |

**不做**（本期明确排除，避免范围蔓延）：网速分接口、Top N 进程、多机监控、插件化数据源、游戏帧时间。

---

## 1. 动手前必须知道的四件事

### 1.1 拖动已经做完了，`06-roadmap.md` 过期

`TaskbarStrip.WndProc`（399–468 行）已实现 `WM_LBUTTONDOWN` 捕获 → `WM_MOUSEMOVE` 跟随 → `WM_LBUTTONUP` 吸附，`Moved` 事件在 `MonitorAppContext` 里写回 `Anchor = Free` + `FreeX/FreeY`。

**结论**：本期「拖动」只剩两个补充项 —— **位置锁定** 和 **点击穿透**。roadmap 里那条要删掉。

### 1.2 定位逻辑是静态单例，写死了主屏

```csharp
// TaskbarStrip.cs:296  —— static，且只找 Shell_TrayWnd
private static bool TryAnchor(AppConfig cfg, int w, int h, out int x, out int y)
{
    IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
    ...
}
```

副屏支持必须把「找任务栏」从 `TryAnchor` 里拆出来，变成**可枚举的外部输入**。这是模块 A 的核心改动，不是加一个 if。

### 1.3 混合 DPI 下 `DeviceDpi` 不可靠

`TaskbarStrip.Update` 第 50 行 `float k = DeviceDpi / 96f;`。

分层窗口在 `Blit` 之前还没定位到目标屏，`DeviceDpi` 取的是窗口**创建时**所在屏的 DPI。双屏 100% + 150% 时，副屏贴片会按 100% 画、按 150% 的位置贴，字明显偏小。

**解法**：DPI 从「目标任务栏所属显示器」直接查，不依赖窗口状态。

```csharp
// 用任务栏 HWND 反查显示器，再查该显示器的有效 DPI
IntPtr mon = Native.MonitorFromWindow(bar.Hwnd, Native.MONITOR_DEFAULTTONEAREST);
Native.GetDpiForMonitor(mon, Native.MDT_EFFECTIVE_DPI, out uint dpiX, out _);
```

`TaskbarStrip` 增加 `public float Scale { get; set; } = 1f;`，第 50 行改为 `float k = Scale;`。**这是模块 A 里最容易漏、也最容易被误判成"位置算错了"的一处。**

### 1.4 设置窗口会超出屏幕

现有内容高约 700 px（400 × 自适应）。本期新增历史 + 告警两个分区约 540 px → 总高 1240 px，1080p 屏（工作区 ~1040）放不下。

**必须做滚动改造**，见 §8.1。这是本期唯一一处结构性 UI 改动，不做的话功能写完用户看不到。

---

## 2. 总体设计

### 2.1 文件变更清单

| 文件 | 动作 | 说明 |
|---|---|---|
| `Interop/Native.cs` | 改 | 加 `EnumWindows` / `GetClassName` / `MonitorFromWindow` / `GetMonitorInfo` / `GetDpiForMonitor` / `MONITORINFO` |
| `UI/TaskbarLocator.cs` | **新增** | 枚举全部任务栏 + 每屏 DPI，输出 `TaskbarInfo` |
| `Metrics/HistoryStore.cs` | **新增** | 内存环 + 磁盘历史 + 抽稀查询 |
| `Metrics/AlertEngine.cs` | **新增** | 规则状态机 + 动作派发 |
| `Metrics/MetricSampler.cs` | 改 | 加磁盘 IO 两个计数器 |
| `UI/TaskbarStrip.cs` | 改 | `TryAnchor` 实例化 + `Scale` + 告警着色 + 点击穿透 |
| `UI/DetailPanel.cs` | 改 | 时间窗 chips + 磁盘行 + 动态高度 |
| `UI/TrayIconRenderer.cs` | 改 | `TrayMetric.Disk` 分支 |
| `UI/SettingsForm.cs` | 改 | 滚动容器 + 两个新分区 + 开关 |
| `UI/Theme.cs` | 改 | 加 `Alert` / `Disk` 两色 |
| `AppConfig.cs` | 改 | 新增 14 个字段 + `AlertRule` |
| `MonitorAppContext.cs` | 改 | 多贴片列表 + 历史 + 告警接线 |
| `Program.cs` | 改 | `--alert-test` / `--history-dump` 诊断 |
| `docs/*` | 改 | 01 / 03 / 05 / 06 / 07 同步 |

### 2.2 新数据流

```
Tick()                                        // 每 RefreshMs
  s = MetricSampler.Sample()                  // 0.3–1.5 ms（+磁盘计数器）
  now = Stopwatch.GetTimestamp()

  hist.Push(s, now)                           // 40 B 写入内存环，<1 µs
  var alert = alerts.Evaluate(s, now)         // 5 条规则，<5 µs

  foreach (strip in _strips)                  // 1..N 片
      strip.Update(s, cfg, theme, bar, alert)

  ApplyAlertActions(alert)                    // 变红 / 闪烁 / 通知 / 脚本（仅状态变化时）
  panel.PushHistory(s)
  if (panel.Visible) panel.Update(s, cfg, theme, hist)
  tray icon（闪烁期间跳过）
```

**线程模型不变**：全部在 UI 线程。历史环是纯内存数组，告警是纯计算，都不需要锁 —— 这是现有架构的既定决策（`01-architecture.md`），不要引入后台线程。

### 2.3 配置兼容策略

- 所有新字段带默认值，`System.Text.Json` 反序列化缺字段时走默认 → **老配置无需迁移**。
- 枚举一律走 `JsonStringEnumConverter`（已有），**新增枚举值必须加在末尾**，否则老配置的下标语义会错。
- `AlertRules` 长度不足 5 时由 `AlertEngine.Configure` 补齐默认，不信任配置文件里的长度。

---

## 3. 模块 A — 多屏任务栏

### 3.1 枚举

```csharp
// UI/TaskbarLocator.cs
public readonly record struct TaskbarInfo(
    IntPtr Hwnd,
    string Device,      // "\\.\DISPLAY1"，用于持久化
    RECT   Rect,        // 任务栏窗口矩形（屏幕坐标）
    uint   Dpi,         // 该显示器的有效 DPI
    bool   IsPrimary);

public static class TaskbarLocator
{
    /// <summary>主屏 + 所有副屏任务栏。副屏任务栏只在「在所有显示器上显示任务栏」开启时存在。</summary>
    public static List<TaskbarInfo> Enumerate();

    /// <summary>按设备名取一片；找不到就回落主屏。用于配置的容错。</summary>
    public static TaskbarInfo? Find(List<TaskbarInfo> all, string device);
}
```

实现要点：

1. 主屏：`FindWindow("Shell_TrayWnd", null)`。
2. 副屏：`EnumWindows` 过滤 `GetClassName == "Shell_SecondaryTrayWnd"`。Win11 每个副屏一个。
3. 每个 HWND → `MonitorFromWindow(MONITOR_DEFAULTTONEAREST)` → `GetMonitorInfo` 拿 `szDevice` → `GetDpiForMonitor` 拿 DPI。
4. 排序：主屏在前，其余按 `rcMonitor.Left` 升序（保证设置里「屏幕 1/2/3」稳定，不随枚举顺序跳动）。
5. 每 tick 都重新枚举的代价约 0.2 ms（副屏任务栏 ≤ 3 个），**但没必要**：缓存在 `MonitorAppContext`，只在 `WM_DISPLAYCHANGE`、Explorer 重启（`Shell_TrayWnd` 句柄变化）、设置变更时刷新。刷新判据：`all[0].Hwnd != _cached[0].Hwnd || all.Count != _cached.Count`。

### 3.2 TaskbarStrip 改造

```csharp
public sealed class TaskbarStrip : LayeredWindow
{
    public float Scale { get; set; } = 1f;              // 由 TaskbarLocator 的 Dpi 算
    public string Device { get; set; } = "";            // 归属显示器
    public bool ClickThrough { get; set; }              // → CreateParams 加 WS_EX_TRANSPARENT
    public bool LockPosition { get; set; }

    public void Update(MetricsSnapshot s, AppConfig cfg, Theme th,
                       TaskbarInfo bar, AlertView alert);

    private bool TryAnchor(AppConfig cfg, TaskbarInfo bar, int w, int h, out int x, out int y);
    public void Reanchor(AppConfig cfg, TaskbarInfo bar);
}
```

`TryAnchor` 从 `static` 改实例方法，逻辑本身不变，只把 `FindWindow` 换成入参 `bar.Rect`：

```csharp
private bool TryAnchor(AppConfig cfg, TaskbarInfo bar, int w, int h, out int x, out int y)
{
    x = y = 0;
    if (cfg.Anchor == StripAnchor.Free)
    {
        if (cfg.FreeX < 0 || cfg.FreeY < 0) return false;
        x = cfg.FreeX; y = cfg.FreeY;
        ClampToScreen(ref x, ref y, w, h);
        return true;
    }

    var tr = bar.Rect;
    // 自动隐藏：取短边判断（横向任务栏高度≈48，竖向≈宽度）—— 只判断高度会误伤竖向
    if (Math.Min(tr.Width, tr.Height) < 12 || tr.Width < 200) return false;

    switch (cfg.Anchor) { /* 原逻辑，tr 来自 bar.Rect */ }

    x += cfg.OffsetX;
    y = tr.Top + (tr.Height - h) / 2;
    return true;
}
```

> Win11 已经不允许把任务栏移到屏幕侧边（设置里没有这个选项），所以**不需要实现竖向布局**。上面只用短边判断避免误伤，不做竖向排版。

`GetAppsBounds` / `GetTrayLeft` 里的 `FindWindowEx(tray, ...)` 同样改成用 `bar.Hwnd` 作为父窗口。

### 3.3 MonitorAppContext 改造

```csharp
private List<TaskbarStrip> _strips = new();   // 取代单个 _strip
private List<TaskbarInfo> _bars = new();

private void RebuildStrips()
{
    _bars = TaskbarLocator.Enumerate();

    var want = new List<TaskbarInfo>();
    if (_cfg.ShowOnAllMonitors) want.AddRange(_bars);
    else want.Add(TaskbarLocator.Find(_bars, _cfg.StripMonitor) ?? _bars[0]);

    // 差量复用：设备名 + 序号都对得上就沿用实例，避免每秒重建窗口
    ...
    foreach (var st in _strips) { st.Clicked -= OnStripClicked; st.Moved -= OnStripMoved; st.Dispose(); }
    _strips = ...;
}
```

- `Clicked` 事件必须带上来源贴片：`_strip.Clicked += (s, _) => OpenPanel((TaskbarStrip)s!);`，面板定位用**被点那一片**的 `StripBounds`。
- `Moved` 在多贴片模式下**不持久化**（见 §3.5）。
- `RefreshAll()` 里 `_strip.Reanchor(_cfg)` 改成遍历。

### 3.4 设置项

```
贴片所在屏幕   [ 屏幕 1 ][ 屏幕 2 ][ 全部 ]     ← 只有 1 屏时整行隐藏
```

分段控件项数 = 屏数 + 1，项文案 `屏幕 N`（宽度够时带分辨率 `屏幕 1 · 2560×1440`）。屏数 > 4 时只列前 3 屏 + `全部`，其余屏在托盘菜单里选（避免 352 px 里挤 6 个选项）。

### 3.5 边界与取舍

| 情况 | 行为 |
|---|---|
| 副屏没开「在所有显示器上显示任务栏」 | 枚举不到该屏，`Find` 回落主屏，不报错 |
| 配置里记的显示器拔掉了 | 回落主屏，并在 `error.log` 记一行 |
| 自动隐藏 | 短边 < 12 → `Hide()`，鼠标移出后靠每 tick 重定位自愈（现有机制） |
| 多贴片模式下拖动 | **禁用拖动**。原因：配置模型只有一个 `FreeX/FreeY`，多屏各自自由坐标需要 `Dictionary<device, Point>`，本期不值得为它加一层配置。点击照常可用 |
| 换分辨率 / 插拔屏 | 靠每 tick 重定位自愈；`RebuildStrips` 由句柄变化触发 |

### 3.6 验收

- 双屏 100% + 150%：两片贴片字号比例正确、垂直居中、不越界。
- 副屏任务栏在屏幕顶部时，贴片仍贴托盘左侧。
- 主屏 Explorer 重启 → 两片都恢复。
- 拔掉副屏 → 不崩，贴片回到主屏。
- 内存增量 ≤ 1 MB（每片一个 30 px 位图 + 一个窗口）。

---

## 4. 模块 B — 位置锁定 / 点击穿透

两项都只是 `CreateParams` 与 `WndProc` 的小改。

### 4.1 点击穿透

```csharp
// LayeredWindow.cs
public bool ClickThrough { get; set; }              // 默认 false

protected override CreateParams CreateParams
{
    get
    {
        var cp = base.CreateParams;
        cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW;
        if (NoActivate)    cp.ExStyle |= Native.WS_EX_NOACTIVATE;
        if (ClickThrough)  cp.ExStyle |= Native.WS_EX_TRANSPARENT;   // 常量已存在于 Native.cs:57
        return cp;
    }
}
```

运行时切换需要 `UpdateStyles()`（WinForms 会用新 `CreateParams` 重设 `ExStyle`）。

**必须同时说明的副作用**：穿透后贴片点不动 → 详情面板只能从托盘菜单进。设置里开启这一项时，在下方加一行提示：`穿透后点不到贴片，请从托盘菜单打开面板`。

### 4.2 位置锁定

```csharp
// TaskbarStrip.WndProc
case WM_LBUTTONDOWN:
    if (LockPosition) return;   // 不捕获鼠标，但仍允许 WM_LBUTTONUP → Clicked
    ...
```

只锁拖动、不锁点击。`LockPosition` 由 `cfg.LockPosition` 每 tick 同步到实例字段（`Update` 里赋一次即可）。

### 4.3 验收

- 开穿透后鼠标点击直接落到下面的窗口（用任务栏图标验证）。
- 开锁定后拖不动、点得开面板；关掉后能拖。
- 两项都关时行为与 v0.04 完全一致（回归）。

---

## 5. 模块 C — 磁盘 IO

### 5.1 采样

`MetricSampler` 主 query 追加两个计数器（成本约 0.05 ms，可接受）：

```
\PhysicalDisk(_Total)\Disk Read Bytes/sec
\PhysicalDisk(_Total)\Disk Write Bytes/sec
```

`MetricsSnapshot` 加：

```csharp
public double DiskReadBps;
public double DiskWriteBps;
```

### 5.2 贴片

`BuildCells` 在网速之后追加两个 cell（`ShowDisk` 打开才出现）：

```csharp
if (cfg.ShowDisk)
{
    list.Add(new("R", RateFmt(s.DiskReadBps),  "", th.Disk, 0, false));
    list.Add(new("W", RateFmt(s.DiskWriteBps), "", th.Disk, 0, false));
}
```

`NetFmt` 重命名为 `RateFmt`（网速和磁盘共用），`TrayIconRenderer.NetText` 同步改名 `RateText`，避免两份实现。

### 5.3 面板

第 5 行，插在显存之后：

```
DSK  1.2 / 0.4 MB/s        ▁▂▅▃   1.2 MB/s
```

- `PanelH = 314 + (cfg.ShowDisk ? 52 : 0)`，底部线 / 齿轮 / 关闭按钮的 y 全部由 `rowY + rowH * rowCount + 4` 推导，**不要再写死 250 / 262**（`03-ui-spec.md` 里的固定值要同步改成公式）。
- 图标：`DrawIcon` 加 `"disk"` 分支（两条横线 + 一个圆柱侧影，14 × 14 内画）。

### 5.4 托盘槽位

`TrayMetric` 末尾加 `Disk = 6`；`MetricNames` 追加 `"磁盘"`（设置里 7 项 × 282 px ≈ 40 px/项，「磁盘」两个字放得下）。

`TrayIconRenderer.Metric`：

```csharp
TrayMetric.Disk => new Val(RateText(s.DiskReadBps + s.DiskWriteBps),
                           NetPct(s.DiskReadBps + s.DiskWriteBps), th.Disk),
```

沿用网速的对数刻度（20 KB/s ≈ 26%，1 MB/s ≈ 60%，100 MB/s 满），不新造一套量程。

### 5.5 配色

| 主题 | `Disk` | `Alert` |
|---|---|---|
| Dark | `#FF8FB1` | `#FF453A` |
| Light | `#C2255C` | `#D70015` |

现有 5 色（CPU 蓝 / 内存 紫 / GPU 绿 / 显存 橙 / 网速 青）之后，磁盘用玫红系，与橙色显存拉开。

### 5.6 验收

- `--probe` 输出含 `disk=读/写 KB/s`，与任务管理器「性能 → 磁盘」数量级一致（允许 ±20%，PDH 与任务管理器口径差异）。
- 关闭 `ShowDisk` 时贴片宽度与 v0.04 一致（回归）。
- 面板开磁盘行时总高 366，不遮挡任务栏。

---

## 6. 模块 D — 历史存储 `HistoryStore`

这是本期唯一的新子系统。设计目标：**给「刚才那波卡顿是什么」一个答案**，同时不把内存和 CPU 基线打穿。

### 6.1 数据模型

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct HistoryRecord          // 40 B
{
    public uint  Ts;        // Unix 秒
    public float Cpu;       // 0–100
    public float Ram;
    public float Gpu;
    public float Vram;
    public float DiskR;     // KB/s
    public float DiskW;
    public float NetDown;   // KB/s
    public float NetUp;
    public float Temp;      // °C；NaN = 无 NVML
}

public enum HistoryMetric { Cpu, Ram, Gpu, Vram, Disk, Net, Temp }

public readonly record struct HistorySeries(
    float[] Values, long FromTs, long ToTs, bool FromDisk, int BucketSeconds);
```

**为什么是 40 B 定长结构**：`BinaryWriter` 逐字段写会引入 9 次虚调用，`MemoryMarshal.Write` 一次 40 B 更干净；也方便将来按固定偏移读取。采样间隔由 `RefreshMs` 决定（0.5–5 s），所以**每条都带 `Ts`**，不能靠下标推算时间。

### 6.2 两级存储

| 层级 | 内容 | 容量 | 用途 |
|---|---|---|---|
| 内存环 | 最近 3600 条（≥1 小时，1 秒粒度） | 3600 × 40 B = **144 KB** | 面板 1 分钟 / 5 分钟 / 1 小时 |
| 磁盘文件 | 按天滚动 `history\YYYYMMDD.hist` | 3.4 MB/天 | 面板 6 小时 / 24 小时 / 3 天 + 重启后回看 |

**为什么内存只留 1 小时**：24 小时全放内存是 3.4 MB，72 小时是 10 MB，会把「41 MB」这个卖点毁掉。1 小时覆盖了 95% 的「刚才卡了」场景，更长的窗口本来就要跨重启，天然属于磁盘。

文件格式：

```
[8B]  magic "WMHIST01"
[40B] HistoryRecord × N     // 按时间升序追加
```

- 写：常驻一个 `FileStream(FileMode.Append)`，每 60 秒 `Flush()` 一次（不 `Flush(true)`，避免每秒 fsync 打盘）。
- 日期变了就关旧开新。
- 读：`File.ReadAllBytes` 后按 40 B 切分；尾部不足 40 B 直接丢弃（崩溃留下的半条记录）。
- 损坏处理：magic 不对 / 长度不对 → 跳过该文件并在 `error.log` 记一行，**不抛异常**。

### 6.3 查询与抽稀

```csharp
public HistorySeries Query(HistoryMetric m, TimeSpan window, int points = 60);
```

- `window <= 1h` → 从内存环取；`> 1h` → 从磁盘取（未开持久化时返回空，见 §6.5）。
- 按 `window / points` 分桶，**每桶取最大值**。
  - 理由：利用率类指标的价值在尖峰，平均会把 3 秒的满载摊平成看不出。内存/显存也用 max，保持一套逻辑，代价是 24 小时视图的曲线偏「上沿」，可接受。
- 输出固定 `points` 个 `float`，与现有 `DrawSpark` 的入参完全兼容 —— **`DetailPanel.DrawSpark` 一行都不用改**。

### 6.4 面板交互

`DetailPanel` 标题行右侧的 `1s · 60 点` 换成 4 个可点击小段：

```
系统监视                          [1m] [5m] [1h] [24h]
```

| 项 | 值 |
|---|---|
| 段尺寸 | 26 × 16，圆角 5，间距 4，右对齐到 `right`，y = 12 |
| 选中 | 底 `th.Hover`，字 `th.Text` |
| 未选 | 无底，字 `th.Label` |
| 命中 | `WndProc` 里按 `_winRects[]` 判断（与 `_gearRect` 同一套做法） |
| 持久化 | 点击写 `cfg.ChartWindowMinutes`，立即 `Save()` |

段与窗口的映射：`1m → 1 分钟`、`5m → 5 分钟`、`1h → 1 小时`、`24h → 24 小时`。**持久化关闭时只显示 `1m/5m/1h` 三段**（`24h` 无数据源，不给死链）。

### 6.5 开关语义（用户要求的「开关 + 对应设置」）

三层，逐层收窄：

| 配置 | 类型 | 默认 | 关掉后 |
|---|---|---|---|
| `HistoryEnabled` | 总开关 | **true** | 不采不存不落盘；面板退回 v0.04 的 60 点实时缓冲；内存回到 41 MB；**不创建 `history` 目录** |
| `HistoryPersist` | 跨重启保留 | **true** | 只留内存环，24 小时档从面板消失；不写任何文件 |
| `HistoryRetentionHours` | 保留时长 | **24** | 可选 `6 / 24 / 72`；启动时删除超期文件 |

**关闭总开关必须是真正的零开销**：`HistoryStore` 不实例化（`_hist = null`），`DetailPanel` 走原路径。不要「采了但不显示」—— 那既浪费又会让用户以为开关没生效。

### 6.6 生命周期

```csharp
public sealed class HistoryStore : IDisposable
{
    public HistoryStore(bool persist, int retentionHours);
    public bool Enabled { get; }
    public long OldestTs { get; }                       // 最早可用时间戳，面板用来禁用空窗口
    public void Push(MetricsSnapshot s, long nowTicks);  // 满 1 秒才落一条（RefreshMs 0.5 秒时跳采）
    public HistorySeries Query(HistoryMetric m, TimeSpan window, int points = 60);
    public void Dispose();                               // 关流 + Flush
}
```

- **节流**：`RefreshMs = 500` 时每 2 tick 才写一条，保证文件始终 1 秒粒度（3.4 MB/天可预测）。
- 启动：读最近 `retentionHours` 的文件尾部填充内存环（只填最后 3600 条，`Seek` 到 `Length - 3600*40`）。
- 退出：`MonitorAppContext.Quit()` 里 `_hist?.Dispose()`（放在 `_sampler.Dispose()` 之前）。
- 目录：`%APPDATA%\WMonitor\history\`。

### 6.7 开销

| 项 | 值 |
|---|---|
| 每次 Push | 40 B 内存写，< 1 µs |
| 每分钟落盘 | ≤ 2.4 KB 追加 + 一次 Flush |
| 面板打开 24h | 读 1 个 3.4 MB 文件 + 抽稀，约 15 ms（只在打开/切窗时发生一次） |
| 常驻内存 | +144 KB |
| CPU | +0.02% |

### 6.8 验收

- 跑 10 分钟：`history\YYYYMMDD.hist` ≈ 24 KB；`--history-dump` 能打印首尾时间戳与条数。
- 重启后打开面板切到 24h，能看到重启前的曲线（连续性不断档，允许 1 个采样点的空档）。
- 关总开关 → 重启 → `history` 目录不存在、内存回 41 MB。
- 关持久化 → 面板只有 3 个时间窗。
- 故意把 `.hist` 尾部截断 20 B → 不崩，丢最后一条。

---

## 7. 模块 E — 阈值告警 `AlertEngine`

### 7.1 规则模型

**固定 5 条内置规则**，不做动态规则编辑器。理由：动态列表要配增删 UI、排序、持久化索引，成本是固定表的 4 倍，而 5 个指标已经覆盖全部可告警项。

```csharp
public enum AlertMetric { Cpu = 0, Ram = 1, Gpu = 2, Vram = 3, GpuTemp = 4 }

public sealed class AlertRule
{
    public AlertMetric Metric    { get; set; }
    public bool        Enabled   { get; set; } = false;
    public double      Threshold { get; set; }      // 百分比或 °C
    public int         DurationSec { get; set; } = 5;    // 持续越线多久才触发
    public int         CooldownSec { get; set; } = 300;  // 触发后静默期

    public static AlertRule[] Defaults() => new[]
    {
        new AlertRule { Metric = AlertMetric.Cpu,     Threshold = 90,  DurationSec = 30 },
        new AlertRule { Metric = AlertMetric.Ram,     Threshold = 90,  DurationSec = 60 },
        new AlertRule { Metric = AlertMetric.Gpu,     Threshold = 95,  DurationSec = 10 },
        new AlertRule { Metric = AlertMetric.Vram,    Threshold = 95,  DurationSec = 10 },
        new AlertRule { Metric = AlertMetric.GpuTemp, Threshold = 85,  DurationSec = 5  },
    };
}
```

默认全部 `Enabled = false` + 全局 `AlertsEnabled = false`：**装完不打扰**，用户主动开。

### 7.2 状态机

每条规则独立：

```
        value >= Threshold                    持续 >= DurationSec
Idle ──────────────────────► Armed ───────────────────────────► Firing
  ▲                             │  value < Threshold - 迟滞           │
  │                             └──────────────► Idle                │
  └───────────── value < Threshold - 迟滞 ───────────────────────────┘
                                              （同时进入 CooldownSec 静默）
```

- **迟滞**：恢复阈值 = `Threshold - 5`（百分比）/ `Threshold - 3`（温度）。防止在阈值上下抖动导致反复告警。
- **持续**：`Armed` 状态下累计时间；中途回到 `Idle` 就清零。用 `Stopwatch` tick 差，不用 `DateTime`（防系统时间跳变）。
- **冷却**：`Firing` 后 `CooldownSec` 内不再产生新事件，但 `Firing` 状态保持（贴片继续红）。
- 温度规则在 `!s.HasTemp` 时直接 `Idle`（无 NVML 不告警，不报 0）。

### 7.3 输出

```csharp
public sealed class AlertView                    // 每 tick 返回，给 UI 用
{
    public AlertMetric?[] Firing;                // 当前处于告警的指标（贴片着色用）
    public List<AlertEvent> Events;              // 本 tick 新触发的（动作派发用）
}

public readonly record struct AlertEvent(AlertMetric Metric, double Value, double Threshold);
```

### 7.4 四种动作

| 动作 | 配置 | 实现 |
|---|---|---|
| 贴片变红 | `AlertStrip` | `BuildCells` 后按 `alert.Firing` 覆盖 `Cell.Color` → `th.Alert`，数值文字也用 `th.Alert` |
| 托盘闪烁 | `AlertTrayFlash` | 预渲染两张 Icon（常态 + 红边），`Timer(400ms)` 切换 6 次后复原。**闪烁期间 `Apply` 跳过图标更新**（`_flashing` 守卫），否则会和 `_trayKey` 逻辑打架 |
| 系统通知 | `AlertToast` | `_tray.ShowBalloonTip(5000, "WMonitor 告警", $"{名称} {值:0}{单位} 超过 {阈值:0}{单位}", ToolTipIcon.Warning)`。零依赖，Win11 会呈现为系统通知 |
| 运行脚本 | `AlertScript` + `AlertScriptPath` | 见 §7.5 |

**贴片变红要改 `Cell` 的构造**：现有 `Cell` 是 `record struct`，`Color` 在 `BuildCells` 里定死。最省事的做法是在 `BuildCells` 末尾统一重着色：

```csharp
foreach (var c in list)
    if (alert.Firing.Contains(MapToAlert(c.Label)))
        list[i] = c with { Color = th.Alert };
```

需要 `Label → AlertMetric` 的映射表（`"CPU"→Cpu`、`"T"→GpuTemp` …）。`record struct` 的 `with` 表达式正好用得上。

### 7.5 脚本执行与安全

```csharp
var psi = new ProcessStartInfo
{
    FileName = "cmd.exe",
    CreateNoWindow = true,
    UseShellExecute = false,
    WorkingDirectory = Path.GetDirectoryName(path)!
};
psi.ArgumentList.Add("/c");
psi.ArgumentList.Add(path);                       // 用 ArgumentList，不拼命令行字符串
psi.Environment["WMONITOR_METRIC"]    = rule.Metric.ToString();
psi.Environment["WMONITOR_VALUE"]     = value.ToString("0.#", CultureInfo.InvariantCulture);
psi.Environment["WMONITOR_THRESHOLD"] = rule.Threshold.ToString("0.#", CultureInfo.InvariantCulture);

var p = Process.Start(psi);
if (p != null && !p.WaitForExit(30_000)) { try { p.Kill(true); } catch { } }
```

安全约束（写进 `05-known-issues.md`）：

- 只执行用户在设置里**显式选中**的文件，路径来自 `OpenFileDialog`，不从配置里解析命令行。
- 30 秒超时强杀（含子进程树），避免脚本卡死拖住 UI 线程 —— 所以 `WaitForExit` 必须放在 `Task.Run` 里，或者干脆不等（fire-and-forget + 一个独立 Timer 超时）。**推荐 fire-and-forget**：UI 线程绝不能阻塞 30 秒。
- 同一规则在冷却期内只跑一次。

### 7.6 静音

托盘菜单 `告警 → 静音 1 小时`。静音期间只停止「派发动作」，`Firing` 状态照常维护（贴片还是红的），菜单项显示剩余时间。

### 7.7 验收

- `WMonitor.exe --alert-test`：伪造 CPU 99% 连续 40 秒，验证 变红 → 闪烁 → 通知 全链路，日志写 `%APPDATA%\WMonitor\alert.log`。
- 真机验证：跑 `nvidia-smi -lms 100` 或一个满载脚本，GPU 温度规则触发。
- 关闭总开关 → 零开销（`AlertEngine` 不实例化）。
- 阈值上下抖动 20 次 → 只触发 1 次（迟滞 + 冷却生效）。
- 脚本超时 → 30 秒后被 kill，UI 不卡。

---

## 8. 模块 F — 设置界面

### 8.1 滚动改造（必做）

```csharp
private readonly Panel _scroll = new();     // Dock = Fill, AutoScroll = true

private void LayoutBlocks()
{
    int y = 18;
    foreach (var b in _blocks) { /* 原逻辑，只改父容器 */ }

    int contentH = y + 14;
    _scroll.AutoScrollMinSize = new Size(FormW - SystemInformation.VerticalScrollBarWidth, contentH);
    int maxH = Screen.PrimaryScreen!.WorkingArea.Height - 120;
    ClientSize = new Size(FormW, Math.Min(contentH, maxH));
}
```

要点：

1. 所有控件 `Controls.Add` → `_scroll.Controls.Add`；`_scroll` 加到窗体并 `Dock = Fill`。
2. `_scroll.BackColor = _th.PanelBgOpaque`（**不能带 alpha**，见 `05-known-issues.md` #3）。
3. `Recolor()` 里遍历 `_scroll.Controls` 而不是 `Controls`。
4. 滚动时自绘控件的 `g.Clear(Parent?.BackColor ?? ...)` 已经取 `Parent.BackColor`，行为不变。
5. `FlowLayoutPanel`（显示项 chip 组）在滚动容器里要显式设 `Width = ContentW - 20`，否则会因 `AutoScroll` 出现横向滚动条。
6. `FormBorderStyle.FixedSingle` 保留；`MaximizeBox` 仍 `false`。

### 8.2 分区顺序（新）

```
贴片位置（含屏幕选择）
贴片风格
托盘图标 → 主/次/三/四指标
主题 / 刷新间隔
贴片显示项（+ 磁盘 IO）
不透明度
────────────
历史记录                     ← 新增
────────────
告警                         ← 新增
────────────
行为开关（置顶 / 自启 / 显示贴片 / 锁定位置 / 点击穿透）
水平微调
[恢复默认]              [完成]
```

### 8.3 「历史记录」分区逐像素

| 块 | 内容 | 尺寸 / 位置 | 显示条件 |
|---|---|---|---|
| 1 | `Chip 记录历史` + `Chip 跨重启保留` | 高 30，间距 8，x 从 24 起 | 总是 |
| 2 | Caption `保留时长` + Segmented `6 小时 / 24 小时 / 3 天` | Caption 20 高；Segmented 34 高、宽 352 | `HistoryEnabled && HistoryPersist` |
| 3 | Caption `面板默认时间窗` + Segmented `1 分钟 / 5 分钟 / 1 小时 / 24 小时` | 同上 | `HistoryEnabled` |
| 4 | Caption `已用 xx MB · 最早 xx:xx` （只读） | 20 高，`Label` 色 | `HistoryEnabled && HistoryPersist` |

**时间窗分段项数要动态**：`HistoryPersist` 关掉时只有 3 项（`1m/5m/1h`），重建 `Items` 数组后 `SelectedIndex = Math.Min(SelectedIndex, Items.Length-1)`。不要给 `Segmented` 加 disabled 态 —— 那要改控件绘制和命中，性价比低。

块高：`30+16` / `20+34+12` / `20+34+12` / `20+8` → 合计约 196 px。

### 8.4 「告警」分区逐像素

| 块 | 内容 | 尺寸 |
|---|---|---|
| 1 | `Chip 启用告警` | 30 高 |
| 2–6 | 5 行规则 | 每行 30 高 + 8 间距 = 38 |
| 7 | 动作 chips（FlowLayoutPanel） | 高 76（两行） |
| 8 | `选择脚本…` 按钮 + 路径 Label | 30 高 |

单行规则排布（总宽 310，落在 24–376 内）：

```
x=24   Chip "CPU"          62 × 30
x=94   Stepper 阈值        76 × 30      (Minimum 40, Maximum 100 / 温度 40–110)
x=178  单位 Label "%"       16 × 30      右对齐，Label 色
x=200  Caption "持续"       30 × 30
x=236  Stepper 秒数        64 × 30      (Minimum 1, Maximum 300)
x=306  Caption "秒"         16 × 30
```

- 规则行的 5 个控件同属一个 `Block`，`Show` 条件 = `AlertsEnabled`。整块隐藏时行高归零，窗口自然缩短。
- 阈值 Stepper 的 `Minimum/Maximum` 按指标类型在构造时设（百分比 40–100，温度 40–110）。
- 动作 chips：`贴片变红` `托盘闪烁` `系统通知` `运行脚本`，走现有 `AddChip(_flow, ...)` 模式。
- 脚本行 `Show` 条件 = `AlertsEnabled && AlertScript`。路径 Label 超宽用 `TextRenderer.MeasureText` 截断加 `…`。

块高：`30+16` + `5×38` + `20+76+8` + `30+16` ≈ 340 px。

### 8.5 行为开关行改造

现有 3 个 chip（置顶 / 自启 / 显示贴片）手算 `cx += c.Width + 8` 排一行。新增 2 个后总宽超 352，改成 `FlowLayoutPanel`（高 76，两行）：

```csharp
var behaviors = new FlowLayoutPanel
{
    Left = Pad, Width = ContentW - 20, Height = 76,
    BackColor = _th.PanelBgOpaque, WrapContents = true, AutoScroll = false
};
```

5 个 chip 顺序：`始终置顶` `开机自动启动` `显示贴片` `锁定位置` `点击穿透`。

### 8.6 `ResetDefaults` 必须重构

现有实现用**下标**映射 chip 与字段：

```csharp
// SettingsForm.cs:376 —— 加 chip 之后必然错位
_chips[i].Checked = i switch { 0 => _cfg.ShowCpu, ... };
```

改成注册时带上默认值访问器：

```csharp
private void AddChip(FlowLayoutPanel flow, string text,
                     Func<bool> get, Action<bool> set,
                     Func<AppConfig, bool> dflt)
{
    ...
    _chips.Add((chip, dflt));     // List<(Chip, Func<AppConfig,bool>)>
}

// ResetDefaults:
foreach (var (chip, dflt) in _chips)
{
    chip.Checked = dflt(d);
    chip.Invalidate();
}
```

同时 `ResetDefaults` 要重置新增字段（`HistoryEnabled/Persist/RetentionHours/ChartWindowMinutes/AlertsEnabled/AlertRules/Alert*/ShowDisk/ShowOnAllMonitors/StripMonitor/LockPosition/ClickThrough`），并重建规则行 Stepper 的 `Value`。

### 8.7 主题重着色

`RecolorControl` 需要补的分支：

```csharp
else if (c is Panel p && p.Height <= 2) { p.BackColor = _th.Line; }   // 已有
```

`Panel` 分支现在只处理分隔线（`Height <= 2`）。滚动容器 `_scroll` 高度 > 2，必须单独处理，否则换主题后设置窗口背景不会跟着变：

```csharp
if (c == _scroll) { _scroll.BackColor = _th.PanelBgOpaque; }
```

---

## 9. 配置模型

### 9.1 新增字段（`AppConfig.cs`）

```csharp
// ---- 多屏 ----
public bool   ShowOnAllMonitors { get; set; } = false;
public string StripMonitor      { get; set; } = "";      // "" = 主屏；"\\.\DISPLAY2"
public bool   LockPosition      { get; set; } = false;
public bool   ClickThrough      { get; set; } = false;

// ---- 磁盘 ----
public bool ShowDisk { get; set; } = false;

// ---- 历史 ----
public bool HistoryEnabled        { get; set; } = true;
public bool HistoryPersist        { get; set; } = true;
public int  HistoryRetentionHours { get; set; } = 24;     // 6 / 24 / 72
public int  ChartWindowMinutes    { get; set; } = 1;      // 1 / 5 / 60 / 1440

// ---- 告警 ----
public bool        AlertsEnabled  { get; set; } = false;
public AlertRule[] AlertRules     { get; set; } = AlertRule.Defaults();
public bool        AlertStrip     { get; set; } = true;
public bool        AlertTrayFlash { get; set; } = true;
public bool        AlertToast     { get; set; } = true;
public bool        AlertScript    { get; set; } = false;
public string?     AlertScriptPath{ get; set; }
```

### 9.2 JSON 示例

```json
{
  "Strip": "Numbers",
  "ShowOnAllMonitors": false,
  "StripMonitor": "\\\\.\\DISPLAY1",
  "LockPosition": false,
  "ClickThrough": false,
  "ShowDisk": false,
  "HistoryEnabled": true,
  "HistoryPersist": true,
  "HistoryRetentionHours": 24,
  "ChartWindowMinutes": 1,
  "AlertsEnabled": false,
  "AlertRules": [
    { "Metric": "Cpu", "Enabled": false, "Threshold": 90, "DurationSec": 30, "CooldownSec": 300 },
    { "Metric": "Ram", "Enabled": false, "Threshold": 90, "DurationSec": 60, "CooldownSec": 300 }
  ],
  "AlertStrip": true,
  "AlertTrayFlash": true,
  "AlertToast": true,
  "AlertScript": false,
  "AlertScriptPath": null
}
```

### 9.3 兼容性

| 场景 | 行为 |
|---|---|
| v0.04 的 config.json | 缺字段走默认，直接可用 |
| `AlertRules` 少于 5 条 | `AlertEngine.Configure` 补齐，缺的用 `Defaults()` 同指标项 |
| `HistoryRetentionHours` 非法值 | `Math.Clamp(v, 6, 72)` |
| `ChartWindowMinutes` 大于可用数据 | 面板照常显示，曲线前段为空（`DrawSpark` 遇到 < 3 点不画） |
| `StripMonitor` 指向不存在的屏 | 回落主屏 + 写 `error.log` |

---

## 10. 托盘菜单变更

```
贴片位置 ▸
贴片所在屏幕 ▸   主屏 / 屏幕 2 / 屏幕 3 / 全部屏幕          ← 新增（1 屏时隐藏）
贴片风格 ▸
托盘图标 ▸
主题 ▸
──────────
显示项 ▸         [任务栏贴片] ── CPU / 内存 / GPU / 显存 / 网速 / GPU 温度 / 磁盘 IO  ← 新增磁盘
历史 ▸           记录历史 / 跨重启保留 / ── / 1m 5m 1h 24h   ← 新增
告警 ▸           启用告警 / 静音 1 小时 / 打开告警设置…      ← 新增
──────────
详情面板…
设置…
暂停刷新
──────────
退出
```

「历史」子菜单里的时间窗项用 `Radio` 记录（`AddRadio`），与面板里的小段双向同步 —— 改任一处都要 `_cfg.Save()` 并 `_panel.Invalidate()`。

---

## 11. 诊断钩子

| 参数 | 行为 | 输出 |
|---|---|---|
| `--probe` | 扩展为输出 `disk=读/写 KB/s`、`hist=条数/最早时间`、`alert=Firing 列表` | `D:/Hermes/output/results/sysmon-probe.txt` |
| `--alert-test` | **新增**：伪造 CPU 99% 连续 40 秒，走完整告警链路 | `%APPDATA%\WMonitor\alert.log` |
| `--history-dump` | **新增**：打印内存环与磁盘文件的首尾时间戳、条数、文件大小 | stdout |
| `--icons` | 不变 | `wmonitor-icons.png` |

`--alert-test` 的意义：温度规则要等真实负载，验证一次得跑游戏。伪造快照能 40 秒内验证 变红 / 闪烁 / 通知 / 脚本 四条链路。

---

## 12. 任务拆解

7 个提交，每个都能独立编译、独立验收。**不要并行改同一文件**（`AGENTS.md` §7）。

| # | 提交 | 内容 | 估时 | 验收 |
|---|---|---|---|---|
| 1 | `feat(interop): 多屏枚举与 DPI 查询` | `Native` 加 5 个 API + `TaskbarLocator`；`TaskbarStrip.Scale` 替换 `DeviceDpi` | 0.5 d | 单屏行为零变化（回归）；双屏日志打印出两块屏的 DPI |
| 2 | `feat(strip): 副屏贴片` | `TryAnchor` 实例化、`MonitorAppContext` 多贴片、设置屏幕选择 | 1 d | §3.6 |
| 3 | `feat(history): 历史存储` | `HistoryStore` + `DetailPanel` 时间窗 chips + 配置 | 1.5 d | §6.8 |
| 4 | `feat(alert): 阈值告警` | `AlertEngine` + 4 种动作 + 静音 + `--alert-test` | 1.5 d | §7.7 |
| 5 | `feat(disk): 磁盘 IO` | PDH 两计数器 + 贴片 cell + 面板行 + 托盘槽位 | 0.5 d | §5.6 |
| 6 | `feat(settings): 滚动容器与新分区` | §8 全部 | 1 d | 1080p 下窗口不超屏；滚动顺畅；换主题无残留 |
| 7 | `chore: 文档与发布 v0.05` | 同步 01/03/05/06/07 + `AGENTS.md` + 三个产物 | 0.5 d | §13 全绿 |

合计约 **6.5 人日**。

---

## 13. 验收标准（硬指标）

发布前逐条打勾，**任何一条不过就不发**。

| 类别 | 指标 | 阈值 |
|---|---|---|
| 编译 | `dotnet build -c Release` | 0 error 0 warning |
| 视觉 | 单屏 100% / 双屏 100%+150% / 副屏在顶部 | 贴片字号、垂直居中、不越界，截图确认 |
| 视觉 | 面板 4 个时间窗切换 | 曲线连续，不出现整段空白 |
| 视觉 | 告警触发 | 对应数字变 `#FF453A`，5 秒后恢复原色 |
| 内存 | 单屏、历史开、告警开 | ≤ 46 MB（基线 41 + 历史 0.15 + 多缓冲） |
| 内存 | 历史总开关关闭 | ≤ 43 MB（回到 v0.04 基线） |
| 内存 | 三屏各一片贴片 | ≤ 50 MB |
| CPU | 1 Hz、历史开、告警开 | ≤ 0.5%（基线 0.3） |
| 磁盘 | 24 小时历史文件 | 3.4 MB ± 10% |
| 功能 | 关历史 → `history` 目录不存在 | 硬性 |
| 功能 | 告警抖动 20 次 | 只触发 1 次 |
| 功能 | 脚本超时 | 30 秒 kill，UI 不卡 |
| 回归 | 拖动 / 点击面板 / 托盘菜单 / 设置全项 | 与 v0.04 一致 |
| 回归 | 老 config.json 直接启动 | 不报错、不丢设置 |

---

## 14. 风险与坑

| 风险 | 影响 | 对策 |
|---|---|---|
| `DeviceDpi` 在 Blit 前不可靠 | 副屏字号错 | 用 `GetDpiForMonitor` 显式算 `Scale`（§1.3） |
| `WS_EX_TRANSPARENT` 与 `WS_EX_LAYERED` 叠加后点击丢失 | 面板打不开 | 托盘菜单保留入口 + 设置里给提示（§4.1） |
| 托盘闪烁与 `_trayKey` 冲突 | 图标卡在告警态 | `_flashing` 守卫，闪烁期间 `Apply` 跳过图标更新 |
| `AlertRules` 数组下标与枚举错位 | 改错阈值 | 只按 `Metric` 字段匹配，不按下标取 |
| 历史文件读到半条记录 | 崩溃 | 尾部不足 40 B 丢弃；magic 校验失败跳过文件 |
| 24h 视图打开时读 3.4 MB | 面板卡顿 15 ms | 只在打开/切窗时读一次，缓存结果到 `_seriesCache` |
| 设置窗口滚动 + 自绘控件 | 滚动残影 | 控件都是 `OptimizedDoubleBuffer`；`_scroll` 不设 `DoubleBuffered`（Panel 不支持），靠子控件自绘 |
| 多贴片各自一个窗口 | 内存 +1 MB/片 | 可接受；超过 3 片时提示用户改用「单屏 + 全部屏幕关闭」 |
| `FlowLayoutPanel` 在 AutoScroll 容器里出横向滚动条 | 视觉瑕疵 | 显式 `Width = ContentW - 20` |
| 脚本 fire-and-forget 后子进程泄漏 | 僵尸进程 | 超时 kill 整棵进程树（`Kill(true)`） |

---

## 15. 后续（不在本期）

- 网速分接口（有线 / 无线分别显示）
- Top N 进程（仅在面板打开时采样）
- 多机监控 agent（第二步）
- 历史导出 CSV / 截图分享
- 告警规则动态增删（等固定 5 条被用户吐槽再加）
