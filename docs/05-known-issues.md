# 05 · 已知限制与踩坑记录

## 一、平台限制（无解，别再试）

### 1. Win11 没有 Deskband，无法真正嵌入任务栏

Win11 的 Explorer 任务栏是 XAML 树，不开放第三方插入，Deskband 接口已在 Win10 后期废弃、Win11 完全移除。
所有"任务栏显示"的软件（TrafficMonitor、NetSpeedMonitor 类）都是同一个做法：**置顶悬浮窗贴在任务栏上**。

本项目的贴片：`WS_POPUP` + `WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`，`ShowInTaskbar=false`，每 tick 重新定位。Explorer 重启后自动重贴。

### 2. 副屏任务栏：v0.10 已支持，但有前提

`Shell_TrayWnd` 是主屏任务栏；副屏是 `Shell_SecondaryTrayWnd`，**只有开启「在所有显示器上显示任务栏」才存在**。
关掉时枚举只会拿到主屏，配置里指向副屏的 `StripMonitor` 会自动回落主屏（`TaskbarLocator.Find`）。

另外 Win11 已经不允许把任务栏移到屏幕侧边，所以贴片只做横向布局；`TryAnchor` 仍用「短边 < 12」判断自动隐藏，
避免把竖向任务栏误判成隐藏。

### 3. 贴片不接收键盘

`WS_EX_NOACTIVATE` 意味着点它不抢焦点（这是刻意的：不能打断用户正在输入的窗口）。代价是没有键盘交互。

### 4. 无 N 卡时 GPU 利用率口径不同

AMD / Intel 走 PDH 的 `GPU Engine` 聚合，采样开销从 0.3 ms 升到约 2 ms，数值口径也更接近任务管理器（所有引擎最大值 vs SM）。

---

## 二、踩过的坑（按发现顺序）

### 1. `AdapterRAM` 是 32 位字段

**现象**：12 GB 的 RTX 3060 显示成 4 GB。
**原因**：`Win32_VideoController.AdapterRAM` 是 `uint32`，超过 4 GB 溢出。
**解法**：容量走 NVML `memory.total`，回落注册表 `HardwareInformation.qwMemorySize`。

### 2. 主题判定用错了注册表值

**现象**：系统是深色任务栏，贴片却是白底。
**原因**：读了 `AppsUseLightTheme`（应用模式）。任务栏跟随的是 `SystemUsesLightTheme`（Windows 模式），两者可以不一致。
**解法**：`Theme.SystemUsesLight()` 改读 `SystemUsesLightTheme`。

### 3. WinForms 控件 `BackColor` 不接受 alpha

**现象**：设置窗口一闪就没，日志里 `ArgumentException: Control does not support transparent background colors`。
**原因**：`Theme.PanelBg` 是 `rgba(...,242)`，直接赋给 `Form.BackColor`。
**解法**：新增 `Theme.PanelBgOpaque`（alpha 255）给控件用；分层窗口的自绘仍用半透明色。

### 4. `ToolStripSeparator` 强转 `ToolStripMenuItem` 崩溃

**现象**：右键托盘图标，菜单打不开，日志 `InvalidCastException`。
**原因**：`foreach (ToolStripMenuItem item in menu.Items)` 遇到分隔符就炸。
**解法**：遍历 `ToolStripItem` + `is not ToolStripMenuItem` 过滤。

### 5. 详情面板弹出即消失

**现象**：点贴片，面板闪一下就没了。
**原因**：面板 `Show()` + `Activate()` 后，点击事件的焦点回弹立刻触发 `Deactivate`。
**解法**：打开后 350 ms 内忽略 `Deactivate`（`_deafUntil`）。

### 6. 贴片用 `NOACTIVATE` 时面板无法自动关闭

**现象**：面板打开后点别处不收起。
**原因**：`WS_EX_NOACTIVATE` 的窗口永远不会激活，也就永远不会 `Deactivate`。
**解法**：`LayeredWindow.NoActivate` 做成属性 —— 贴片 `true`，面板 `false`。

### 7. 贴片窗口存在但内容透明

