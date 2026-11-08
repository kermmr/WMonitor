# 14 · 主面板阶段 4 施工进度（管控）

> **本文档是阶段 4 的交接页。** 任何 Agent / 对话中断后接手，先读本页 + `docs/10-main-dashboard-plan.md`（§7 阶段 4 + 安全规则）+ `docs/13-phase3-progress.md` + `AGENTS.md`。
> 规则：**每完成一个文件就更新本页的「施工状态」和「日志」**，不要攒到最后一起写。

- 方案总纲：`docs/10-main-dashboard-plan.md`（管控范围 §7 阶段 4、安全规则同节、详情区 §2.4）
- 阶段 1/2/3 交接：`docs/11` / `docs/12`（LUID 坑 §5.4）/ `docs/13`（NtQuery / owner SID / 图标缓存十三决策）
- 目标版本：v0.23（阶段 4）→ v0.24（UI 整改 P1 + icon B）→ v0.25（2026-11-05：标题栏窗口钮修复 + UI 整改 P2 批次 C9~C17，见日志 2026-11-05 (5)）→ **v0.26（2026-11-05：UI 整改 P3 批次 C18~C22，见日志 (6)）**
- 状态：**✅ 阶段 4 完成并已发布 v0.23 → v0.24（P1 + icon B）→ ✅ v0.25（窗口钮修复 + P2）→ ✅ v0.26（P3）—— 主面板四阶段 + UI 整改 P1~P3 全部收官**

## 0. 当前状态速览（接手先看这里）

| 项 | 状态 |
|---|---|
| 编译 | ✅ 0 error 0 warning |
| 阶段 4 全部文件 | ✅ 已完成并发布（安装版 + framework + portable 三产物，当前 **v0.29**） |
| UI 整改 | ✅ **P1（C1~C8，v0.24）+ P2（C9~C17，v0.25）+ P3（C18~C22，v0.26）全部完成**，见 docs/03 Part C |
| 标题栏窗口钮 | ✅ 无响应 bug 已修复（客户区坐标二次换算），`--dashboard-chrome-probe` 18/18 PASS（v0.26 扩容） |
| 标题栏右键 | ✅ C20：右键标题栏弹系统菜单（探针拦截路径正/反例 PASS；真实弹菜单观感留晨检） |
| 应用图标 | ✅ 方案 B（仪表）已落 `app.ico`；**v0.28 满幅重建**（79.7%→99% 画布占用，任务栏实测与邻居同大），见 docs/15 |
| 表头状态 | ✅ v0.28：排序列常驻 Accent 淡底（B2 互斥单选）+ hover 规则对齐，真光标截图验证 |
| 托盘健壮性 | ✅ v0.28：TaskbarCreated 兜底 + Shell_TrayWnd 就绪探测重试（NIM_ADD 静默失败竞态），见 docs/05 §22 |
| 下一步 | **无排期项**；遗留交互实测项见 §5 与日志 2026-11-05 (3)(5)(6) 未验清单 |
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

- **2026-11-08 (1)** **面板数值冻结事故修复，发布 v0.29（用户报告「ComfyUI 跑一晚后属性监控全部不变」）**：
  - **现象**：11-08 凌晨 ~02:20 起面板/贴片全部数值冻结，窗口仍可响应；故障进程 CPU 累计
    36,461 s ≈ 单核 100% 持续空转（线程 CPU 差分 3s 窗口锁定主线程 99%）；debug.log 贴片绘制
    日志同步停更。
  - **定位三件套**（脚本入档 `wmonitor-eidt/diag/`：threads.ps1 / winupd.ps1 / verify.ps1）：
    ① 线程级 CPU 差分找热点线程；② `comsvcs.dll,MiniDump` 全量 dump + `dotnet-dump analyze`
    （`dotnet tool install -g dotnet-dump`，SDK 在 `D:\Hermes\tools\dotnet-sdk`）两次 `clrstack`
    —— 主线程在 `GetMessage`/`DispatchMessage` 间打转，泵活着但被消息灌满；③ `GetUpdateRect`
    查出贴片窗口整窗脏区永久清不掉 → **无限 `WM_PAINT` 风暴**。
  - **根因**：`LayeredWindow.WndProc` 吞 `WM_PAINT` 不 `ValidateRect`。`WM_PAINT`/`WM_TIMER`
    都是队列空闲时才生成的低优先级消息：风暴一起，WinForms 采样定时器饿死 → 数值冻结；
    能结束风暴的 `Blit()`（UpdateLayeredWindow 顺带校验整窗）只能由被饿死的定时器触发 →
    死锁无自愈。触发事件 = ComfyUI 把 GPU 打满引起的驱动 TDR / DWM 重绘（弄脏一次即永久）。
    **修复**：`WM_PAINT` 分支 `ValidateRect(Handle, null)` 后 return。复盘入 docs/05 §23。
  - **顺带修复**：`Native.SendMessagePtr` 缺 `EntryPoint="SendMessageW"` —— user32 无此导出，
    v0.28 的 WM_SETICON 24px 小图标从未发出（catch 吞掉只留 `taskbar icon fail` 日志）。
    见 docs/05 §24。
  - **验证**：构建 0 error 0 warning；对运行中 v0.29 强制 `InvalidateRect` 贴片（= 复现触发
    条件），脏区 300 ms 清空、CPU 0%（v0.28 同条件即永久风暴）；稳态 CPU 0.78% 单核（含启动
    预热）。三产物重发（安装版已在运行）。
