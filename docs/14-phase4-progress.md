# 14 · 主面板阶段 4 施工进度（管控）

> **本文档是阶段 4 的交接页。** 任何 Agent / 对话中断后接手，先读本页 + `docs/10-main-dashboard-plan.md`（§7 阶段 4 + 安全规则）+ `docs/13-phase3-progress.md` + `AGENTS.md`。
> 规则：**每完成一个文件就更新本页的「施工状态」和「日志」**，不要攒到最后一起写。

- 方案总纲：`docs/10-main-dashboard-plan.md`（管控范围 §7 阶段 4、安全规则同节、详情区 §2.4）
- 阶段 1/2/3 交接：`docs/11` / `docs/12`（LUID 坑 §5.4）/ `docs/13`（NtQuery / owner SID / 图标缓存十三决策）
- 目标版本：v0.23
- 状态：**✅ 阶段 4 完成并已发布 v0.23（2026-11-04，含复验修复与 UI 细节修复）—— 主面板四阶段全部收官**

## 0. 当前状态速览（接手先看这里）

| 项 | 状态 |
|---|---|
| 编译 | ✅ 0 error 0 warning |
| 阶段 4 全部文件 | ✅ 已完成并发布（安装版 + framework 481KB + portable 66MB 三产物） |
| 下一步 | 主面板 roadmap 已收官；遗留复验项见 §5（交互实测 / 1 小时稳定性 / Overloaded 降级，均为阶段 1/2 传承） |
| 已知问题 | 见 §5 |

## 1. 阶段 4 范围（来自 plan §7 阶段 4 + 安全规则）

1. 只对用户确认后的目标进程执行（全部动作二次确认，plan §2.1「全部要求二次确认」）。
2. 操作前显示名称、PID、路径、影响范围。
3. 按应用聚合行默认只作用于主 PID，不批量杀全部成员；需要批量时明确提示数量（对话框勾选）。
4. `TerminateProcess` 结束进程（+ 等待句柄置信号确认退出）。
5. `NtSuspendProcess` / `NtResumeProcess` 挂起 / 恢复。
6. `SetPriorityClass` 优先级（空闲 / 低于标准 / 标准 / 高于标准 / 高；不提供实时）。
7. 每次操作写 `%APPDATA%\WMonitor\dashboard-actions.log`。
8. 失败按错误码给中文原因：权限不足、已退出、句柄失效、需要管理员（plan §7.8）。
9. 详情区动作按钮：打开文件位置（不属管控，不确认不写日志）、优先级、挂起/恢复、结束任务。
10. 安全规则：系统进程按钮禁用（含关闭时系统 Chip 的兜底）；受限/提权进程可尝试但不静默失败；关键进程（explorer 等）对话框特别提示；结束任务必须勾选确认。

## 2. 施工状态表

| 文件 | 状态 | 说明 |
|---|---|---|
| `docs/14-phase4-progress.md` | 🚧 | 本页 |
| `Interop/Native.cs` | ✅ | TerminateProcess / WaitForSingleObject / NtSuspend/ResumeProcess / Get+SetPriorityClass / SYNCHRONIZE / 五档优先级常量 |
| `Metrics/ProcessActions.cs` | ✅ | 新文件：四动作 + Win32/NTSTATUS 中文错误映射 + IsSuspended（线程级）+ LogAnd actions.log（失败也留痕） |
| `UI/Bento.cs` | ✅ | BentoButton 加 PrimaryColor（Body/焦点环/投影三处统一 AccentColor） |
| `UI/DashboardWidgets.cs` | ✅ | DetailBar 四按钮（打开位置/优先级/挂起·恢复/结束任务红钮）+ SetTheme + 系统进程禁用 + meta「已挂起」+ 移除「只读模式」注 + 文字行给按钮区让宽 |
| `UI/ActionConfirmDialog.cs` | ✅ | 新文件：二次确认对话框（确认框禁用按钮、失败回显不关窗、批量勾选、优先级五档段控、关键进程红字风险）+ ConfirmCheck + MessageDialog |
| `UI/MainDashboardForm.cs` | ✅ | OnDetailAction 目标快照 + ShowDialog 重试环 + ExecuteTerminate 批量逐 PID + 挂起 TTL 缓存 + OpenLocation + MessageDialog |
| `Program.cs` | ✅ | `--dashboard-action-probe`（无 UI 端到端）+ `--dashboard-action-ui-probe`（对话框截图/真路径执行） |
| `WMonitor.csproj` | ✅ | 0.23.0 |
| 编译 + probe 验收 | ✅ | 见 §4 |
| 发布 v0.23 + changelog | ✅ | changelog v0.23 条目、plan 状态行、AGENTS.md/README 版本引用更新；三产物发布 |

## 3. 关键设计决策（施工中随时补）

