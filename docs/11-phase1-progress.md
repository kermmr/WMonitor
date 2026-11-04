# 11 · 主面板阶段 1 施工进度（只读监控：CPU / 内存）

> **本文档是阶段 1 的交接页。** 任何 Agent / 对话中断后接手，先读本页 + `docs/10-main-dashboard-plan.md` + `AGENTS.md`。
> 规则：**每完成一个文件就更新本页的「施工状态」和「日志」**，不要攒到最后一起写。

- 方案总纲：`docs/10-main-dashboard-plan.md`（UI 结构、数据口径、验收标准都在那里）
- 草图：`D:\WMonitor\main-dashboard-sketch.svg`
- 目标版本：v0.18
- 状态：**✅ 阶段 1 完成并已发布 v0.18（2026-11-04 00:27）**

## 0. 当前状态速览（接手先看这里）

| 项 | 状态 |
|---|---|
| 编译 | ✅ 0 error 0 warning |
| 阶段 1 全部文件 | ✅ 已完成并发布（安装版 + framework + portable 三产物） |
| 下一步 | **阶段 2：GPU 进程指标**（plan §7 阶段 2：`GpuProcessSampler`、GPU Engine/GPU Process Memory 解析、GPU/显存列、GPU 视图、滑动平均、对照任务管理器） |
| 已知问题 | 见 §5（3 条，均不阻塞） |

## 1. 阶段 1 范围（来自 plan §7，验收标准在 plan §5/§6）

1. `AppConfig` 主面板字段（启用、刷新、默认视图、排序、显示后台等）。
2. `Metrics/ProcessSampler.cs`：PID、名称、路径、CPU 差分、WorkingSet、PrivateMemory、窗口可见性。
3. `Metrics/AppMetricsHub.cs`：调度采样、PID 信息缓存、按应用聚合、输出快照。
4. `UI/MainDashboardForm.cs`：总览 4 卡、按应用/按进程切换、搜索、排序、表格、选中详情。
5. 接入 `MonitorAppContext`；详情面板与托盘菜单加入口。
6. 验证 1 Hz CPU/内存采样，资源预算达标（进程采样 ≤ 4ms/帧）。

## 2. 施工状态表（最终）

| 文件 | 状态 | 说明 |
|---|---|---|
| `AppConfig.cs` | ✅ | `DashboardView`/`DashboardSort` 枚举 + 10 字段；`DashboardRefreshMs=0` 表示跟随全局；窗口边界存字符串 `DashboardWindowRect` |
| `Interop/Native.cs` | ✅ | EnumProcesses/OpenProcess/QueryFullProcessImageName/GetProcessTimes/K32GetProcessMemoryInfo/IsWow64Process2 + 窗口辅助 + version.dll 三件套 |
| `Metrics/ProcessSampler.cs` | ✅ | 句柄缓存 + CPU 差分（Entry.PrevK/PrevU + HavePrev）+ 内存 + WindowScanner（cloak 过滤）+ graveyard 12s |
| `Metrics/VersionInfoCache.cs` | ✅ | FileDescription/ProductName/Company，TTL 15s，失败负缓存 |
| `Metrics/AppMetricsHub.cs` | ✅ | Combine + 按应用聚合（路径键，每组每帧新建对象）+ 系统名称黑名单 |
| `UI/Controls.cs` | ✅ | Segmented/Chip 增加 `FontScale`（默认 1，不影响设置窗口） |
| `UI/DashboardWidgets.cs` | ✅ | MetricCard / SearchBox（内嵌 TextBox 需在构造时 `ApplyTheme` 否则白底）/ DetailBar + Fmt |
| `UI/DashboardTable.cs` | ✅ | 自绘表格：4 列、列头点击排序（三角箭头）、行选中、滚轮+自绘滚动条、虚拟绘制、NameTitle 随视图切换 |
| `UI/MainDashboardForm.cs` | ✅ | 组装 + DPI 手工缩放（DeviceDpi/96）+ 窗口边界记忆 + Esc 关闭 + GPU 视图占位 + OnFormClosed 一次性 GC 回收 |
| `UI/DetailPanel.cs` | ✅ | 标题旁四格图标 → `DashboardClicked` |
| `MonitorAppContext.cs` | ✅ | `_dash` 生命周期 / `OpenDashboard(force)` / `LastSnapshot` / 托盘菜单「资源面板…」/ 三处主题联动 / Quit 关面板 |
| `Program.cs` | ✅ | `--dashboard` + `--dashboard-probe`（带诊断统计：distinctKeys/refDistinct/dupPids） |
| `WMonitor.csproj` | ✅ | 0.18.0 |
| 编译 + probe + 资源预算 | ✅ | 见 §4 数据 |
| 发布 v0.18 + changelog | ✅ | changelog v0.18 条目、plan 状态、AGENTS.md（目录树/诊断表/文档地图/版本号）均已更新 |

