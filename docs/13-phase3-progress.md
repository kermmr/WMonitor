# 13 · 主面板阶段 3 施工进度（应用识别与体验）

> **本文档是阶段 3 的交接页。** 任何 Agent / 对话中断后接手，先读本页 + `docs/10-main-dashboard-plan.md` + `docs/12-phase2-progress.md` + `AGENTS.md`。
> 规则：**每完成一个文件就更新本页的「施工状态」和「日志」**，不要攒到最后一起写。

- 方案总纲：`docs/10-main-dashboard-plan.md`（图标口径 §3.4、预算 §5、验收 §6）
- 阶段 1 交接：`docs/11-phase1-progress.md`；阶段 2 交接：`docs/12-phase2-progress.md`（LUID 大小写两族坑 §5.4 必读）
- 目标版本：v0.20
- 状态：**✅ 阶段 3 完成并已发布 v0.20（2026-11-04 上午）**

## 0. 当前状态速览（接手先看这里）

| 项 | 状态 |
|---|---|
| 编译 | ✅ 0 error 0 warning |
| 阶段 3 全部文件 | ✅ 已完成并发布（安装版 + framework 453KB + portable 66MB 三产物） |
| 下一步 | **阶段 4：管控**（plan §7 阶段 4：二次确认、TerminateProcess/NtSuspend/NtResume/SetPriorityClass、dashboard-actions.log） |
| 已知问题 | 见 §5（5 条，均不阻塞） |

## 1. 阶段 3 范围（来自 plan §7 阶段 3）