**现象**：任务栏上贴片位置是空的，但 `EnumWindows` 能看到窗口、`IsWindowVisible=1`、位置正确。
**原因**：绘制过程中抛异常，位图停在 `g.Clear(Transparent)` 的状态后被 Blit 上去。
**解法**：查 `%APPDATA%\WMonitor\error.log`；`Blit` 返回 bool，异常不再被吞。

### 8. `PdhCollectQueryData` 一次刷新全部计数器

**现象**：采样一次 8–16 ms，CPU 占用 2.6%。
**原因**：`\GPU Engine(*)` 有数百个实例（进程 × 引擎），挂在同一个 query 上，每次 collect 都要刷新全部。
**解法**：
- 有 NVML 时**根本不创建**这个计数器，GPU 利用率走 `nvmlDeviceGetUtilizationRates`；
- 没有 NVML 时把它挂在**独立的 query** 上，只在需要时 collect。
结果：采样降到 0.3–1 ms。

### 9. `GetAllNetworkInterfaces()` 每次 8–16 ms

**现象**：即使不读 GPU Engine，采样仍要 8 ms。
**原因**：枚举全部网卡（本机 20+ 个，含 VMware / vEthernet / Tailscale / WSL / 蓝牙），每个都要读属性。
**解法**：缓存过滤后的 `List<NetworkInterface>`，30 秒刷新一次；每 tick 只读 `GetIPStatistics()`。降到 0.2–0.7 ms。

### 10. 每秒重建托盘 Icon 会漏 GDI 句柄

**现象**：长时间运行后 GDI 对象数持续上涨。
**原因**：`Bitmap.GetHicon()` 创建的 HICON 必须手动 `DestroyIcon`；`NotifyIcon.Icon` 的旧对象必须 `Dispose`。
**解法**：`TrayIconRenderer.Render` 内部 `try/finally DestroyIcon`；`Apply` 里 `old?.Dispose()`。

### 11. 数字宽度抖动

**现象**：CPU 从 `9%` 变 `10%` 时，整个贴片左右抽动。
**原因**：按实际文字宽度排版。
**解法**：`ValueBox()` 用固定样本宽度（`"88"` / `"88.8"`），数值右对齐到盒内。

### 12. 单文件 exe 被自己锁住

**现象**：改完代码编译报 `MSB3027`，提示 `WMonitor.exe` 被 `WMonitor (pid)` 锁定。
**原因**：程序还在跑。
**解法**：构建脚本里先 `Stop-Process -Name WMonitor -Force` 再编译。

### 13. `Dbg.Log` 抽样把性能日志吃掉

**现象**：加了 `Dbg.Prof` 但日志里看不到。
**原因**：`Prof` 内部调用了带 1/3 抽样的 `Log`，40 条里只有 1/3 概率落盘。
**解法**：`Prof` 自己写文件，不走抽样。

### 14. 配置文件路径含单引号

本机用户名是 `v'h'soft`，`%APPDATA%` 展开后路径带两个单引号。
在 bash 里 `cat "$APPDATA/..."` 会失败，用 Python 的 `os.environ['APPDATA']` 或 PowerShell 才稳。

---

### 15. 点托盘箭头后贴片消失（v0.01 的 bug，v0.02 已修）

**现象**：点「显示隐藏的图标」箭头，任务栏上的贴片整个不见了。
**原因**：任务栏响应点击时把自己提到 TOPMOST 组最前，贴片（也是 TOPMOST）被压到下面；`WS_EX_NOACTIVATE` 窗口不会因为失焦重绘，所以一直不恢复。
**解法**：每 tick 调 `SetWindowPos(HWND_TOPMOST, SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE)`。
**关键顺序**：必须放在 `Blit` **之前**。放在之后会把 `UpdateLayeredWindow` 写入的分层内容清空 —— 表现为窗口存在、`PrintWindow` 有内容、alpha=255，但屏幕上完全透明。

### 16. 应用图标区没有可靠的 HWND

`MSTaskListWClass` 的矩形只有 440px（实际图标区约 600px），Win11 任务栏的 UIA 树通过 `Shell_TrayWnd` 访问返回 0 个元素。
**解法**：左界用 `Start` 窗口的 `Left`（可靠）；右界取「任务栏中心」与「托盘左界」的中点，本机实测误差约 30px，用 `OffsetX` 微调。