- **2026-11-07 (8)** **表头选中块贴内容对称（用户反馈「减少左边的宽度和右边一样」）**：整列选中块文字右对齐后左空当 ~39px vs 右 ~7px —— 块改为**紧贴内容盒（文字+三角）左右对称**（pad 7s、R6、块高 32）,所有列一致;列宽/文字/数据/表格宽度不动,点击命中区仍整列。选中（内存）+ hover（显存）双截图验证。B2 规格行改写。三产物重发。
- **2026-11-07 (7)** **标题栏图标 16→24（用户反馈）**：两个面板（资源面板/设置）标题栏图标按任务栏同比例放大（WindowChrome `iconD = S(24)`,标题文字 x 联动）,B1 规格行改写。双面板 PrintWindow 截图验证。三产物重发。
- **2026-11-07 (6)** **r128 上线 + 图标缓存重建补课**：用户复检仍见旧图 —— ① r128 那轮 `render-gdi.ps1`（中文注释无 BOM）产出「只有指针」的坏帧且未目检就发布（重写纯 ASCII 后正常,渲染产物必须目检后再组装）;② 上轮缓存重建**没删干净** —— iconcache_*.db 还被 StartMenuExperienceHost/SearchHost/ShellExperienceHost 锁着,只杀 explorer 无效;本轮把 shell 四进程全停后删除（残留 0 验证）+ 重启 explorer + 重启 app,任务栏实拍 r128 满幅方块 + 有呼吸感表盘,与邻居一致。三产物重发。
- **2026-11-07 (5)** **表盘回调 r128（用户反馈「里面图形太大了」）**：方块渲染修好后 94% 表盘太满——用户点名「以前改的是表盘但我看不到」属实：方块 16px 期间调表盘全是无效功。三档对比图（r104/128/168 @24px 深底）供选，回调 **r128**（外缘 ~70%）：环 128、笔画 37/44/59、圆点 30/33、指针 25/32，SVG×6 + render-gdi.ps1 同步。任务栏实拍：满幅方块 + 有呼吸感的表盘。三产物重发。
- **2026-11-07 (4)** **单实例弹窗移除（用户反馈「经常出这种弹窗」）**：第二实例启动改为**唤醒已有实例打开资源面板**（主面板未启用则开详情面板）后静默退出，不再弹「已经在运行」。机制：`Local\WMonitor.ShowDashboard.v1` AutoReset 事件（Main 在抢到互斥体后立即创建，缩小 OpenExisting 竞态窗口）+ 首实例 500ms UI 线程轮询；唤醒失败（首实例初始化中/正在退出）同样静默退出。实测：第二实例启动后进程数保持 1、弹窗 0、首实例主面板自动打开。三产物重发。
- **2026-11-07 (2)** **v0.28 复检二轮（用户拍板）**：① 表头互斥单选**不要整格填色** —— 改 A8 Segmented 同款内缩圆角块（缩 3、R6、块高 32）：排序列常驻 `SelectedBg` 块 + Text，未选 hover 叠 `Hover` 块，B2 规范行改写。② icon「还是小」—— 任务栏亮度图逐像素测量定位真因：**深色容器方块在深色任务栏上完全隐形，「可见图标」只是蓝色表盘**（环外缘仅 51%，墨迹 ~17px vs 邻居 20~24px）。表盘两轮放大至 r168（外缘 94%），24px 帧蓝色墨迹实测 20px 对齐邻居；任务栏实拍 + 亮度图复验。**教训：评估任务栏图标必须比「可见墨迹宽度」（亮度图），ICO 画布占用 100% 不代表观感同大。**
- **2026-11-07 (3)** **v0.28 复检三轮·任务栏按钮方块偏小的真因（两层）**：用户指出「不是比图形,是比外面的方块整体大小」—— 8x 截图证实 WMonitor 方块 ~16px vs 邻居 24px。**第一层：任务栏按钮用窗口图标（WM_SETICON）而非 exe ico；Win11 按 ICON_SMALL 原生尺寸绘制、不放大小图标**,WinForms 发的 ICON_SMALL 只有 16px → 方块 2/3。活体实验：对运行中的窗口补发 WM_SETICON(24/32) **无效** —— 任务栏只在按钮创建时取一次图标。修复:`ChromeForm.OnHandleCreated` 在 `Icon = _appIcon` 之后补发 ICON_SMALL = 24×(dpi/96) + ICON_BIG = 32×(dpi/96)（32px 源高质量缩放,ScaledIcon 辅助;句柄随窗体字段生命周期）。**第二层：Windows 外壳图标缓存按 exe 路径缓存旧版图标,文件更新后不一定失效** —— 代码修复部署后用户仍见旧小图标;诊断法:同一 exe 复制到新路径启动,新缓存条目的按钮立刻满幅,与旧路径同屏对比即证。处置:删 iconcache_*.db + 重启 explorer（用户确认后执行,顺带实测 TaskbarCreated 自愈与托盘重挂生效）。任务栏实拍:方块与邻居同大。**教训:升级 exe 后任务栏/桌面图标未更新 = 图标缓存,不是代码问题;A/B 诊断用「新路径副本启动」。**
- **2026-11-07 (2)** **v0.28 复检二轮（用户拍板）**：① 表头互斥单选**不要整格填色** —— 改 A8 Segmented 同款内缩圆角块（缩 3、R6、块高 32）：排序列常驻 `SelectedBg` 块 + Text，未选 hover 叠 `Hover` 块，B2 规范行改写。② icon「还是小」—— 任务栏亮度图逐像素测量定位真因：**深色容器方块在深色任务栏上完全隐形，「可见图标」只是蓝色表盘**（环外缘仅 51%，墨迹 ~17px vs 邻居 20~24px）。表盘两轮放大至 r168（外缘 94%），24px 帧蓝色墨迹实测 20px 对齐邻居；任务栏实拍 + 亮度图复验。**教训：评估任务栏图标必须比「可见墨迹宽度」（亮度图），ICO 画布占用 100% 不代表观感同大。**
- **2026-11-07** **v0.28：应用图标满幅重建 + v0.27 复检三问题修复（用户反馈「icon 太小 / 托盘偶发消失 / 表头状态不对」）**：
  - **icon 太小的真正根因**：生产 SVG 画布 512、主体只占 52..460（79.7%）+ faithful 投影滤镜 —— 与表格行图标、托盘渲染都无关（v0.27 改错了方向）。放大任务栏截图 + ICO 逐帧 LockBits 测墨迹坐实。重建：viewBox 收紧 `50 50 412 412`（99%）、去投影；**渲染管线换 GDI+ 直绘**（`design/icons/tools/render-gdi.ps1`）——magick SVG 光栅化在 48px 截断成 48×36、Edge 无头小窗口截图内容损坏（白顶带黑底带），都不可信；组装 `build-ico.ps1`（256 PNG + 其余 BMP、真实 AND mask、biSizeImage=XOR 长度——magick 组装的 48 帧 mask 行距按 WORD 对齐会花屏）。shell 解码器逐帧验收 16~128 全部 100%。旧图标备份 `backups/app-v0.27-old.ico`。
  - **v0.27 回归**：`DrawRow` 移除 3px 内缩时把 `DrawImage` 一起删了（行图标区域什么都不画），恢复绘制、保留占满 28px 的决定。
  - **表头 B2 互斥单选**：v0.27 只在 hover 时叠 Accent 淡底 → 排序列常态无标识；改为排序列**常驻** `Accent@24%/10%` 淡底、未选 hover 叠 `Hover` + `Label→Text`、排序列 hover 复用淡底不叠加。**真光标验证**：SetCursorPos + mouse_event 本会话可用（探针进程时代被拒，现可用了）——hover 内存/GPU 列出灰底提亮、CPU 列常驻蓝底三张截图 PASS（`head-*.png`）。⚠ 排障注意：**ZCode 终端窗口会盖住面板右半边**，光标测试前必须 SetForegroundWindow 并确认 WindowFromPoint 命中目标（本轮「悬停无效」排查半天结果是光标在终端上）。
  - **托盘 NIM_ADD 竞态**：.NET 8 NotifyIcon 源码核实 —— TaskbarCreated 原生自愈 ✓，但 `Shell_NotifyIconW` 返回值被忽略、`_added` 无条件置 true → 开机自启动早于任务栏就绪时图标永久消失。补 `Shell_TrayWnd` 就绪探测 + `Visible` 翻转重加；v0.27 的 TaskbarCreated filter 保留但去掉多余重画（旧 Icon 句柄仍有效，重画泄漏）。docs/05 §22 重写。
  - **验证**：构建 0/0；主面板截图（CPU 列常驻淡底 + 行图标全恢复 + 标题栏新图标）；任务栏实拍 WMonitor 按钮满幅与邻居同大；三产物重发（framework 661KB / portable 59.5MB），安装目录已更新并以安装版常驻。