1. 图标缓存（SHDefExtractIcon，`%APPDATA%\WMonitor\icon-cache\`，内存 LRU，失败负缓存）。
2. 文件版本信息缓存 —— **阶段 1 已完成**（`VersionInfoCache`，TTL 15s），本阶段不重做。
3. 窗口标题 / 可见主窗口 / 前台进程识别增强：按 WS_EX_APPWINDOW + 最大面积选主窗口，标题取主窗口。
4. 按应用聚合增强：代表进程评分加主窗口 / GPU；受限进程用 NtQuery 镜像名替换「PID n（受限）」占位，补 ParentPid。
5. 系统进程判定换 token owner SID（S-1-5-18/19/20、S-1-5-80-、S-1-5-96- 前缀），名称黑名单降级为兜底；顶栏补「系统进程」Chip。
6. 多卡筛选：GpuProcessSampler 输出 per-(pid,luid) 行；面板 LUID 下拉，仅多卡时显示；比较一律 **OrdinalIgnoreCase**。
7. 最小占用阈值：DashboardMinimumPercent 生效（CPU 与 GPU 都低于阈值才隐藏）。
8. 空态 / 受限态 / 加载态 / 错误提示完善（空态加过滤提示；GPU 不可用已有状态行）。

## 2. 施工状态表

| 文件 | 状态 | 说明 |
|---|---|---|
| `docs/13-phase3-progress.md` | ✅ | 本页 |
| `Interop/Native.cs` | ✅ | NtQuerySystemInformation / OpenProcessToken / GetTokenInformation / ConvertSidToStringSid / SHDefExtractIcon / DestroyIcon / GetWindow + GW_OWNER / WS_EX_APPWINDOW / EM_SETCUEBANNER（GetWindowRect 阶段 1 已有，勿重复声明） |
| `Metrics/ProcessSampler.cs` | ✅ | NtQuery 镜像名 + ParentPid + SessionId（首帧/真新 PID/60s 维护 + 500ms 节流）；token owner 一次性判定 + IsSystemServiceSid 前缀判定（SID ∨ SessionId==0）；受限进程不写「PID n」占位；WindowScanner 主窗口（GW_OWNER/APPWINDOW + 最大面积，标题取主窗） |
| `Metrics/AppMetricsHub.cs` | ✅ | IsSystem = IsSystemService ∨ 黑名单（无扩展名比较）；PickScore = 前台8 > 主窗4 > 有窗2 > CPU/GPU活跃1 |
| `Metrics/IconCache.cs` | ✅ | 新文件：磁盘 PNG（sha1+mtime）+ 内存 LRU 220 + .miss 负缓存 + 每 50ms 现抽上限 2 + 目录 512 文件清理 |
| `Metrics/GpuProcessSampler.cs` | ✅ | 快照加 Luids（per-(pid,luid) util/显存行），LUID 归一小写合流两族大小写；LuidBase 去 phys 段 |
| `AppConfig.cs` | ✅ | `DashboardGpuLuid`（适配器 LUID 前缀，OrdinalIgnoreCase） |
| `UI/DashboardTable.cs` | ✅ | 行首真图标（回退字母圆点 + 受限行蒙灰）；空态副提示 EmptyHint |
| `UI/DashboardWidgets.cs` | ✅ | 详情条 36px 图标 + 「系统进程」标记；SearchBox 占位符换 EM_SETCUEBANNER（修 v0.18 存量 bug） |
| `UI/MainDashboardForm.cs` | ✅ | 「系统进程」Chip、阈值段控（关/≥0.5/≥1/≥5）、多卡下拉（每 tick 按集合变化重建）、Pass() 加阈值、三视图 GPU 字段按所选卡重算、图标接线与 Dispose |
| `Program.cs` | ✅ | probe 加 sys/sysService/owner/parent/mainWin/limited/ntMs/gpuLuids 统计与 SID 样本；新增 `--dashboard-icon-probe`（跨预算窗口重试） |
| `WMonitor.csproj` | ✅ | 0.20.0 |
| 编译 + probe 验收 | ✅ | 见 §4 |
| 发布 v0.20 + changelog | ✅ | changelog v0.20 条目、plan 状态、AGENTS.md 均已更新；三产物发布（framework 453KB / portable 66MB / 安装版）；测试后以 v0.20-portable 恢复托盘 |

## 3. 关键设计决策（施工中随时补）

1. **受限进程命名走 NtQuerySystemInformation(SystemProcessInformation)**：镜像名对 PPL / 提权进程也可读（任务管理器同源），一次调用顺带拿到 ParentPid（阶段 1 注释里欠的账）。只解析 NextEntryOffset / ImageName / CreateTime / UniqueProcessId / InheritedFromUniqueProcessId 五个字段，用 Marshal.ReadInt* 按偏移读，不定义巨型 struct。缓冲 grow-only，STATUS_INFO_LENGTH_MISMATCH 重试（进程启停竞态）。刷新时机：常规 0.5Hz，本帧出现无名字的新 PID 时立即刷。
2. **系统进程判定用 token owner SID 的字符串前缀**，不调 LookupAccountSid（免本地化 / 免 LSAS 查询开销）：`S-1-5-18`（SYSTEM）、`S-1-5-19`（LOCAL SERVICE）、`S-1-5-20`（NETWORK SERVICE）、`S-1-5-80-`（每服务虚拟账户，svchost 组）、`S-1-5-96-`（DWM/UMFD 窗口管理器）。OpenProcessToken 在 QUERY_LIMITED 句柄上即可用（Vista+），每 Entry 一次。打不开 token 的（内核项 / PPL）回退名称黑名单。用户账户 S-1-5-21-*、UWP S-1-12-* 都不会误判。
3. **图标缓存分层**：内存 LRU（约 220 个 48px 位图，含负缓存项）→ 磁盘 `%APPDATA%\WMonitor\icon-cache\<sha1(小写路径)>-<mtimeTicks>.png`（mtime 进文件名，exe 更新自动失效；`.miss` 0 字节文件做失败负缓存）→ SHDefExtractIcon(48px) 现抽。绘制在 UI 线程 paint 里查，抽取每帧上限 2 个（超出回退字母圆点，下一帧再补），摊薄后 < 0.5ms/tick（plan §5）。目录超 512 文件时初始化删最旧。位图用 Graphics.DrawIcon 到透明 Bitmap（不赌 Icon.ToBitmap 的 alpha 行为）。
4. **主窗口识别**：EnumWindows 时按 PID 记「可见 + 非工具 + 未 cloak 的最大面积窗口」，标题取它（阶段 1 取 z 序首个会拿到子窗口标题）；WS_EX_APPWINDOW 或无 owner 判定 HasMainWindow，参与代表进程评分（前台 > 主窗口 > 有窗口 > CPU/GPU>0.5）。
5. **多卡筛选数据面**：GpuProcessSampler 快照加 `Luids` 列表（per-(pid,luid) 的 util 滑动平均 / 专用 / 共享显存），合并键内部一律小写（GPU Engine 实例小写、GPU Process Memory 大写，见 docs/12 §5.4）。面板从 Luids 派生去重 LUID 下拉（全部 / GPU N），选中后行内 GPU/显存字段改取该卡数值；单卡隐藏下拉。选中存 `DashboardGpuLuid`。
6. **最小占用阈值语义**：`DashboardMinimumPercent > 0` 时，CPU 和 GPU 都低于阈值才隐藏（有内存占用但零 CPU/GPU 的后台项会被过滤；任一活跃即显示）。GPU 视图同样生效。
7. **GetWindowRect 已在 Native.cs 存在**（任务栏定位用），阶段 3 只新增 GW_OWNER / WS_EX_APPWINDOW / GetWindow；补声明前先 grep，别重复声明（P/Invoke 重复声明不报错但会埋坑）。
8. **系统进程判定是三层，且 owner 层比预想弱**：非提权进程 OpenProcessToken 服务进程的 token 会被 DACL 拒绝（实测 334 个可读 token 全是本机用户 SID，服务进程一个都读不到 —— 任务管理器显示 SYSTEM 也是靠提权）。落地为：owner SID（S-1-5-18/19/20/80-/96-，覆盖提权场景与同用户判定）→ **NtQuery 的 SessionId==0**（免权限、免开销，服务全在会话 0，实测覆盖 156 个）→ 名称黑名单兜底。
9. **黑名单按无扩展名比较**：阶段 1 的隐患修掉 —— 名单存 "svchost"，进程名是 "svchost.exe"，`Contains` 从未命中（probe 显示 sys=4 只有 System/Registry 等无扩展名条目在生效）。Hub 新增 `NameWithoutExt`。
10. **NtQuery 按需刷新不周期刷**：一次全量 ~6ms（500+ 进程，probe 实测 ntMs），周期刷会把 1s 预算打爆。PID 的名字/父 PID/会话终身不变 → 触发条件 = 首帧 ∨ 真新建 PID（graveyard 复活不算）∨ 60s 维护；500ms 最小间隔防进程风暴连续刷。刷新帧 sampleMs ~9ms 一次性成本，稳态 3.1–3.4ms ✓。
11. **搜索框占位符 v0.18 起就不可见（阶段 3 截图验收发现）**：父控件 OnPaint 画的占位符被内嵌 TextBox 的不透明 CardBg 底色盖住，只露 x=30..32 的 2px 边（CJK 首字抗锯齿切片看起来像一串竖点）。修复 = 换 EM_SETCUEBANNER 由系统在 TextBox 内画（Native.SendMessage）；DrawToBitmap/WM_PRINT 路径不渲染 cue banner，验证必须用真窗口截图。
12. **图标抽取限速**：每 50ms 窗口最多现抽 2 个（SHDefExtractIcon 实测 1–20ms/个，explorer 77ms 首抽），超出预算返回 null 不缓存、下一帧再试 —— 面板打开后图标在 ~几百 ms 内渐进补齐。磁盘命中不受预算限制（读 PNG ~0.2ms）。
13. （待补）

## 4. 验证结果（2026-11-04，本机 498–512 进程 / RTX 3060 / 64GB / Win11 26300）

| 验证项 | 结果 |
|---|---|
| `dotnet build -c Release` | ✅ 0 error 0 warning（首轮 1 错：插值内三元要加括号；1 警 CS8602 graveyard 复活判空补上） |
| `--dashboard-probe` | ✅ procs≈505 apps≈156 dupPids=0；稳态采样 3.1–3.4ms（预算 ≤4 ✓，首帧 ~50ms 冷启动 + 刷新帧 ~9ms 一次性）；**受限进程 166/166 全部有真名**（winlogon.exe / services.exe 直接进列表，docs/11 §5.4 关闭）；parentKnown≈504（ParentPid 补齐）；sys=171–172 / sysService=156–157（svchost/audiodg 等全部命中）；gpuLuids=3 |
| `--dashboard-icon-probe` | ✅ 16 路径 12 OK / 4 MISS（MISS 是真无图标资源：svchost/sihost/SNAPOS64/WindowsApps 服务 exe）；首抽 0.2–17.8ms（explorer 77ms/2 次含预算等待）；LRU 命中 0.00–0.03ms；磁盘 PNG 跨运行复用；负缓存 .miss 落盘后 0.0ms 返回 |
| UI 截图（按应用默认视图） | ✅ 真图标（ZCode/Codex/网易云/Ardot/Hermes/explorer/Weixin 等）、系统进程 Chip、阈值段控、多卡下拉（全部显卡/GPU 1 0eab/GPU 2 214a/GPU 3 21a4）、搜索占位符「搜索应用 / 进程 / PID / 路径」完整可见、GPU 活动行（ZCode 1.4% / Codex 2.3% 绿色加粗）、状态栏采样 3.9ms |
| UI 截图（进程视图 + 系统 + 后台 + 阈值≥1） | ✅ 列头「进程」、Chip 勾选高亮、阈值段控 ≥1 高亮且只留 CPU/GPU≥1 的行、**dwm.exe（受限）有真名 + 「路径不可读」**、无图标行回退字母圆点（D/M） |
| UI 截图（GPU 视图多卡筛选） | ✅ 筛 GPU 3（21a4，零进程虚拟适配器）→ 空态 + 「切回全部显卡」提示；筛 GPU 1（0eab，活动卡）→ 只列该卡 GPU 进程（ZCode 3.9% / explorer / Ardot×2 / wallpaper64…）；config 记忆选中恢复 |
| 交互验收 | ⚠️ 未做（不模拟鼠标）：行选中→详情条、列头点击、Chip 点击切换、阈值段控点击。沿用阶段 1/2 的 config 驱动验证方式 |
| error.log | ✅ 无新异常（文件不存在） |
| 环境异常 | ⚠️ 验收中途桌面分辨率变为 640×480（外部原因，非本代码），窗口被系统钳制变 760 宽导致布局挤压 —— 清 DashboardWindowRect 后以默认 1280×800 重验通过 |

## 5. 已知问题 / 风险（随时补）

1. 阶段 1/2 遗留：面板关闭私有内存残差 ~12MB、1 小时稳定性未测、交互未实测、Overloaded 降级未实测 —— 见 docs/11 §5 / docs/12 §5。
2. **owner SID 层对服务进程无效**（非提权被 DACL 拒）：SessionId==0 与黑名单兜底覆盖；若未来支持提权重启（plan §3.5 runas），owner 层会自动补位。
3. GPU 视图按卡筛选时 TopEngine 仍是合并行（util 最高的卡）的引擎类型，非所选卡的引擎 —— 可接受（详情条参考信息）。
4. 图标 MISS 的进程（svchost 等无图标资源）回退首字母圆点，属预期降级。
5. cue banner 提示文字颜色由系统画（浅灰），深浅主题都可读但不可定制；若未来要自定义颜色需回到自绘 + 透明 TextBox 方案。
6. （待补）

## 6. 施工日志（新条目加在最上面）

- **2026-11-04 (5)** 阶段 3 收尾：config 驱动四截图（默认视图 / 进程+系统+后台+阈值≥1 / GPU 筛空卡 / GPU 筛活动卡 0eab）。确认 21a4/214a 是零进程适配器（gpu-probe 实测全部活动在 0eAB，Ardot 279GB 驱动异常值也在此卡 —— docs/12 §5.2 口径一致），筛空表现正确。发布三产物 v0.20 + changelog/plan/AGENTS.md 更新；以 v0.20-portable 恢复托盘（error.log 无异常）。遗留：交互验收与 1 小时稳定性复验（同阶段 1/2）。
- **2026-11-04 (4)** UI 截图验收 + 修搜索框占位符隐藏 bug：默认视图截图发现占位符不可见（只有「搜」字 2px 抗锯齿竖点切片）→ sbtest 独立工程复现 → 变体矩阵定位 = 内嵌 TextBox 不透明底色盖住父控件绘制的占位符（v0.18 起的存量 bug）→ 换 EM_SETCUEBANNER 原生提示修复（DrawToBitmap 验证不了它，cue 不走 WM_PRINT，真窗口截图确认 ✓）。截图确认：真图标 / 系统 Chip / 阈值段控 / 三卡下拉全就位。环境插曲：桌面分辨率中途变 640×480（外部因素），清 DashboardWindowRect 重验。
- **2026-11-04 (3)** probe 三轮修正：① token owner 探长度写法错（GetTokenInformation 首次返回 false+ReturnLength>0 是正常路径，首轮 ownerKnown=0）→ 修后 334 个可读，但全是同用户 SID，服务进程 token 被 DACL 拒 → 判定链加 SessionId==0（NtQuery +0x64）+ 黑名单无扩展名比较 → sys=171 ✓；② NtQuery 从 2s 周期刷改按需刷（首帧/真新 PID/60s 维护 + 500ms 节流），graveyard 复活不再计新 PID → 稳态 3.1–3.4ms（此前刷新帧 9.2ms 超预算）；③ CS8602 复活判空。icon-probe 补「跨 50ms 预算窗口重试」后 12 OK / 4 真 MISS。
- **2026-11-04 (2)** 代码全量完成：Native(+NtQuery/token/SID/SHDefExtractIcon/cue banner)、ProcessSampler(NtQuery 名字+ParentPid+SessionId、token owner、主窗口扫描)、AppMetricsHub(IsSystem 三层、PickScore v2)、IconCache(新)、GpuProcessSampler(Luids per-(pid,luid))、AppConfig(DashboardGpuLuid)、DashboardTable(真图标+空态提示)、DashboardWidgets(详情图标+系统标记)、MainDashboardForm(系统 Chip/阈值段控/多卡下拉/接线)、Program(probe 扩展+--dashboard-icon-probe)、csproj 0.20.0。编译首轮 1 错（插值三元括号）即 0/0。
- **2026-11-04 (1)** 建立本进度页。前置阅读完成：plan §2.2/§3.4/§5/§7 阶段 3、docs/11/12、ProcessSampler/AppMetricsHub/VersionInfoCache/GpuProcessSampler/MetricSampler(LUID 口径)/MainDashboardForm/DashboardTable/DashboardWidgets/AppConfig/Program/Native/MonitorAppContext(OpenDashboard)。范围定稿与设计决策见 §1/§3。确认阶段 1 的 VersionInfoCache 已覆盖「文件版本信息缓存」，不重做。