### 17. 换主题后控件前景色不跟着变（v0.03 已修）

**现象**：浅色主题下设置窗口标题是白字白底，看不见。
**原因**：`RecolorControl` 只更新了 `Label.BackColor`，`ForeColor` 还是构造时那个主题的值。
**解法**：给 Label 打 `Tag = "header" / "caption"`，换主题时按层级分别恢复 `_th.Text` / `_th.Label`。

### 18. 截图判断贴片是否可见要注意

贴片只有 30px 高，缩略到整条任务栏（2560×60 → 1280×30）后视觉上几乎看不出内容，容易误判成"消失"。验证时按贴片实际区域裁剪并放大 2–3 倍。

### 19. 历史文件读不出来（共享冲突）

**现象**：`--history-dump` 显示 `ring count=0`，面板 24 h 窗口全 0；但文件明明有数据。
**原因**：应用自己用 `FileShare.Read` 打开了今天文件的写入句柄，`File.ReadAllBytes`（默认 `FileShare.Read`）
再来打开会被共享冲突拒绝，异常被 `catch` 吞掉 → 看起来「没有历史」。
**解法**：读取一律走 `HistoryStore.ReadShared`（`FileShare.ReadWrite | Delete`）。

### 20. 自绘 `Stepper` 太窄会把数字挤成两行

**现象**：告警规则行里阈值显示成竖排的「9 / 0」。
**原因**：`Stepper` 两侧按钮各占 26 px，宽度 76 时留给数字只剩 24 px。
**解法**：阈值 Stepper ≥ 90 宽，持续秒数 Stepper ≥ 84 宽。

### 21. 告警规则数组不能按下标取

**现象**：改了 CPU 阈值，结果 GPU 规则变了。
**原因**：`AlertRules` 是 JSON 数组，用户手改或旧版本长度不足时，下标与 `AlertMetric` 不再对应。
**解法**：`AlertEngine.Configure` 与 `SettingsForm.NormalizeRules` 都按 `Metric` 字段归位，并补齐到 5 条。

---

## 三、当前已知小问题（不阻塞使用）

| 问题 | 影响 | 备注 |
|---|---|---|
| 浅色贴片 + 深色任务栏不协调 | 视觉 | 用户主动选「浅色」才有；默认跟随系统不会 |
| 托盘图标 16 px 下双数字略挤 | 视觉 | v0.04 已放大字号 + 4 倍超采样；仍偏挤是 16 px 的物理上限 |
| 「开机自动启动」胶囊未选中时像禁用态 | 视觉 | 功能正常，后续可换成 toggle 开关 |
| 设置窗口置顶 | 交互 | 关掉即消失，不影响其他窗口 |
| 未处理 `WM_DISPLAYCHANGE` | 边界 | 换分辨率后靠每 tick 重新定位自愈，最多 1 秒延迟 |

## 设置窗口切换托盘样式时闪白条

**现象**：设置窗口里把托盘样式从「双数字/双环」切到「四段条」，卡片变高时窗口跟着长高，
那一下会闪一条白色。

**排查**：60 fps 录屏（ffmpeg gdigrab）+ 逐帧统计亮像素，新旧版本都没抓到白块帧 ——
可能是只在特定刷新率/合成器下可见的一两帧。所以按"三个可能来源"全堵：

1. 窗口缩放本身 → **不再缩放**：托盘卡隐藏的行也计入高度，均摊到三行，四种样式窗口都是 1254×925。
2. 窗口类默认擦除刷是系统白 → 拦 `WM_ERASEBKGND` 用 `WindowBg` 擦，改完尺寸立刻 `Update()`。
3. 自绘控件（Segmented/Chip/Slider/Stepper）从隐藏变可见时先用默认白底刷一帧 →
   统一重写 `OnPaintBackground` 用 `Parent.BackColor` 擦。

**教训**：WinForms 里凡是自绘控件，都要自己重写 `OnPaintBackground`；
默认的 `BackColor` 刷（白）在控件首次显示/父窗口缩放时一定会露一次。