1. **管控层独立成 `Metrics/ProcessActions.cs`**（静态类）：UI 只给它 PID/参数，它返回 (Ok, Error中文)。动作实现全走 P/Invoke：TerminateProcess（kernel32）、NtSuspendProcess/NtResumeProcess（ntdll，直接 DllImport，与 NtQuerySystemInformation 同待遇）、Get/SetPriorityClass（kernel32）。句柄按需最小权限开：终止 PROCESS_TERMINATE|SYNCHRONIZE，挂起恢复 PROCESS_SUSPEND_RESUME，优先级 PROCESS_SET_INFORMATION，读优先级 PROCESS_QUERY_LIMITED_INFORMATION 即可（Vista+）。
2. **错误映射两张表**：Win32（OpenProcess/Terminate/SetPriority 失败取 Marshal.GetLastWin32Error）与 NTSTATUS（NtSuspend/Resume 返回值）。关键映射：5→权限不足（受保护进程或需要管理员）、6→句柄失效、87→进程已退出或 PID 无效（Win11 上 OpenProcess 对不存在 PID 报 87）、0xC0000022→权限不足。TerminateProcess 是异步的，成功后 WaitForSingleObject(3s) 等句柄置信号再报成功。
3. **优先级只降不升的口径放宽为「五档可选、不含实时」**：plan 只要求「降优先级」，但只给一档会让误降的用户没有回头路；实时档需要 SeIncreaseBasePriorityPrivilege 且可冻结系统，不提供。对话框内 Segmented 五选一，默认当前优先级（GetPriorityClass 读出）。
4. **挂起状态检测用 System.Diagnostics.Process.Threads**（全部线程 WaitReason==Suspended 才算挂起），只对**选中行**调用 + TTL 2s 缓存 + 动作后强制刷新 —— NtQuery 的线程数组偏移版本相关（docs/13 §3.1 只敢读前 0x68 字节），不走那条路。0.5Hz × 单进程 ~1-3ms，不破采样预算。
5. **二次确认对话框是独立小 Form**（ActionConfirmDialog）：无边框自绘卡 + 主题跟随，内容 = 动作标题 / 目标（名称·PID·路径）/ 影响范围 / 关键进程风险行（红）/ 优先级五档段控（仅优先级动作）/ 批量勾选（仅应用行结束任务，明确显示成员数）/ 必勾确认框 / 取消+确认执行。结束任务的确认按钮在勾选前禁用（plan 安全规则 4「不要只用 OK」）。失败时不关对话框：错误显示在对话框顶部红字，可重试或取消。
6. **批量作用域默认最小**：应用聚合行（MemberCount>1）结束任务时，默认只结束主 PID；「同时结束全部 N 个成员进程」需显式勾选，勾选后按钮文字同步显示数量（plan §7 阶段 4.3）。
7. **系统进程入口禁用是双保险**：DetailBar 按钮按行 System 标志 Enabled=false（浅色下 40% 文字），MainDashboardForm.OnDetailAction 再拦一次（防状态不同步的边缘）。打开文件位置不属管控（只是 explorer /select），系统进程也放行。
8. **关键进程风险名单**（可操作但特别提示）：explorer、sihost、taskhostw、textinputhost、searchhost、startmenuexperiencehost、runtimebroker、shellhost。dwm/winlogon/svchost 等已在系统名单里，入口直接禁用。
9. **actions.log 行格式**（追加写，UTF-8）：`2026-11-04 15:20:31 | 结束 | pid=1234 | name=foo.exe | path=D:\... | 批量=N | 结果=成功|失败 | 原因=…`。批量执行逐 PID 各写一行。打开文件位置不写（非管控动作）。
10. **probe 双层验证**：`--dashboard-action-probe` 无 UI 端到端（直接起单进程 `ping.exe -n 600` 牺牲进程，无子进程残留 → 挂起 → 线程级验证已挂起 → 恢复 → 优先级 BELOW_NORMAL → GetPriorityClass 验证 → 结束 → 等退出；另测 pid=4 错误映射与死 PID 场景）；`--dashboard-action-ui-probe` 打开真面板选中牺牲进程行，走真对话框（ProbeSetConfirm + ProbeConfirmClick，与鼠标点击同代码路径）真执行挂起/恢复/结束并截图。
11. **死 PID 的 OpenProcess 报 5 不报 87**（Win11 26300 实测）：OpenProcess 失败统一走 OpenFailed —— 先取 LastWin32Error（EnumProcesses 会把它冲掉，先取！），再查 K32EnumProcesses 列表判断进程是否还在，不在 → 「已退出」。
12. **K32EnumProcesses 缓冲区 1024 不够**：本机 500+ 进程时 needed 被截断，PID 撞在截断区就误判「存在」。开 4096。
13. **zombie 进程的 TerminateProcess 报 5**：已终止但对象没放干净（PID 还在列表、GetProcessById 还能拿到）时 TerminateProcess 报 ERROR_ACCESS_DENIED。修法：失败后先 WaitForSingleObject(0)，句柄已置信号 → 报「已退出」而非「权限不足」。
14. （待补）

## 4. 验证结果