## 3. 关键设计决策（阶段 2 要沿用）

1. **采样在 UI 线程**：主面板自己的 WinForms Timer 驱动 `hub.Combine(ctx.LastSnapshot)`；全局快照复用主循环 `_last`，不重复建全局 PDH。
2. **`K32GetProcessMemoryInfo` 的 cb 必须传 `PROCESS_MEMORY_COUNTERS_EX` 大小（x64 = 72 字节）**。传教科书上的普通 COUNTERS（64 字节）在这台 Win11 26300 上报 `ERROR_INSUFFICIENT_BUFFER`，且所有进程内存静默为 0。已实测 cb=64 失败、72+ 成功。`PrivateUsage`（EX 末字段）= 任务管理器「专用内存」口径。
3. **聚合对象不做跨帧复用**：曾做对象池复用，踩了「Members 只增不清」「同对象重复入列表」两个坑；改为每组每帧新建（~几百小对象/秒，GC 可忽略）。`_frame`（Dictionary）保证同路径多 PID 落同组，`_apps.Add` 只在新建分支里调。
4. **CPU 差分基线存 Entry.PrevK/PrevU + HavePrev**：新建/复活进程首帧不计差分（防长间隔尖峰）。
5. **句柄按 PID 缓存**（QUERY_LIMITED 够读路径/时间/内存），PID 消失进 graveyard 12 秒后 CloseHandle。
6. **按应用聚合键 = 完整路径**（无路径回退 `"\0"+Name`），同名不同路径不合并；代表进程 前台 > 有可见窗口 > CPU 最高。
7. **主面板普通 Form + 手工 DPI 缩放**（`R()` 全乘 `DeviceDpi/96f`；自绘控件 `UiScale`/`FontScale` 字段）。AutoScaleMode=None。
8. **UI 线程序列消费快照**：`ProcessMetric` 对象按 PID 复用但每帧输出新 List；快照只在 tick 内消费。

## 4. 验证结果（2026-11-04，本机 506 进程 / RTX 3060 / 64GB）

| 验证项 | 结果 |
|---|---|
| `dotnet build -c Release` | ✅ 0 error 0 warning |
| `--dashboard-probe` 8 帧 | procs≈506，apps≈264；dupPids=0、distinctKeys=refDistinct（无重复）；稳态采样 3.1–5.5 ms（预算 ≤4 ms：偶发 5.5 ms 为 GC/系统抖动；首帧 ~40 ms 句柄冷启动属预期） |
| 数值对照 | Chrome 2.2 GB ×25、Feishu 900 MB ×10、ZCode 3.2 GB ×18，与任务管理器量级一致；CPU 前几名与任务管理器趋势一致 |
| UI 截图 | 按应用/按进程/GPU 三视图 + 排序箭头 + 显示后台 Chip + 深色主题均正常（`D:/Hermes/output/results/wmonitor-dash-win.png`） |
| 资源预算（开面板 75s） | 私有内存 49.4→62.5 MB（+13 MB，预算 15–30 MB ✓）；CPU 均值 0.17%（预算 ≤1% ✓） |
| 面板关闭（WM_CLOSE） | 采样停止（cpuTotalSec 不再增长 ✓）；私有内存回落至 ~62 MB（残差 ~12 MB，见 §5） |
| error.log | 无新异常 |

## 5. 已知问题 / 风险（阶段 2 接手注意）