- **2026-11-05 (7)** **真鼠标验收（CUA 桌面控制）+ 修复窗口越界恢复**：
  - 真实输入验证：标题栏拖拽移动 PASS（两次拖拽窗口跟随精确）；真点击可命中自绘 chrome 控件行（设置窗被真点击打开，渲染 v0.26 正常）。之后 GetCursorPos 被输入桌面拒绝（与探针进程同症状），最大化/最小化/关闭钮真点击、右键系统菜单、Snap Layouts 飞出仍留用户手测。
  - **修复**：记忆矩形 `812,319,1280,800` 在 1648×762 工作区上只做 IntersectsWith 就照搬恢复 → 面板右侧 ~444px / 底部 ~309px 出屏。`RestoreWindowBounds` 恢复前 `FitToWorkArea` 夹回工作区（`UI/MainDashboardForm.cs`）。重启后全窗可见，修复验证 PASS。
  - 三产物已重新发布（版本号保持 v0.26）。
- **2026-11-05 (8)** **用户晨检反馈修复**：① 图标没换 —— `MainDashboardForm` 构造里 `if (Icon == null)` 永假（默认图标非 null），任务栏/标题栏一直是 WinForms 默认图标；改 `ChromeForm.OnHandleCreated` 静态缓存 `ExtractAssociatedIcon`（debug.log `formIcon=16x16` 实证），exe 内嵌图标经提取比对确认已是方案 B 仪表。② 首开卡顿 + 透明首帧 —— 构造函数同步首采样卡 UI 线程，挪 `OnShown`（`MainDashboardForm`）。portable 冷启动 1~2s 解压是独立成本。三产物重发，PrintWindow 验证标题栏/列表图标均为新仪表。
- **2026-11-05 (9)** **「提示在运行但看不到任何图标和界面」定位**：沙箱 shell 启动的实例（新旧构建 alike）托盘/贴片注册被卡（线程 EventPairLow、无 strip 窗口），占着单实例 Mutex 导致用户双击只见「已在运行」。v0.25 同样复现 → 非 app bug；任务计划程序（用户完整令牌）启动同一 exe 一切正常（strip vis=True 372×30 @任务栏）。已用计划任务恢复常驻并删除任务；入档 docs/05（含 `inspect-wmonitor.ps1` 诊断脚本）。
- **2026-11-05 (10)** **表格列头 hover 补齐（用户反馈「这一排也是文字按钮没有 hover」）**：`DashboardTable` 表头此前只处理点击排序无 hover 态；补 `_headHover` 跟踪 + hover 单元格叠 `Hover` / 文字提亮 / 手型光标（排序列不叠色保持 Text 常亮）。编译 0 warning，chrome 探针 18/18 回归，三产物重发并经任务计划恢复常驻。
- **2026-11-05 (6)** **UI 整改 P3 批次（C18~C22）落地，发布 v0.26 —— Part C 全部收官**：
  - **C18 焦点**：`Segmented`/`Chip` 加 `TabStop=true` + GotFocus/LostFocus 重绘 + A7 焦点环（内缩 2.5px 处 1px `Text@47%`，`Color.FromArgb(120,…)`，与 BentoButton 同规格；Segmented 环 R5、Chip 环 Pill）；键盘：Segmented 方向键（含上下）切选项并触发 SelectionChanged，Chip **Space** 切换（**Enter 特意不拦**——对话框里 Enter 是确认键）。探针验证走程序化 `Focus()`（与真实 Tab 同一条 GotFocus 路径）+ DrawToBitmap 有/无焦点两帧像素 diff。
  - **C19 Ghost**：`BentoButton.Ghost` 分支（A7 全项）：常态透明不铺底、字 Label Regular、hover/按下变 Text、永不投影（`layers` 分支排除）、禁用不铺 DisabledBg 只压字 Label@190、`BodyH = Height - (Ghost ? 0 : Shadow)`（Ghost 实例建议 Height=32/Shadow=0）、焦点环用 `Th.Text` 定色（不随 hover 的 Label↔Text 抖动）。**入档偏差：标题栏窗口钮不走 Ghost**——A7 的「透明 + Label」与 B1 的「hover 底 Hover + 关闭钮红底 + Text 图标（Win11 惯例）」冲突，B1 优先且不动 v0.25 刚修好、探针覆盖的 chrome 消息路径；Ghost 分支待表格列头/状态栏操作控件化时接入（探针已布像素断言，接入即有回归）。
  - **C20 标题栏右键系统菜单**：`WM_NCRBUTTONUP(HTCAPTION)` 拦截（不交 base 防双菜单）→ `ShowCaptionMenu`：SetForegroundWindow（防点外不收）→ `TrackPopupMenu(GetSystemMenu, TPM_RIGHTBUTTON|TPM_RETURNCMD|TPM_NONOTIFY)` → PostMessage `WM_NULL` + `WM_SYSCOMMAND`（选中项走系统标准路径，移动/大小/最小化/最大化/还原/关闭全复用现有处理）。探针开关 `ProbeInterceptSysMenu` 只置旗标不真弹（TrackPopupMenu 会阻塞消息循环），断言拦截路径正/反例（HTCLIENT 不触发）。
  - **C21 圆角双画验证**：PrintWindow 抓主面板 + 挂起/结束确认对话框共 12 张四角 4x 放大图（`D:/Hermes/tmp/capcorner.ps1`，纯 ASCII 免 BOM 坑，与 action-ui-probe 并行轮询）：**自绘层全部干净**——对话框单条 R12 弧 + 均匀 WindowBg 外圈，无第二弧、无裁角；主面板四角 WindowBg 到边，内部卡片/按钮圆角正常。DWM 偏好读回 main=2 / settings=2（ROUND）。**局限入档**：DWM 合成圆角裁的是窗口形状，发生在窗口表面之外，PrintWindow 截不到（本环境 CopyFromScreen 不可用）；解析上安全（卡 R12·s ≥ DWM ~8px，合成弧只裁 WindowBg 外圈），合成弧最终观感留用户晨检一眼。
  - **C22 MessageDialog 行距**：正文行距 17（旧小字号手量值）→ `Gfx.MeasureG`（新公共共用量 Graphics，静态持有防 GC 回收坑，同 BentoButton.MeasureG）+ `Font.GetHeight` 实算（Body 12 ≈16px，字体已含 scale，绘制与高度公式都不再乘 s）；按钮行与正文间隙恒 12px 不随行数漂移（解析：body 底 − 钮顶 = −12 恒成立）。
  - **验证**：构建 0 error 0 warning；`--dashboard-chrome-probe` **18/18 PASS**（原 10 项回归 + C20×2 + DWM 读回 + C18×4 + C19×1）；`--dashboard-action-ui-probe` 三轮 PASS（探针行标签 `isSuspended=` 仍是旧歧义——打的是 WaitSuspended「到达期望态」，resume 行 True=确认脱离挂起，docs 早有记录非本轮回归）；`--dashboard-probe` 正常；error.log 无新异常。四角截图 `D:/Hermes/output/results/c21-*.png`。
  - **发布**：三产物（`WMonitor/` 安装目录 + framework + portable）；发布版 chrome/action 探针复验 PASS 后，v0.26 portable 接替 v0.25 常驻托盘。changelog/docs/03 状态行/README/AGENTS 版本引用同步。
  - **未验项（无真实鼠标/键盘模拟，留用户晨检）**：标题栏右键真弹系统菜单观感（点外收起、命令生效）、DWM 合成圆角最终观感、Segmented/Chip 真实 Tab 巡航 + 方向键/Space 手感、Ghost 无实例（纯分支，无用户可见变化）。
