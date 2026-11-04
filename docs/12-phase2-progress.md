# 12 · 主面板阶段 2 施工进度（GPU 进程指标）

> **本文档是阶段 2 的交接页。** 任何 Agent / 对话中断后接手，先读本页 + `docs/10-main-dashboard-plan.md` + `docs/11-phase1-progress.md` + `AGENTS.md`。
> 规则：**每完成一个文件就更新本页的「施工状态」和「日志」**，不要攒到最后一起写。

- 方案总纲：`docs/10-main-dashboard-plan.md`（GPU 口径 §3.2、预算 §5、验收 §6）
- 阶段 1 交接：`docs/11-phase1-progress.md`（关键设计决策 §3 要沿用）
- 目标版本：v0.19
- 状态：**✅ 阶段 2 完成并已发布 v0.19（2026-11-04 01:20 前后）**

## 0. 当前状态速览（接手先看这里）

| 项 | 状态 |
|---|---|
| 编译 | ✅ 0 error 0 warning |
| 阶段 2 全部文件 | ✅ 已完成并发布（安装版 + framework + portable 三产物） |
| 下一步 | **阶段 3：应用识别与体验**（plan §7 阶段 3：图标缓存、窗口/前台识别增强、owner 判定、显示后台/系统进程/多卡筛选/最小占用阈值） |
| 已知问题 | 见 §5（5 条，均不阻塞） |

## 1. 阶段 2 范围（来自 plan §7 阶段 2）

1. 新增 `Metrics/GpuProcessSampler.cs`：独立 PDH query（不与全局采样器共享，plan §10.6/§10.7）。
2. 解析 `\GPU Engine(*)\Utilization Percentage` 与 `\GPU Process Memory(*)\Dedicated/Shared Usage` 实例名（`pid_N_luid_..._phys_N_eng_N_engtype_X`）。
3. 按 PID + LUID 聚合，利用率做 3 帧滑动平均。
4. `DashboardTable` 增加 GPU、显存列；`DashboardSort` 增加 Gpu/Vram。
5. `MainDashboardForm` GPU 视图从占位 Label 换成真表格（按 PID 列 GPU 活动进程）。
6. 诊断参数 `--dashboard-gpu-probe`；对照任务管理器验收（util ±8pp、专用显存 ≤128MB）。

## 2. 施工状态表

| 文件 | 状态 | 说明 |
|---|---|---|
| `docs/12-phase2-progress.md` | ✅ | 本页 |
| `Metrics/GpuProcessSampler.cs` | ✅ | 独立 query 三计数器；(pid\|luid) 聚合 + 3 帧滑动平均；grow-only 缓冲；>12ms×3 帧 → Overloaded 降级信号 |
| `AppConfig.cs` | ✅ | `DashboardSort` 加 Gpu=4/Vram=5 |
| `Metrics/AppMetricsHub.cs` | ✅ | ctor 收 gpuIntervalMs；0.5Hz 子采样 + Overloaded×2.5 降级；合入 ProcessMetric（进程退出 GPU 活动清零）；BuildApps 聚合 GPU 字段 |
| `UI/DashboardTable.cs` | ✅ | 6 列（GPU/显存），Row 加 4 字段，NameFrac 0.36、列宽 92/106/88/106 |
| `UI/MainDashboardForm.cs` | ✅ | GPU 视图真表格（util>0 或显存>0 的 PID；忽略显示后台过滤，保留搜索+系统过滤）、`_gpuNote` 移除、状态栏 GPU 段、MapCol/MapSort/Compare 加 GPU |
| `UI/DashboardWidgets.cs` | ✅ | DetailBar 行 3 补 GPU%（引擎）/显存（共享） |
| `Program.cs` | ✅ | `--dashboard-gpu-probe`（8 帧×2s 对照全局显存）；`--dashboard-probe` 行加 gpuProcs/gpuInstances/gpuMs |
| `WMonitor.csproj` | ✅ | 0.19.0 |
| 编译 + probe 验收 | ✅ | 见 §4 |
| 发布 v0.19 + changelog | ✅ | changelog v0.19 条目、plan 状态、AGENTS.md（目录树/诊断表/文档地图/版本号）均已更新；三产物发布（framework 436 KB / portable 66 MB / 安装版） |

## 3. 关键设计决策（施工中随时补）

