# 01 · 架构

## 进程模型

单进程、单线程（UI 线程）+ 一个 `System.Windows.Forms.Timer`。
没有后台采样线程 —— 1 秒采样耗时 0.3–1 ms，放在 UI 线程不会造成可感知卡顿，反而省掉了跨线程同步与 `SynchronizationContext` 的坑（早期版本用 `System.Threading.Timer` + `Post`，出现过贴片不刷新的问题）。

```
Program.Main
  ├─ --probe        → 采样 8 次写文件后退出（诊断）
  ├─ Mutex          → Global\WMonitor.SingleInstance.v1，第二个实例直接退出
  └─ Application.Run(MonitorAppContext)
        │
        ├─ AppConfig.Load()            %APPDATA%\WMonitor\config.json
        ├─ MetricSampler               PDH + NVML + 网卡
        ├─ TaskbarStrip                分层窗口（贴片）
        ├─ DetailPanel                 分层窗口（面板，可激活）
        ├─ NotifyIcon + ContextMenuStrip
        └─ Forms.Timer(1s)
              Tick() → Sample() → Apply()
```

## 主循环

```
Tick()                                    // 每 1 秒（可配 0.5–5 秒）
  s = MetricSampler.Sample()              // 0.3–1.0 ms
  Apply(s)
    ├─ Theme 跟随系统时每 2 tick 复查一次
    ├─ TaskbarStrip.Update(s, cfg, theme) // 0.6–0.9 ms：测宽 → 绘制 → 定位 → Blit
    ├─ DetailPanel.PushHistory(s)         // 60 点环形缓冲
    ├─ DetailPanel.Update(...)            // 仅当面板可见
    ├─ 托盘图标：key = 样式|CPU|GPU|主题，变了才重建（约 0–1.7 ms）
    └─ 托盘 tooltip 文本
```

## 关键类

### `MonitorAppContext : ApplicationContext`

生命周期与胶水层。持有全部 UI 对象和配置。

| 成员 | 作用 |
|---|---|
| `Tick()` / `Apply(s)` | 主循环，见上 |
| `BuildMenu()` / `SyncMenu()` | 托盘右键菜单；`SyncMenu` 在 `Opening` 时同步勾选态 |
| `OpenPanel()` | 贴片点击：可见则关，否则 `OpenNear` |
| `OpenSettings()` | 模态打开设置；`Changed` 事件里即时应用 |
| `RefreshAll()` | 改配置后重设 Interval + 重贴 + 立即重绘 |
| `Quit()` | 停 timer、隐藏托盘、Dispose、保存配置 |

### `TaskbarStrip : LayeredWindow`

贴片的全部视觉逻辑。

| 方法 | 说明 |
|---|---|
| `Update(s, cfg, theme)` | 主入口：历史 → cells → 测宽 → 绘制 → 定位 → Blit |
| `BuildCells(...)` | 按 `cfg.ShowXxx` 生成 `Cell(Label, Value, Unit, Color, Pct, Bar)` 列表 |
| `MeasureRow(...)` / `DrawRow(...)` | 同一套推进逻辑，`MeasureRow` 只算宽度不画，保证测量与绘制完全一致 |
| `ValueBox(...)` | 数值的固定宽度盒（`"88"` / `"88.8"`），数字变化时不抖动 |
| `DrawRings(...)` | 四环模式（只取 `Bar == true` 的项） |
| `TryAnchor(...)` | 找任务栏矩形 → 算出贴片左上角 |
| `StripBounds` | 当前屏幕矩形，给面板定位用 |

**宽度计算基准**：`k = DeviceDpi / 96f`，所有尺寸都写成 `基准值 * k`。

**历史数据**：`_hCpu/_hRam/_hGpu/_hVram` 各 5 点，供迷你柱使用。

### `DetailPanel : LayeredWindow`

344 × 314，四行指标 + 曲线 + 底部温度/功耗/频率 + 齿轮/关闭按钮。

- `NoActivate = false`（必须可激活，否则 `Deactivate` 不触发）
- 打开后 350 ms 内忽略 `Deactivate`（避免焦点抖动导致秒关）
- `OpenNear()`：贴片上方 8 px，顶部放不下则翻到下方
- 点击齿轮 → `SettingsClicked` 事件 → 打开设置

### `MetricSampler`

见 [`02-data-sources.md`](02-data-sources.md)。要点：

- **主 PDH query**：CPU 占用、CPU 频率比、GPU Adapter Memory（3 个计数器）
- **独立 PDH query**：GPU Engine（仅在没有 NVML 时创建）
- NVML 动态加载，失败静默降级
- 网卡列表缓存 30 秒

### `LayeredWindow : Form`

- `CreateParams` 加 `WS_EX_LAYERED | WS_EX_TOOLWINDOW`（`NoActivate` 时再加 `WS_EX_NOACTIVATE`）
- `Blit(bitmap, x, y)`：`GetDC(0)` → 内存 DC → `bmp.GetHbitmap` → `UpdateLayeredWindow` → 全部释放
- 屏蔽 `WM_PAINT` / `WM_ERASEBKGND`（内容只由 `Blit` 提供）
- `Gfx` 静态类：圆角路径 + 字体缓存（避免每秒 new Font）

### `Theme`

深/浅两套只读配色。`SystemUsesLight()` 读的是 **`SystemUsesLightTheme`**（Windows 模式），不是 `AppsUseLightTheme`。

`PanelBgOpaque` 是给 WinForms 控件用的不透明版本（`BackColor` 不接受 alpha）。

### `SettingsForm`

普通 WinForms 窗体，控件全部自绘：

- `Segmented`：分段选择器（贴片风格 / 托盘图标 / 主题 / 刷新间隔）
- `Chip`：胶囊开关（显示项、开机自启）
- 深色标题栏：`DwmSetWindowAttribute(hwnd, 20/19, 1)`
- 所有修改即时生效并 `AppConfig.Save()`

## 定位算法

```
tray   = FindWindow("Shell_TrayWnd")
rect   = GetWindowRect(tray)
if rect.Height < 12: 视为自动隐藏 → 隐藏贴片
notify = FindWindowEx(tray, 0, "TrayNotifyWnd", 0)
right  = notify ? GetWindowRect(notify).Left : rect.Right - 180
x      = right - stripWidth - 6 + cfg.OffsetX
y      = rect.Top + (rect.Height - stripHeight) / 2
```

每 tick 都重新定位（开销可忽略），所以任务栏换位置、改高度、Explorer 重启都能自愈。

## 线程与资源

| 资源 | 生命周期 |
|---|---|
| PDH query × 1–2 | 进程级，Dispose 时关闭 |
| NVML 句柄 | 进程级，Dispose 时 `nvmlShutdown` + `FreeLibrary` |
| 贴片 Bitmap + Graphics | 复用，仅尺寸变化时重建 |
| 测量用 1×1 Graphics | 静态复用，永不释放 |
| Font | 静态缓存（family + px + style） |
| 托盘 Icon | 每次重建后 Dispose 旧对象，`GetHicon` 后 `DestroyIcon` |