- **2026-11-05 (5)** **UI 整改 P2 批次（C9~C17）落地，发布 v0.25**（接 (4) 窗口钮修复，同日连做；「逐处小改注意联动」按文件逐一核对）：
  - **C9 字号映射**（≤12→12、12.5~13.5→14、15→16）：`DashboardTable`（行名 12.5→14、首字母 13→14、表头 11→12B、数值 11.5→12、路径 9.5→12、空态提示 10→12）、`DashboardWidgets`（MetricCard 标签 12.5→14 / 副题 11→12；DetailBar hint/meta/sub 11.5/10.5/10→12；进程名 14 不动）、`DetailPanel`（标题 12.5→14、行名 11.5→14、数值 15→16、单位/副题/底栏/时间窗 9~11.5→12）、`MainDashboardForm`（状态栏 10→12）、`ActionConfirmDialog`/`MessageDialog`（标题 13.5/13→14、正文 10.5→12、辅助 10→12）、`FlyoutMenu`（13/13.5→14、11→12B、10.5→12）。托盘图标（TrayIconRenderer）按 A3 豁免不动。
  - **C10 单位 px 化**：SearchBox 输入字 10pt→12px（A3 点名的真变值），且 `UiScale` 改属性 setter 随缩放重建字体；BentoCard 标题 10.5pt→14px / 注释 8.5pt→12px；主面板/设置窗默认 Font 9pt→12px；设置窗品牌 15pt→20px、Cap/Inline/Small 8.5pt→12px。副产品：pt 字在固定布局的高 DPI 下不再膨胀错位。
  - **C11 控件高 32**：`Segmented` 34→32、`Chip` 30→32（默认宽改用 12px 字实测重算）、`Stepper` 30→32、`SearchBox` 34→32、`BentoButton` 默认 44→38（视觉 32+投影 6，A10）；对话框按钮 40→38（C17）；设置窗 `CtrlH` 34→32、`ChipH` 30→32、告警规则胶囊 28→32、±/单位格 30→32、完成/恢复默认 44→38、`_scriptBtn` 视觉高对齐 32。**联动**：设置窗 Cap 标题格 16→18（12px 字 16 装不下）；告警规则行两排格子 y 34→36；Slider 不在 A6 表内维持 26。
  - **C12/C13 间距**：主面板 DoLayout pad 14→16 / gap 10→12（只动两个变量，公式自适应）；设置网格 Gap 14→12、ColW 287→288（(1238−48−36)/4）。
  - **C14 表头底 token**：`DashboardTable.DrawHeader` 内联 rgb(30,33,39)/rgb(246,247,250) → `Th.HeadBg`（token v0.24 已建，值一致）。**C15/C16 复核**：Segmented 选中块与 Soft/SoftLight 合并均已在 v0.24 完成，全库无残留。
  - **C17 对话框/详情条按钮**：DetailBar 四钮已是 32 视觉（LayoutButtons 32+6，P1 时已落）；本轮把 `ActionConfirmDialog` 取消/确认与 `MessageDialog` 确定钮 40→38（视觉 32+6），ComputeSize 按钮行 52→50，优先级段控 34→32（槽位 56→54）。
  - **联动修正两处**：① `ActionConfirmDialog` 关键进程红字槽位 36→40、绘制框 30→34（两行 12px 墨迹 ~32，原槽位会裁）；② `DetailPanel` 时间窗胶囊 26×16→30×18（12px 的「24h」装不下）。`_scriptBtn` Radius 9→10 补齐 v0.24 日志声称但实际漏改的一处。
  - **验证**：构建 0 error 0 warning；`--dashboard-probe` 正常；`--dashboard-chrome-probe` 11/11 PASS；`--dashboard-action-ui-probe`（真对话框新几何）suspend/resume/terminate 全 PASS；**portable 发布版** chrome + action 探针全 PASS；PrintWindow 截图：深色常态/最大化(2576×1408 客户区内缩正确)/还原、设置窗（v0.25 版本号、全 32 控件）、浅色主题全套、贴片 372×30、确认对话框（12px 正文 + 等宽 38 高钮 + 禁用态确认钮）；portable 常驻内存 84.3MB（与 v0.24 ~81-84 持平）。
  - **发布**：三产物（`WMonitor/` 安装目录 + `WMonitor-v0.25-framework.exe` 678,599 B + `WMonitor-v0.25-portable.exe` 66,150,789 B）；v0.25 portable 已作为常驻托盘运行（顶替被杀的 v0.24 portable）。changelog/plan/docs/03 状态行/AGENTS.md 版本引用同步。
  - **未验项（无真实鼠标模拟，留用户晨检）**：标题栏窗口钮真实手感（拖拽移动/边缘缩放/Snap Layouts 飞出）、设置窗滚动、告警开启态布局。