1. **数据结构**：`GpuProcessMetric { Pid, Luid, UtilPercent, DedicatedBytes, SharedBytes, TopEngine }`。内部按 (pid, luid) 键做滑动平均，**快照输出按 PID 合并成一行**（多卡进程 sum util 后 clamp 100，Luid/TopEngine 取单卡 util 最高的适配器；显存本来就是按 pid 汇总）——GPU 视图一行 = 一个 PID，与任务管理器进程页签一致；多卡筛选阶段 3 再做。合并放在采样器内部，Hub 的 pid 索引键唯一。
2. **滑动平均**：每 (pid,luid) 键保留最近 3 帧原始 util，输出均值；引擎明细不平均，只保留当帧 util 最高的 engtype 原文。
3. **PDH 缓冲**：PdhGetFormattedCounterArray 走「先探 size 再分配」模式（同 MetricSampler.ReadArray），但缓冲区 grow-only 复用，减少 0.5Hz 下的分配。
4. **子采样调度在 Hub**：`AppMetricsHub` 持有 GpuProcessSampler，按 `DashboardGpuRefreshMs`（默认 2000ms）节流；两次 GPU 采样之间沿用上次数值（plan §3 推荐做法）。
5. **GPU 视图过滤**：忽略「显示后台」（GPU 计算进程通常无窗口），保留搜索 + 系统进程过滤；只显示 util > 0 或专用显存 > 0 的 PID。
6. （待补）

## 4. 验证结果（2026-11-04，本机 506 进程 / RTX 3060 / 64GB / Win11 26300）

| 验证项 | 结果 |
|---|---|
| `dotnet build -c Release` | ✅ 0 error 0 warning（首轮 15 错：GpuProcessSampler 漏 using WMonitor.Interop + span IndexOf 无 StringComparison 重载，改字符串 IndexOf 后修复） |
| `--dashboard-gpu-probe` 空载 | ✅ 52–53 个 GPU 进程、1048–1072 引擎实例；稳态 1.5–5.2 ms（首帧 ~10–11 ms 冷缓冲属预期）；专用显存逐 PID 与 typeperf 地面真值一致（dwm 4009MB / msedge 912MB） |
| 负载（ffmpeg d3d11va 4K60 硬解） | ✅ ffmpeg pid util 80.0%→77.7%（滑动平均）；Get-Counter 单实例明细 3D 55.4% + VideoDecode 21.9% = 77.3%，TopEngine=3d 归因正确；共享显存 560MB 正确 |
| 预算 ≤8ms | ✅ 稳态最高 5.2ms（负载 1072 实例时 4.46ms） |
| headless Edge WebGL | ⚠️ 走 SwiftShader 软渲染，打不到硬件 GPU（负载改用 ffmpeg d3d11va） |
| UI 截图（GPU 视图） | ✅ 负载下 ffmpeg 置顶 GPU 78.1% 绿色加粗、GPU 列排序三角、显存-only 行（util —）正常；按应用视图六列与 GPU 聚合正常（Ardot 273GB / Edge 920MB） |
| `--dashboard-probe` 全链路 | ✅ procs 491–492 / apps 256 / dupPids=0 / distinct=refDistinct；GPU 0.5Hz 节奏可见（gpuMs 两帧一换：9.13→2.30→1.94→1.91） |
| error.log | ✅ 无新异常（文件不存在） |

## 5. 已知问题 / 风险（随时补）

1. 阶段 1 遗留：私有内存残差 ~12MB、probe 偶发 5.5ms、交互未实测、受限进程 `PID n（受限）` —— 见 docs/11 §5，阶段 2 复验。
2. **驱动上报的异常显存值**：pid「Ardot」的 Dedicated Usage 原始值就是 ~290 GB（typeperf/Get-Counter 同值，任务管理器同源同怪）——非本代码 bug，如实显示。GPU 视图排序按显存降序时它会置顶。
3. **Σ(按进程专用显存) > 全局适配器专用显存**（去掉异常值后 9.6GB vs 6.1GB）：进程侧是「专用提交」（可超卖/溢出到共享），适配器侧是驻留量；任务管理器两个页签同样对不上。验收对照应逐 PID 比任务管理器进程页签，不比总和。
4. **LUID 大小写两族不一致**：GPU Engine 实例名小写（`0x000121a4`）、GPU Process Memory 大写（`0x000121A4`）。阶段 3 多卡筛选与 LockedGpuLuid 比较时必须 OrdinalIgnoreCase。
5. Overloaded 降级路径未实测（需要持续 >12ms 负载），仅代码审查。

## 6. 施工日志（新条目加在最上面）