| 验证项 | 结果 |
|---|---|
| `dotnet build -c Release` | ✅ 0 error 0 warning（首轮 16 错：对话框缺 using WMonitor.Interop / BentoButton 无 FontScale / ThreadState 二义性；1 警 CS8602 订阅先于 _btnConfirm 赋值 → 挪后修掉） |
| `--dashboard-action-probe` | ✅ 全 PASS：挂起→线程级验证 isSuspended=True、恢复→isSuspended=False、优先级 0x20→0x4000 读回验证、结束→HasExited、死 PID →「进程已退出」、pid=4 →「权限不足」、actions.log 逐条留痕（成功/失败都记）。三个实测坑见 §3.11–13 |
| `--dashboard-action-ui-probe` | ✅ 三轮全 PASS：真面板选中牺牲进程行 → 真对话框（ProbeSetConfirm+ProbeConfirmClick 与鼠标同路径）→ 挂起（isSuspended=True）/恢复（isSuspended=False）/结束（HasExited=True）；config 视图/过滤/窗口记忆用后恢复 |
| UI 截图 | ✅ 面板 1280×800（DetailBar 四按钮就位：打开位置/优先级/挂起/红色结束任务）、挂起确认 470×222（目标·PID·路径·影响范围·必勾确认框·确认钮禁用态）、结束任务确认 470×222（红色标题+红钮） |
| error.log | ✅ 无新异常（文件不存在） |

## 5. 已知问题 / 风险（随时补）

1. 阶段 1/2/3 遗留：交互未实测、1 小时稳定性未测、Overloaded 降级未实测（docs/13 §5.1）。
2. 复验后已修正：名称/PID 排序方向、主题切换时搜索框未换色、关键进程提示按显示名匹配不到 explorer、优先级为 REALTIME/HIGHEST 时对话框默认落到「空闲」、磁盘图标流关闭后的 GDI+ 生命周期、探针把「达到目标状态」误标成 isSuspended。修复未重新发布版本时，运行中的 v0.21 仍是修复前产物。

## 6. 施工日志（新条目加在最上面）

- **2026-11-04 (5)** v0.23 UI 修复发布：选项卡 `Segmented` 加 `NoWrap`，GPU/阈值段控加宽，文字单行；`DetailBar.BackColor` 对齐卡片底色，消除操作按钮黑色底块；设置按钮改为与顶栏 Chip 同高同基线（32px、y=9、无额外投影）。发布 framework（481,991 B）/ portable（66,047,761 B）/ 安装版三产物；发布版 dashboard/action probe 全 PASS，PrintWindow 截图确认三处 UI 修复生效；当前以 v0.23 portable 运行托盘。
- **2026-11-04 (4)** v0.22 发布：版本号统一 0.22.0；发布安装版 + framework（481 KB）+ portable（66 MB）三产物；README/AGENTS/changelog/plan 版本引用同步。发布后启动 portable 验证：主面板标题「WMonitor 资源面板」可打开/关闭，关闭后托盘进程存活；framework 发布版 dashboard/action probe 均 PASS，portable/安装版 dashboard probe 均 PASS，无 ping 残留。内存实测：framework 关闭面板 ≈48.7 MB，开面板 ≈54.3 MB；portable 因自包含单文件关闭面板 ≈80 MB。
- **2026-11-04 (3)** 独立复验：`dotnet build -c Release` 0 error 0 warning；四组 probe 通过（dashboard / gpu / icon / action）。修正名称/PID 排序反向、SearchBox 主题切换、explorer 关键进程提示、REALTIME/HIGHEST 优先级回退、IconCache 磁盘流生命周期、动作探针 `isSuspended` 输出语义与牺牲进程清理。复验断言：nameAsc=-1、nameDesc=1、criticalExplorer=True、realtimeFallback=0x80。注意：本轮只编译验证，未重新发布 v0.21 三产物；dashboard probe 刷新帧曾出现 17ms（含 NtQuery 全量刷新），稳态仍为 3–5ms。
- **2026-11-04 (2)** 编译 + 双 probe + 截图验收全过（§4）。三个实测坑入档（§3.11–13）：死 PID 的 OpenProcess 报 5（OpenFailed 先取 LastWin32Error 再 EnumProcesses 判存在，缓冲区 4096）；zombie 进程 TerminateProcess 报 5（先 WaitForSingleObject(0) 判已置信号 → 报「已退出」）。UI probe 加 terminate 对话框 Show 停留后双对话框截图齐。工具脚本 `D:/Hermes/tmp/pwdlg.ps1`（按标题 PrintWindow 抓对话框；**PS1 带中文注释 + 无 BOM 会被 PowerShell 5.1 按 ANSI 解析爆掉，要么纯 ASCII 要么带 BOM**）。
- **2026-11-04 (1)** 建立本进度页。前置阅读完成：plan §2.4/§7（阶段 4 + 安全规则）、docs/13、AGENTS.md、Native/AppMetricsHub/DashboardWidgets(DetailBar)/MainDashboardForm/BentoButton/Segmented/Chip/Theme/Program(probe 结构)/csproj。范围与设计决策定稿见 §1/§3。