- **2026-11-05 (4)** **修复 C1 自绘标题栏「最大化/最小化/关闭钮无响应」（用户实测反馈）+ 新增 `--dashboard-chrome-probe` 回归探针**：
  - **根因**：`WM_NCLBUTTONDOWN` 吞掉消息后 `SetCapture` 接管鼠标，此后系统送来的是**客户区消息**（`WM_MOUSEMOVE`/`WM_LBUTTONUP`，坐标已是客户区坐标），旧代码对它们又 `PointToClient` 一次 = 把点平移一个窗口原点 → `ButtonAt` 永远不命中。**只有窗口恰好在屏幕 (0,0) 时碰巧能用**——所以 PrintWindow 截图验收（不点击）发现不了。
  - **修法**（WindowChrome.cs）：`WM_MOUSEMOVE`/`WM_LBUTTONUP` 按下期间直接用客户区坐标（不再换算）；`WM_NCMOUSEMOVE`/`WM_NCLBUTTONUP` 增加 `_press != None` 兜底分支（屏幕坐标，SetCapture 失败时 UP 以 NC 消息回来）；新增 `WM_CAPTURECHANGED` 复位按下态（防弹窗抢捕获后按钮卡 pressed）；按下逻辑收敛为 `TrackPressInside(p)` / `FinishPress(p)` 两个私有方法（p 一律客户区坐标，由各消息分支负责换算）。
  - **探针**（`--dashboard-chrome-probe`，结果写 `wmonitor-chrome-probe.txt`）：消息级复现真实点击序列（`WM_NCLBUTTONDOWN`(HT*, 屏幕坐标) → `WM_MOUSEMOVE`/`WM_LBUTTONUP`(客户区)），窗口故意摆在 (220,140) 非原点（旧 bug 必现场景）；覆盖 hover 命中+LEAVE 清理、pressed 滑出/滑回、最大化→还原→最小化→关闭、设置窗（FixedSingle 仅关闭钮）关闭。**11/11 PASS**。
  - **探针坑两个**：① 本机探针进程 `SetCursorPos` 被输入桌面拒绝（ok=False，读回 0,0）——真实光标模拟不可用；② 合成 `WM_NCMOUSEMOVE` 会注册 TME_NONCLIENT，真实光标不在窗口上时系统立刻回发 `WM_NCMOUSELEAVE` 清 `_hot` → hover 是瞬态，外部轮询看不见，断言走 `ChromeForm.ProbeHoverFired` 内部旗标（WndProc 入口 trace 验证过：`_hot` 确实先变 Close 再被 LEAVE 清理，逻辑本身没毛病）。
  - 构建仍 0 error 0 warning。Snap Layouts 飞出 / 拖拽移动 / 边缘缩放仍无鼠标模拟，留用户晨检。