- **2026-11-04 (7)** 阶段 2 收尾：UI 验证三连（GPU 视图空载/按应用六列/GPU 视图+硬解负载截图，`wmonitor-dash-win.png`）；`--dashboard-probe` 全链路（GPU 0.5Hz 节奏可见，阶段 1 不变量无回归）；error.log 无异常。发布三产物 v0.19 + changelog/plan/AGENTS.md 更新。测试期间杀过用户的 v0.18-portable 实例，结束时以 v0.19-portable 恢复托盘。遗留：交互验收与 1 小时稳定性复验（同阶段 1 §5.1/§5.3）。
- **2026-11-04 (6)** 编译修复 + probe 验收：首轮 15 错（漏 using / span IndexOf 重载）；0/0 后跑 `--dashboard-gpu-probe`：显存逐 PID 与 typeperf 一致（排除"×100"误判，pid 15180=Ardot 驱动上报 270GB 属 OS 数据本身）；Σ>全局是 WDDM 提交 vs 驻入口径差；probe 补 per-LUID 分组对照。负载验证：headless Edge 走软渲染失败 → ffmpeg d3d11va 4K60 硬解，util 80%→77.7% 滑动平均、TopEngine=3d 与 Get-Counter 明细（55.4+21.9）一致、共享显存 560MB、稳态 ≤5.2ms。known-issues +4 条（§5.2–5.5）。
- **2026-11-04 (5)** 首轮编译：15 错集中在 GpuProcessSampler（`using WMonitor.Interop` 漏 + ReadOnlySpan.Index 无 StringComparison 重载）→ 补 using、解析改回 string.IndexOf(start, comparison) 再切 span。编译 0/0。
- **2026-11-04 (4)** UI 层 + 接入层完成：DashboardTable 六列；MainDashboardForm GPU 视图真表格（按 PID、忽略显示后台过滤——GPU 计算进程通常无窗口、Key 复用 "p:" 前缀跨视图保选中）、`_gpuNote` 占位移除、Hub 构造传入 `DashboardGpuRefreshMs`、状态栏加 GPU 项数/耗时/降级提示；DetailBar 行 3 加 GPU%（引擎类型）/显存（共享）；`--dashboard-gpu-probe` + `--dashboard-probe` 扩展。踩坑：多卡进程同 PID 两行会在 Hub 字典里互相覆盖 → 把按 PID 合并下沉到采样器内部（sum util / 主适配器取单卡 util 最高）；probe 里写晕一行 shared 布尔表达式，顺手修掉。csproj → 0.19.0。
- **2026-11-04 (3)** AppMetricsHub 接入：ctor 收 gpuIntervalMs（clamp 500–30000）；Combine 内 0.5Hz 子采样（Environment.TickCount64 节流，两次之间沿用 `_gpuByPid` 旧值），Overloaded 时间隔 ×2.5（plan §5）；进程行合入 GPU 四字段，进程不再有 GPU 活动时清零（ProcessMetric 对象按 PID 跨帧复用）；BuildApps 聚合 GPU（TopEngine 取 util 最高成员）；Dispose 释放 _gpu。AppConfig：`DashboardSort` 加 Gpu/Vram（枚举按名序列化，追加成员向后兼容旧 config.json）。
- **2026-11-04 (2)** `Metrics/GpuProcessSampler.cs` 完成：独立 PDH query（GPU Engine Utilization + GPU Process Memory Dedicated/Shared），实例名 span 解析（pid / luid_phys / engtype），(pid|luid) 键聚合 + 3 帧滑动平均（U[3] 环形 + 有效帧数 N）；缓冲 grow-only 复用；引擎 top 用 TopVal 单独记最大项（不能拿累加和当比较基准——初稿踩过）；只有显存、无引擎活动的 PID 也出行；4 帧不出现清 stats；collect 失败保留旧值并标 Error；连续 3 帧 >12ms 置 Overloaded 降级信号。初稿 EnumerateItems 二次调 API 会破坏缓冲语义，重写为 ReadArray 存 _itemCount。
- **2026-11-04 (1)** 建立本进度页。前置阅读完成：plan §3.2/§5/§7、docs/11（阶段 1 决策与坑）、ProcessSampler / AppMetricsHub / MainDashboardForm / DashboardTable / DashboardWidgets / MetricSampler（PDH 与 LUID 口径）/ Native（已有 PDH P/Invoke，无需新增）/ Program（probe 模式）。设计定稿见 §3。