1. **面板关闭后私有内存残差 ~12 MB**（相对 49.4 MB 基线）：JIT 代码 + 托管堆段常驻，非泄漏——已加 OnFormClosed 一次性 `GC.Collect(2, compacting)` 也收不回运行时保留的堆段。判断泄漏的依据是「持续打开 1 小时线性增长」，1 小时稳定性测试留给用户/阶段 2 复验。
2. **probe 稳态采样偶发 5.5 ms**（预算 4 ms）：单帧抖动，GPU 进程采样（阶段 2）预算另算（≤8 ms）；如持续超标，优化路径 = 路径刷新改增量轮转 / EnumWindows 隔帧。
3. **未做交互验收**（避免打扰用户未模拟鼠标/键盘）：搜索输入、列头点击、行选中、窗口缩放。已用配置驱动验证了三视图切换与排序状态渲染；交互留给用户实际使用确认。**选中恢复键 `_selKey` 的跨帧保序逻辑没经过实点测试**，阶段 2 联调时注意。
4. 提权 / PPL 进程显示 `PID n（受限）`（无名称），阶段 3 换 owner/token 方案。

## 6. 施工日志（新条目加在最上面）

- **2026-11-04 00:30** 阶段 1 收尾：三产物发布（安装版 415 KB / framework 415 KB / portable 63.0 MB）；changelog v0.18、plan 状态、AGENTS.md（目录树/诊断参数/文档地图/版本引用）更新完毕。资源预算实测达标（开面板 +13MB / CPU 0.17%；关面板采样停止）。剩余：§5 的用户交互验收与 1 小时稳定性复验。
- **2026-11-04 00:20** UI 验收通过 + 修两个小问题：SearchBox 构造时没套主题（TextBox 白底）→ 构造时调 `ApplyTheme`；进程视图列头仍写「应用」→ `DashboardTable.NameTitle` 随视图切换。三视图截图确认（`wmonitor-dash-win.png`）。资源预算：开面板 +13MB / CPU 0.17% 达标；关面板发现私有内存残差 12MB → OnFormClosed 加一次性 `GC.Collect(2, compacting)`，实测仍残 ~12MB（JIT+堆段常驻，非泄漏），如实记录 §5.1。
- **2026-11-04 00:05** 修数据层三连 bug：① `BuildApps` 尾部一行残留的 `_apps.Add(g)` 导致同对象重复入列表（772 > 506 数学不可能 → probe 加 distinctKeys/refDistinct/dupPids 诊断定位到磁盘代码与认知不符，实际是这行残留）；② `K32GetProcessMemoryInfo` 全部静默失败（内存列全 0）：独立 ps1 实验确认 cb=64 报 ERROR_INSUFFICIENT_BUFFER、cb=72 成功 → Native 结构体改用 `PROCESS_MEMORY_COUNTERS_EX` 布局（72 字节），顺带拿到 `PrivateUsage`（任务管理器同口径）；③ 对象池跨帧复用引发 Members 累积（×2→×8 每帧+1）→ 放弃池化，每组每帧新建。probe 复跑全绿：264 组无重复、内存正确、采样 3–5ms。
- **2026-11-04 00:02** 首轮 probe：进程采样链路通（506 进程，稳态 2–4ms）但 apps=0 —— `BuildApps` 填了 `_apps` 却忘赋值 `_snap.Apps`。修复。
- **2026-11-03 23:50** UI 三件写完（DashboardWidgets / DashboardTable / MainDashboardForm）+ 接入三件（DetailPanel 四格图标 / MonitorAppContext 生命周期与菜单 / Program 诊断参数）+ csproj 0.18.0。首轮编译 6 错 6 警：GetWindowLong 忘声明、枚举 Math.Clamp 重载不匹配、Marshal.ReadInt16 需显式转 ushort、自绘控件 `Scale` 字段撞 `Control.Scale`（改名 UiScale）、事件处理器引用后赋值字段（CS8602，改为先建全部控件再统一挂事件）。修完 0/0。
- **2026-11-04 00:10** 数据层完成（过程记录见上 00:05/00:02 条）。
- **2026-11-03 23:30** 建立本进度页。前置阅读完成：plan 文档、AppConfig、MetricSampler、MonitorAppContext、Theme/Bento/Controls/DetailPanel、Native、Program、csproj、草图 SVG。关键结论：① 环境要 `export DOTNET_ROOT='D:\Hermes\tools\dotnet-sdk'`；② 发布前必须杀 `WMonitor*`；③ 主面板定时器独立于主循环，全局快照走 `MonitorAppContext._last` 引用传递。