- **2026-11-05 (3)** **UI 整改 P1 批次（C1~C8）+ 应用图标方案 B 落地，发布 v0.24**（用户确认「C1 → C2~C8」+「icon 选方案 B」后夜间自动执行）：
  - **icon B（仪表）落 .ico**：RSVG 2.40 不认 `feDropShadow`，生产 SVG 改 SVG1.1 滤镜链（feGaussianBlur/feOffset/feFlood/feComposite/feMerge）；尺寸链 256/128/64/48（faithful）+ 32/24（small：去光泽/内描边、笔画加粗 36/26/r27）+ 16（tiny：去暗轨+去指针，弧 48 + 点 r24，直缩 16px 会糊成块）。exe 主图标固定**深色版**（任务栏深色为默认场景），浅色版 `design/icons/app/app-light.ico` 备换。源文件 + 256px PNG 归档 `design/icons/app/`；旧 `app.ico` 备份 `backups/app-v0.23-old.ico`。组装：ImageMagick（256 帧 PNG、其余 BMP）。
  - **Theme.cs**：新增 `SelectedBg/HeadBg/InputBorder/DisabledBg`，`Soft/SoftLight` 合并 `SecondaryBg`（C16，FlyoutMenu 单选块同步迁移，深色值不变）。
  - **C1 自绘标题栏**：新基类 `UI/WindowChrome.cs`（`ChromeForm`）——保留 WS_OVERLAPPEDWINDOW（Snap/系统菜单/原生最大化动画都在），`WM_NCCALCSIZE` 抹非客户区（最大化按边框厚度内缩）、`WM_NCHITTEST` 自报 HTCAPTION/HTMINBUTTON/HTMAXBUTTON/HTCLOSE 与边缘热区（FixedSingle 不给缩放）、窗口钮 46×32 自绘（关闭 hover #C42B1C、按下加深）、DWM ROUNDED 系统圆角、Snap Layouts 经 HTMAXBUTTON 原生获得。主面板删 OnPaint 重复标题、DoLayout 改「标题栏 44 → 控制行 44 带 → 卡片」；设置窗随 B1 改自绘（只有关闭钮），顶栏 64/底栏 56 整体下移，原 DWM 深色属性代码删除。
  - **C2/C4/C7**：Segmented/Stepper/Chip/ConfirmCheck 全部 token 化（SecondaryBg/InputBorder/SelectedBg/Hover），Segmented 补 1px InputBorder 描边，Chip 补 hover（未选 Hover 叠加 / 选中 Accent@24%）；Segmented/Chip/ConfirmCheck/Stepper 值字 → Body 12（A8/A9 明确目标值）。
  - **C3/C5**：BentoButton Disabled 态（DisabledBg + Label@190，OnEnabledChanged 重绘）；字 9.5pt → 12px（Gfx.F 像素，主 Bold/次 Regular）；新增 `WidthFor`/`GroupWidth`（A7：Bold 基准 + 32、组内等宽）接线 DetailBar（右缩 14→16，B3）与两对话框。
  - **C6/C8**：MetricCard/BentoCard 圆角 14→12（设置窗告警卡按钮 Radius 9→10 顺带对齐 A5）；贴片 BaseRadius 7→8；贴片字号 8.5/12/9 → 12 Regular/14 Bold/12 Regular、圆点 5→6、间距 5/5/3（Measure 与 Draw 同步改），总宽实测 286→372（+30%，托盘左吸附只向左生长，间距 6 不变，目检无碰撞）。
  - **偏差记录**：① 多卡筛选 240→**264**（A3 最小 12px 后「全部显卡」在 4 段 240 内截断，A3 硬约束优先）；② 对话框取消/确认与 MessageDialog 确定钮组内等宽取 **max(公式值, 120)**（B4 目标值 ≈120 与 A7 公式 86 冲突，取 B4 明示值保底）；③ DetailBar/对话框按钮高度 40 留待 C17（P2）。
  - **两个运行时 bug（本轮实测发现并修复）**：① `BentoButton` 字体改走 Gfx 共享缓存后 `Dispose()` 仍释放 `_font` → 第一个对话框销毁即销毁全局缓存字体，下一个 `MeasureString` 抛「Parameter is not valid」（action-ui-probe 抓到）；缓存字体一律不 Dispose。② **portable 图标重试泄漏**：ChromeForm 每帧 `new Icon(Application.ExecutablePath, 32,32)` 在单文件 bundle exe 上失败被吞 → 下帧重试 → 每次读 66MB bundle，实测 70MB/s 内存增长（GDI/USER 句柄稳定、CPU 4%、最小化停止——paint 路径）；改为 `_iconTried` 一次性闸门 + 优先 `Form.Icon.ToBitmap()`（ExtractAssociatedIcon 在 bundle 上可用），修复后平台期 ~106MB（关面板回落 +13MB 与 v0.23 一致）。⚠ 排障插曲：实验 A 的 sed 注释 DrawChrome 后忘恢复，导致一轮「复测正常」全部无效——**对照实验后必须立即还原代码再验证**。
  - **验证**：构建 0 error 0 warning；PrintWindow 多轮截图（主面板常态/最大化(客户区内缩正确)/还原/选中行 DetailBar 启用态/挂起+结束两对话框/设置窗 v0.24/浅色主题全套/贴片 3x）；`--dashboard-action-ui-probe` suspend/resume/terminate 全 PASS；portable `--dashboard-action-probe` 6/6 PASS；内存 portable 常态 81MB / 开面板平台 ~106MB（无线性增长）。
  - **未验项（无鼠标模拟，留给用户）**：标题栏窗口钮点击与 hover、拖拽移动/边缘缩放、Snap Layouts 飞出、Alt+Tab/任务栏预览观感。**P2（C9~C17）与 P3（C18~C22）未动，见 docs/03。**
  - 发布三产物 + 常驻 portable（托盘+贴片）留给用户晨检；截图在 `D:/Hermes/output/results/v024-*.png`。
- **2026-11-05 (2)** 规范补充（用户评审 HTML 指南反馈）：① 指南按钮矩阵 Hover/按下/焦点格误加 `disabled` 属性、被禁用样式 `!important` 覆盖成三格同貌 —— 改 `.wm-static`（pointer-events:none）后状态色正确；② **A7 增补宽度规则**：同字数同宽（内容宽按主按钮 Bold 字宽基准，文字宽+32）、组内等宽（并排按钮组以最宽者为准）；B3 详情条四钮 78/72/72/86 → 组内等宽 ≈86、B4 对话框取消/确认 → 组内等宽 ≈120，C5/C17 同步；指南交互区/详情条/对话框按钮组已按等宽演示。
- **2026-11-05 (1)** **应用图标提案（新支线）**：调研设计网站趋势后产出三方案 × 明暗共 6 版 SVG（无字母抽象图形、统一 UI 语言，用户已拍板约束）——A 脉搏 / B 仪表 / C 雷达，源文件入库 `design/icons/proposals/`（唯一事实来源），预览 PNG `previews/`（2x，含全页总览/分方案/总览条），提案文档 `docs/15-app-icon-proposals.md`（含 token 映射表与小尺寸策略）。工具链：`design/icons/tools/build-fragment.mjs`（生成全量内联 SVG 展示页）+ `build-previews.mjs`（纯 SVG 版面）。两个渲染坑入档 docs/15 §4（`<use>` 渐变层丢失 → 全量内联；浏览器截图合成层不可信 → img→canvas 光栅化）。**待用户选定方案后落 .ico（256…16，≤32 简化）并替换 csproj `ApplicationIcon`**。
- **2026-11-04 (7)** 规范修订（用户反馈）+ 交互式样式指南：字体规则改为**最小 12px、向上步进一律偶数**（12/14/16…/30，A3 重写，全文档字号目标同步：按钮 11.5→12、表格行名 12.5→14、详情面板数值 15→16、状态栏/路径 10→12；托盘图标位图豁免）。贴片字号放大 12/14/12 入整改清单 **C8**（总宽 +约 30% 需目检托盘间距）。Part C 重编号：**P1×8 / P2×8 / P3×5**（C8 之后顺延）。另按规范产出**交互式 HTML 样式指南**（深浅主题切换、按钮四类×五状态、选择/输入控件、主面板模拟含确认对话框），随会话内联展示，供用户直观评审目标值。
- **2026-11-04 (6)** **UI 规范重写（无代码改动）**：`docs/03-ui-spec.md` 重构为全项目唯一视觉权威 —— Part A 设计语言（颜色 token / 字体 5 档 px / 间距 4-8-12-16-20-24 / 圆角 6 档 / 控件高统一 32 / 按钮四类×五状态 / 选择控件 / 阴影规则）/ Part B 各界面规格（新增主面板与对话框，标题栏定为**全自绘无边框 44 高**，用户已拍板）/ Part C 现状整改清单（P1×7 / P2×8 / P3×5，含 Theme.cs 需新增 SelectedBg/HeadBg/InputBorder/DisabledBg、合并 Soft→SecondaryBg）。盘点发现的关键不一致：三种控件底色内联并存、px/pt 字体单位混用（SearchBox 10pt≈13.3px）、卡片圆角 14/12 混用、**按钮/Chip/Segmented 均无禁用态渲染**（UserPaint 控件禁用不自动置灰）、Chip 无 hover。**下一步：用户确认规范目标值后按 C1→C7 批次执行整改**。
- **2026-11-04 (5)** v0.23 UI 修复发布：选项卡 `Segmented` 加 `NoWrap`，GPU/阈值段控加宽，文字单行；`DetailBar.BackColor` 对齐卡片底色，消除操作按钮黑色底块；设置按钮改为与顶栏 Chip 同高同基线（32px、y=9、无额外投影）。发布 framework（481,991 B）/ portable（66,047,761 B）/ 安装版三产物；发布版 dashboard/action probe 全 PASS，PrintWindow 截图确认三处 UI 修复生效；当前以 v0.23 portable 运行托盘。
- **2026-11-04 (4)** v0.22 发布：版本号统一 0.22.0；发布安装版 + framework（481 KB）+ portable（66 MB）三产物；README/AGENTS/changelog/plan 版本引用同步。发布后启动 portable 验证：主面板标题「WMonitor 资源面板」可打开/关闭，关闭后托盘进程存活；framework 发布版 dashboard/action probe 均 PASS，portable/安装版 dashboard probe 均 PASS，无 ping 残留。内存实测：framework 关闭面板 ≈48.7 MB，开面板 ≈54.3 MB；portable 因自包含单文件关闭面板 ≈80 MB。
- **2026-11-04 (3)** 独立复验：`dotnet build -c Release` 0 error 0 warning；四组 probe 通过（dashboard / gpu / icon / action）。修正名称/PID 排序反向、SearchBox 主题切换、explorer 关键进程提示、REALTIME/HIGHEST 优先级回退、IconCache 磁盘流生命周期、动作探针 `isSuspended` 输出语义与牺牲进程清理。复验断言：nameAsc=-1、nameDesc=1、criticalExplorer=True、realtimeFallback=0x80。注意：本轮只编译验证，未重新发布 v0.21 三产物；dashboard probe 刷新帧曾出现 17ms（含 NtQuery 全量刷新），稳态仍为 3–5ms。
- **2026-11-04 (2)** 编译 + 双 probe + 截图验收全过（§4）。三个实测坑入档（§3.11–13）：死 PID 的 OpenProcess 报 5（OpenFailed 先取 LastWin32Error 再 EnumProcesses 判存在，缓冲区 4096）；zombie 进程 TerminateProcess 报 5（先 WaitForSingleObject(0) 判已置信号 → 报「已退出」）。UI probe 加 terminate 对话框 Show 停留后双对话框截图齐。工具脚本 `D:/Hermes/tmp/pwdlg.ps1`（按标题 PrintWindow 抓对话框；**PS1 带中文注释 + 无 BOM 会被 PowerShell 5.1 按 ANSI 解析爆掉，要么纯 ASCII 要么带 BOM**）。
- **2026-11-04 (1)** 建立本进度页。前置阅读完成：plan §2.4/§7（阶段 4 + 安全规则）、docs/13、AGENTS.md、Native/AppMetricsHub/DashboardWidgets(DetailBar)/MainDashboardForm/BentoButton/Segmented/Chip/Theme/Program(probe 结构)/csproj。范围与设计决策定稿见 §1/§3。
