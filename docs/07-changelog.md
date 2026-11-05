# 07 · 版本记录

## v0.26 — 2026-11-05

**UI 整改 P3 批次（docs/03 Part C C18~C22，P1~P3 全部收官）**

- **修复（真鼠标验收发现）**：主面板记忆位置恢复后大部分开在屏幕外 —— `RestoreWindowBounds` 原来只检查
  `IntersectsWith`（有交集就照搬），记忆矩形来自更大屏幕（换屏 / 改分辨率）时右侧/底部整块出屏。
  恢复前先 `FitToWorkArea` 把矩形夹回工作区（超出的宽高收缩、位置夹紧）。
- **修复（用户实测反馈）**：① 任务栏 / 标题栏图标没换 —— `if (Icon == null)` 永远不成立（`Form.Icon`
  未设置时返回默认图标不是 null），一直在画 WinForms 默认双方块图标；`ChromeForm.OnHandleCreated`
  统一 `ExtractAssociatedIcon` 取 exe 内嵌图标（portable bundle 可用路径），静态缓存一次。② 打开主面板
  卡顿 + 首帧透明 —— 构造函数里的首次同步采样（GPU Engine 首采 + NtQuery + 版本缓存）在 `Show()` 前
  卡住 UI 线程 1~2s；挪到 `OnShown` 之后，窗口先画出来再填数据。portable 冷启动还有 1~2s 自包含
  解压成本，与本次修复无关。
- **列头 hover（用户实测反馈）**：`DashboardTable` 表头补 hover 交互 —— 单元格叠 `Hover` 底色、
  文字 `Label→Text` 提亮、光标变手型（激活排序列不重复叠加，仍 Text 常亮 + 三角）。

- **焦点（C18）**：Segmented / Chip 支持 Tab 焦点，按 A7 补焦点环（内缩 2.5px 处 1px Text@47%，与 BentoButton 同规格）；Segmented 加方向键切换选项（立即生效并触发 SelectionChanged）、Chip 加 Space 切换（Enter 不拦，对话框里是确认键）。
- **Ghost 按钮（C19）**：`BentoButton.Ghost` 分支落地（A7：透明底 + Label 字 Regular，hover/按下变 Text，永不投影，禁用只压字不铺 DisabledBg；BodyH 不再预留投影高度）。**入档偏差：标题栏窗口钮不走 Ghost**——B1 有自己的规格（hover 底 Hover + 关闭钮红底 + Text 图标，Win11 惯例），保持 v0.25 刚修好的 ChromeForm 自绘路径不动；Ghost 分支待表格列头/状态栏操作控件化时接入。
- **标题栏右键（C20）**：无边框窗口右键标题栏在光标处弹系统菜单（SetForegroundWindow + GetSystemMenu + TrackPopupMenu TPM_RETURNCMD，选中项 PostMessage WM_SYSCOMMAND 走系统标准路径；拦截后不再交给 DefWindowProc 防双菜单）。移动/大小/最小化/最大化/还原/关闭全部复用现有处理。
- **行距（C22）**：MessageDialog 正文行距 17（旧小字号手量值）→ `Font.GetHeight` 实算（Body 12 ≈16px，含 DPI 缩放），高度公式同步；按钮行与正文间隙恒 12px 不再随行数漂移。
- **圆角双画验证（C21）**：PrintWindow 抓主面板 + 挂起/结束两对话框共 12 张四角 4x 放大图——自绘层全部干净（单条 R12 弧 + 均匀 WindowBg 外圈，无第二弧、无裁角）；DWM 圆角偏好读回 main=2/settings=2（ROUND）。局限：DWM 合成圆角发生在窗口表面之外，PrintWindow 截不到（本环境 CopyFromScreen 不可用），合成弧观感留用户晨检一眼。
- 探针扩容：`--dashboard-chrome-probe` 11 项 → **18 项**（+右键系统菜单拦截路径正/反例、DWM 偏好读回、Segmented/Chip 程序化焦点 + 焦点环像素 diff、Ghost 透明底像素断言），18/18 PASS；`--dashboard-action-ui-probe` 三轮 PASS；`--dashboard-probe` 正常；error.log 无新异常。


## v0.25 — 2026-11-05

**修复标题栏窗口钮无响应 + UI 整改 P2 批次（docs/03 Part C C9~C17）**

- **修复（用户实测反馈）**：自绘标题栏「最大化 / 最小化 / 关闭」三个窗口钮点击无响应——`WM_NCLBUTTONDOWN` 后 `SetCapture` 接管的鼠标消息是**客户区坐标**，旧代码又做了一次 `PointToClient`（平移一个窗口原点），只有窗口恰在屏幕 (0,0) 时碰巧能用。同时补 `WM_NCLBUTTONUP` 兜底（SetCapture 失败时 UP 走 NC 消息）与 `WM_CAPTURECHANGED` 按下态复位。新增 `--dashboard-chrome-probe` 消息级回归探针（非原点窗口 + 真实点击消息序列），11/11 PASS。
- **字号（C9）**：全站档外字号按 A3 映射——表格行名 12.5→14、首字母 13→14、表头 11→12 Bold、行数值 11.5→12、路径 9.5/10→12、详情面板标题 12.5→14 / 数值 15→16 / 单位·副题·底栏 9~11.5→12、对话框标题 13/13.5→14 / 正文 10.5→12、飞出菜单 13/13.5→14 / 11/10.5→12、详情条 hint/meta/sub 10~11.5→12、状态栏 10→12；详情面板时间窗胶囊 26×16→30×18（装得下 12px「24h」）。
- **单位（C10）**：SearchBox 输入字 10pt→12px（真变值）、BentoCard 标题 10.5pt→14px / 注释 8.5pt→12px、主面板/设置窗默认 Font 9pt→12px、设置窗品牌 15pt→20px、Cap/Inline/Small 8.5pt→12px（全部 `GraphicsUnit.Pixel`，附带消除高 DPI 下 pt 字与固定布局的膨胀错位）。
- **控件高（C11）**：Segmented/Chip/Stepper/SearchBox 34/30/34/30 → 32；BentoButton 默认 44→38（视觉 32+投影 6）；对话框按钮 40→38（C17）；设置窗 CtrlH 34→32、ChipH 30→32、告警规则胶囊 28→32、完成/恢复默认 44→38、选择脚本按钮视觉高对齐 32（Radius 9→10 补齐 v0.24 漏改）。
- **间距（C12/C13）**：主面板 pad 14→16、gap 10→12；设置网格 Gap 14→12（ColW 287→288 自适应）。
- **token（C14）**：表头底内联 rgb(30,33,39)/rgb(246,247,250) → `HeadBg`（C15/C16 已随 v0.24 完成，本轮复核无残留）。
- 联动调整：对话框关键进程红字槽位 36→40（两行 12px 墨迹 ~32）；设置窗 Cap 标题格 16→18、行内标题/偏移格对齐 32。
- 验证：`--dashboard-probe` / `--dashboard-chrome-probe` 11 项 / `--dashboard-action-ui-probe`（新 38 高钮对话框真点击路径）全 PASS；portable 发布版 chrome + action 探针全 PASS；PrintWindow 截图（深浅主题常态/最大化/还原/设置窗/贴片 372px/确认对话框）；内存 84.3MB 与 v0.24 持平。

## v0.24 — 2026-11-05

**UI 整改 P1 批次（docs/03 Part C C1~C8）+ 应用图标方案 B 落地**

- **自绘无边框标题栏（C1）**：新基类 `UI/WindowChrome.cs`，主面板与设置窗换成全自绘 44 高标题栏（图标 16 + 标题 14 Bold + 窗口钮 46×32，关闭钮 hover #C42B1C），保留系统边框样式（Aero Snap / Snap Layouts / 系统菜单 / 原生最大化动画不受影响），DWM Win11 圆角；主面板删除面板内重复大标题。最大化时客户区按边框厚度内缩，内容不裁切。
- **控件全面 token 化（C2/C4/C7）**：Segmented / Stepper / Chip / ConfirmCheck 内联色全部换成 Theme token（SecondaryBg / InputBorder / SelectedBg / Hover）；Segmented 补 1px InputBorder 描边；Chip 补 hover 反馈；Theme.cs 新增 SelectedBg / HeadBg / InputBorder / DisabledBg，Soft/SoftLight 合并 SecondaryBg。
- **按钮（C3/C5）**：BentoButton 新增 Disabled 态（DisabledBg + Label@190）——未选中行时「优先级/挂起/结束任务」终于有视觉禁用；字号 9.5pt → 12px（主 Bold / 次 Regular）；宽度规则 A7 落地：`WidthFor` / `GroupWidth`（同字数同宽、组内等宽），DetailBar 四钮与对话框按钮组内等宽。
- **圆角与贴片（C6/C8）**：指标卡 / Bento 卡 14→12，贴片 7→8；贴片字号 8.5/12/9 → 12/14/12、圆点 6、间距 5/5/3（总宽 +30%，托盘间距目检无碰撞）。
- **应用图标**：提案方案 B「仪表」落 `app.ico`（256/128/64/48/32/24/16，≤32 按规范简化，深色版固定；设计源文件见 `design/icons/app/`、提案文档 docs/15）。
- **修复**：BentoButton 共享缓存字体被 Dispose 导致对话框二连开崩溃（Parameter is not valid）；portable 单文件 exe 上标题栏图标每帧重试读取 66MB bundle 造成 ~70MB/s 内存泄漏（改为一次性闸门 + Form.Icon 优先）。
- **偏差**：多卡筛选段控 240→264（A3 最小 12px 下「全部显卡」截断）；对话框按钮组等宽保底 120（B4 目标值优先于 A7 公式）。
- 验证：PrintWindow 多轮截图（常态/最大化/设置窗/对话框/浅色主题/贴片）、`--dashboard-action-ui-probe` 全 PASS、portable `--dashboard-action-probe` 6/6 PASS、内存平台无增长（详见 docs/14 施工日志 2026-11-05 (3)）。

## v0.23 — 2026-11-04

**主面板 UI 细节修复**

- 选项卡 `Segmented` 增加 `NoWrap`，GPU 筛选与最小占用阈值段控加宽，文字不再换行。
- `DetailBar` 背景色对齐卡片底色，操作按钮不再出现黑色矩形底块。
- 主面板「设置」按钮改为与顶栏「系统进程 / 显示后台」同高同基线，去掉额外投影高度。

## v0.22 — 2026-11-04

**主面板复验修复版**

- 修复主面板名称/PID 排序方向反向。
- 修复切换深浅主题后搜索框底色/文字不跟随。
- 修复 explorer 等关键进程按显示名匹配不到、风险提示不出现。
- 修复当前优先级为 REALTIME/HIGHEST 时，优先级对话框默认落到「空闲」。
- 修复磁盘图标流关闭后的 GDI+ 生命周期风险。
- 修复动作探针 `isSuspended` 输出语义、恢复 PASS 判定和牺牲进程残留。
- 多显卡筛选改为紧凑标签，避免 1280 宽下挤爆；关闭主面板入口时同步隐藏托盘菜单与详情面板图标。
- `NtQuerySystemInformation` 解析边界改为完整 0x68 字节。

## v0.21 — 2026-11-04

**主面板阶段 4：管控（二次确认 / 结束·挂起·恢复·优先级 / actions 日志 / 中文错误提示）**

方案：`docs/10-main-dashboard-plan.md` §7（阶段 4 + 安全规则），进度与验收数据：`docs/14-phase4-progress.md`。

- **新增 `Metrics/ProcessActions.cs`**：管控执行层（静态类）。四动作全走 P/Invoke 最小权限句柄：
  `TerminateProcess`（+ WaitForSingleObject 3s 确认真的退了）、`NtSuspendProcess`/`NtResumeProcess`
  （ntdll）、`SetPriorityClass`（五档：空闲/低于标准/标准/高于标准/高，不提供实时档）。
  错误码 → 中文映射（Win32 + NTSTATUS 两张表）：权限不足 / 已退出 / 句柄失效 / 需要管理员。
  每次管控动作（含失败）追加写 `%APPDATA%\WMonitor\dashboard-actions.log`。
- **新增 `UI/ActionConfirmDialog.cs`**：二次确认对话框（无边框自绘，深浅主题跟随）——
  动作标题 / 目标（名称·PID·路径）/ 影响范围 / 关键进程红色风险提示（explorer 等外壳组件）/
  受限进程提示 / 优先级五档段控 / 批量勾选 / **必勾确认框（勾选前确认按钮禁用）**。
  操作失败不关对话框：红字回显原因，可重试或取消。附 `MessageDialog`（结果提示）与 `ConfirmCheck`。
- **DetailBar 动作按钮行**：打开位置（explorer /select，非管控不确认）/ 优先级 / 挂起⇄恢复
  （互斥 label）/ 结束任务（红色 `BentoButton.PrimaryColor`）。系统进程的管控按钮禁用
  （双保险：表单侧再拦一次）；受限/提权进程不禁用，失败给中文原因。meta 行加「已挂起」。
- **安全规则落地**（plan §7）：应用聚合行默认只结束主 PID，「同时结束全部 N 个成员」需显式勾选；
  结束任务确认必须勾选；挂起/恢复按钮按真实挂起状态互斥（`ProcessActions.IsSuspended` 线程级
  判定，只查选中行 + 2s TTL 缓存，不进采样预算）。
- **实测坑**（docs/14 §3.11–13）：死 PID 的 `OpenProcess` 报 5 而非 87（先取 LastWin32Error、
  再 EnumProcesses 判存在）；zombie 进程 `TerminateProcess` 也报 5（句柄已置信号 → 报「已退出」）；
  `K32EnumProcesses` 缓冲区要 4096（1024 在 500+ 进程机器上截断误判）。
- **新增诊断**：`--dashboard-action-probe`（无 UI 端到端：牺牲子进程 挂起→验证→恢复→优先级→结束，
  另测死 PID 与 pid=4 错误映射）；`--dashboard-action-ui-probe`（真面板+真对话框同代码路径执行，
  config 用后恢复）。对话框截图工具 `D:/Hermes/tmp/pwdlg.ps1`（PS1 含中文必须带 BOM 或纯 ASCII）。


## v0.20 — 2026-11-04

**主面板阶段 3：应用识别与体验（真图标 / 系统进程三层判定 / 受限进程真名 / 多卡筛选 / 最小占用阈值）**

方案：`docs/10-main-dashboard-plan.md` §3.4/§7，进度与验收数据：`docs/13-phase3-progress.md`。

- **新增 `Metrics/IconCache.cs`**：三层图标缓存 —— 内存 LRU（约 220 个 48px，含负缓存项）→
  磁盘 `%APPDATA%\WMonitor\icon-cache\<sha1(小写路径)>-<mtimeTicks>.png`（exe 更新自动失效，
  同名 `.miss` 记录「抽不到」防反复重试，目录超 512 文件删最旧）→ `SHDefExtractIcon` 现抽
  （48px，DrawIcon 到透明 Bitmap 保 alpha）。绘制时按可见行查表，每 50ms 窗口最多现抽 2 个
  （超出回退首字母圆点、下帧补上），摊薄 < 0.5 ms/tick（plan §5 图标预算）。无图标资源的
  进程（svchost 等）回退字母圆点。表格与详情条都接了缓存。
- **受限进程真名 + ParentPid**（`ProcessSampler.cs` + `Interop/Native.cs`）：新增
  `NtQuerySystemInformation(SystemProcessInformation)` 按 x64 偏移解析镜像名 / 父 PID / 会话 ID
  （PPL / 提权进程也可读，任务管理器同源）。「PID n（受限）」占位彻底移除 —— 现在显示
  `dwm.exe（受限）` + 「路径不可读」；`ParentPid`（阶段 1 欠账）全量补齐。刷新按需触发
  （首帧 ∨ 真新建 PID ∨ 60s 维护，500ms 节流）：一次全量实测 ~6ms，PID 信息终身不变，
  graveyard 复活不算新 PID；稳态采样保持 3.1–3.4 ms（预算 ≤4）。
- **系统进程三层判定**（替换单一名称黑名单）：① token owner SID 前缀（S-1-5-18/19/20 精确 +
  S-1-5-80- 每服务虚拟账户 + S-1-5-96- 窗口管理器；OpenProcessToken 在 QUERY_LIMITED 句柄即可）；
  ② NtQuery 的 SessionId==0（服务全在会话 0 —— 非提权读不到服务进程 token，实测 334 个可读
  owner 全是同用户 SID，这层是主力）；③ 名称黑名单兜底且改为**无扩展名比较**（修掉 v0.18 起
  "svchost" 永远匹配不上 "svchost.exe" 的存量 bug）。默认列表不再出现 dwm / svchost / audiodg。
- **窗口识别增强**（`WindowScanner`）：每个 PID 按面积选「主窗口」（可见 + 非工具 + 未 cloak，
  无 owner 或 WS_EX_APPWINDOW 才算应用主窗），标题取主窗口而非 z 序首个；
  代表进程评分改为 前台(8) > 应用主窗(4) > 有窗口(2) > CPU/GPU 活跃(1)。
- **多卡筛选**（`GpuProcessSampler.cs` 快照新增 `Luids` per-(pid,luid) 行）：LUID 归一小写
  合流引擎（小写）/ 显存（大写）两族实例名，比较一律 OrdinalIgnoreCase（docs/12 §5.4）。
  面板多卡时显示下拉（全部显卡 / GPU N + LUID 尾号），选中后行内 GPU/显存改取该卡数值
  （多卡进程跨卡求和）；单卡隐藏；选择持久化 `DashboardGpuLuid`；筛空显示「切回全部显卡」提示。
- **最小占用阈值**（`DashboardMinimumPercent` 落地）：顶栏段控 关 / ≥0.5 / ≥1 / ≥5；
  CPU 与 GPU 都低于阈值才隐藏（零负载后台项会被过滤，任一活跃即显示），GPU 视图同样生效。
  顶栏补「系统进程」Chip（此前 `DashboardShowSystemProcesses` 无 UI 入口）。
- **搜索框占位符修复**（v0.18 起存量 bug）：占位文字原在父控件 OnPaint 绘制，被内嵌 TextBox
  的不透明底色盖住只剩 2px 抗锯齿边。换 `EM_SETCUEBANNER` 由系统在 TextBox 内绘制。
- **诊断参数**：新增 `--dashboard-icon-probe`（图标三层缓存验证：抽取耗时 / LRU 命中 /
  磁盘缓存 / 负缓存与目录状态）；`--dashboard-probe` 增加系统进程计数、owner/parent 覆盖率、
  主窗口数、受限进程命名、per-LUID 分组数与 NtQuery 耗时。

**验收数据（本机 498–512 进程、RTX 3060 / 64GB、3 个 LUID）**

- 稳态进程采样 3.1–3.4 ms（预算 ≤4 ✓）；NtQuery 刷新帧 ~9ms 一次性成本，按需触发摊薄可忽略。
- `--dashboard-icon-probe`：16 路径 12 OK / 4 真 MISS（svchost 等无图标资源，负缓存生效）；
  首抽 0.2–17.8 ms，LRU 命中 0.00–0.03 ms，磁盘 PNG 跨运行复用。
- 受限进程 166/166 全部有真名；sys=171 / sysService=156（svchost / audiodg / dwm 全部命中）。
- UI 截图四连（默认按应用 / 进程+系统+后台+阈值≥1 / GPU 视图筛空卡 / GPU 视图筛 0eAB）：
  真图标、Chip、段控、多卡下拉、占位符、dwm.exe（受限）全部就位。

## v0.19 — 2026-11-04

**主面板阶段 2：GPU 进程指标（GPU / 显存列 + GPU 视图真表格）**

方案：`docs/10-main-dashboard-plan.md` §3.2/§7，进度与验收数据：`docs/12-phase2-progress.md`。

- **新增 `Metrics/GpuProcessSampler.cs`**：独立 PDH query 读 `\GPU Engine(*)\Utilization Percentage`
  与 `\GPU Process Memory(*)\Dedicated/Shared Usage`（不与全局采样器共享 query，plan §10.6）；
  实例名 `pid_N_luid_..._phys_N_eng_N_engtype_X` span 解析（PID 为稳定锚点，engtype 保留原文）；
  按 PID+LUID 聚合，利用率 3 帧滑动平均（快照按 PID 合行，多卡进程 sum util、主适配器取单卡
  util 最高者）；PDH 格式化数组缓冲 grow-only 复用；只有显存、无引擎活动的进程也出行；
  collect 失败保留旧值并降级标注；连续 3 帧 >12 ms 置 Overloaded，Hub 自动拉长子采样间隔
  （plan §5 降级）。GPU Process Memory 实例在 Win11 已无 engtype 段（`pid_N_luid_X_phys_0`）。
- **主面板数据中枢**（`AppMetricsHub.cs`）：按 `DashboardGpuRefreshMs`（默认 2s）做 0.5Hz
  GPU 子采样，两次采样间沿用旧值；GPU 四字段（util / 专用 / 共享 / 引擎类型）合入
  `ProcessMetric` 并在按应用视图求和（引擎类型取 util 最高成员）。
- **表格六列**（`DashboardTable.cs`）：应用 | PID | CPU | 内存 | **GPU** | **显存**；
  GPU 列 ≥1% 强调色加粗（同 CPU 口径），无 GPU 活动显示 —；显存列以专用显存为主值。
  `DashboardSort` 枚举新增 `Gpu` / `Vram`（旧 config.json 向后兼容）。
- **GPU 视图真表格**（`MainDashboardForm.cs`，替换阶段 1 占位 Label）：按 PID 列出 GPU
  活动进程（util > 0 或专用显存 > 0；GPU 计算进程通常无窗口，此视图忽略「显示后台」过滤，
  搜索与系统进程过滤仍生效）；选中键与按进程视图共用，跨视图选中不丢。
- **详情条**：选中行显示 GPU%（引擎类型）与专用/共享显存。状态栏显示 GPU 项数与 GPU
  采样耗时；Overloaded 时显示「GPU 暂缓刷新」。
- **诊断参数**：新增 `--dashboard-gpu-probe`（8 帧 × 2s 单独验证 GPU 解析，per-LUID 显存
  合计与全局对照）；`--dashboard-probe` 增加 GPU 项数/实例数/耗时。

**验收数据（本机 491–506 进程、RTX 3060 / 64GB、3 个 LUID）**

| 项 | 结果 |
|---|---|
| 编译 | 0 error 0 warning |
| GPU 采样耗时 | 稳态 1.5–5.2 ms/帧（预算 ≤8 ms 达标；首帧 ~10 ms 冷缓冲属预期），负载 1072 实例时 4.5 ms |
| 显存数值 | 逐 PID 与 typeperf 地面真值一致（dwm 4009MB / msedge 912MB / chrome 201MB） |
| GPU 利用率 | ffmpeg d3d11va 4K60 硬解：本面板 80.0%→77.7%（滑动平均）vs Get-Counter 明细 3D 55.4%+VideoDecode 21.9%=77.3%，TopEngine=3d 归因正确 |
| 阶段 1 无回归 | dupPids=0、distinctKeys=refDistinct=256；进程采样 3.2–8.1 ms（基线 3.1–5.5） |
| UI 截图 | GPU 视图（负载下 ffmpeg 置顶 78.1% 绿色加粗）与按应用视图六列均正常 |
| 已知口径差 | 进程侧 Σ专用显存 > 适配器值（WDDM 专用提交 vs 驻留，任务管理器同样）；「Ardot」进程驱动上报 ~290 GB 专用显存（OS 数据本身如此，任务管理器同源同怪） |

---

## v0.18 — 2026-11-04

**主面板阶段 1：资源管控面板 · 只读监控（按应用 / 按进程 的 CPU 与内存）**

方案：`docs/10-main-dashboard-plan.md`，进度与验收数据：`docs/11-phase1-progress.md`。

- **新增资源面板**（`UI/MainDashboardForm.cs`，普通 Form）：顶部 CPU / 内存 / GPU / 显存
  四张总览卡（复用主循环全局快照，不重复建 PDH）；「按应用 / 按进程 / GPU」视图切换
  （GPU 视图阶段 2 占位）；搜索（名称 / 路径 / PID / 文件描述）；列头点击排序（升降切换）；
  选中行底部只读详情（路径 / 公司 / 描述 / 启动时间 / 架构 / 成员 PID 列表）；
  状态栏显示进程数 / 应用组数 / 采样耗时 / 最近刷新。顶栏有「刷新 跟随·0.5s·1s·2s」、
  「显示后台」开关（默认关 = 只显示有可见窗口或前台的应用）与设置入口。
- **按应用聚合**（`Metrics/AppMetricsHub.cs`）：以完整路径为键（同名不同路径不合并），
  聚合 CPU / WorkingSet / PrivateMemory；代表进程 = 前台 > 有可见窗口 > CPU 最高；
  显示名取 FileDescription > ProductName > 进程名（`Metrics/VersionInfoCache.cs`，TTL 15 秒
  含失败负缓存）。Chrome ×25、Feishu ×10 等多进程应用聚为一行。
- **进程采样**（`Metrics/ProcessSampler.cs`）：`EnumProcesses` + `OpenProcess(QUERY_LIMITED)`
  句柄缓存；CPU 按 `GetProcessTimes` 差分归一化到整机；内存走
  `K32GetProcessMemoryInfo`（**必须传 `PROCESS_MEMORY_COUNTERS_EX` 的 72 字节 cb**，
  传普通 COUNTERS 的 64 字节会报 `ERROR_INSUFFICIENT_BUFFER` —— 本版实测踩坑，见
  `Interop/Native.cs` 注释）；路径 TTL 15 秒、架构 `IsWow64Process2` 一次性；
  每帧一次 EnumWindows 记录可见窗口 / 前台（cloak 过滤）；消失进程保留 12 秒防闪烁。
- **生命周期**（plan §4.3）：打开面板才创建 hub，关闭即 Dispose（采样定时器停止、句柄关闭、
  一次性 GC 回收缓存）；不打开时零开销。窗口大小 / 位置可记忆（`DashboardWindowRect`）。
- **入口**：托盘右键菜单「资源面板…」、详情面板标题旁四格图标；`AppConfig` 新增
  `Dashboard*` 字段 10 个（强类型枚举 `DashboardView` / `DashboardSort`）。
- **诊断参数**：`--dashboard`（启动即开面板）、`--dashboard-probe`（无 UI 空跑 8 帧 →
  `D:/Hermes/output/results/wmonitor-dashboard-probe.txt`）。

**验收数据（本机 506 进程、RTX 3060 / 64GB）**

| 项 | 结果 |
|---|---|
| 编译 | 0 error 0 warning |
| probe 采样耗时 | 稳态 3.1–5.5 ms/帧（预算 ≤4 ms，偶发超出为 GC/系统抖动；首帧 40 ms 为句柄冷启动） |
| 聚合正确性 | 264 组 / 无重复 / dupPids=0；Chrome 2.2 GB、ZCode 3.2 GB 与任务管理器一致 |
| 面板打开增量 | 私有内存 +13 MB（预算 15–30 MB）；CPU 均值 0.17%（预算 ≤1%） |
| 面板关闭 | 采样停止（CPU 增量归零）；私有内存残差 ~12 MB（JIT + 托管堆段常驻，非泄漏） |
| error.log | 无异常 |

**产物**

| 文件 | 大小 |
|---|---|
| `D:\WMonitor\WMonitor\`（安装版，多文件） | 415 KB |
| `WMonitor-v0.18-framework.exe` | 415 KB |
| `WMonitor-v0.18-portable.exe` | 63.0 MB |


## v0.17 — 2026-11-03

**修复设置交互时序、贴片消失恢复与多屏实时切换**

- **设置面板点击假死 / 无反应但已生效**：由 `MouseDown` 触发的整窗重排不再同步执行，
  改为延迟一帧；否则 `SuspendLayout / SetBounds / 强制重绘` 会吞掉后续 `MouseUp / Click`。
  受影响的“记录历史”“跨重启保留”“启用告警”“运行脚本”等开关都切到延迟布局。
- **贴片偶发消失自动恢复**：每 tick 除 WinForms 可见性外，还检查原生 `IsWindowVisible`
  与 DWM Cloak 状态；必要时强制 `Show()`、重新置顶并 `UpdateLayeredWindow`。
  隐藏状态会清掉恢复标记，避免下一次无法触发展示。
- **屏幕选项实时切换**：在设置里选屏幕 1 / 屏幕 2 / 全部时，立即重新枚举任务栏并重建贴片，
  不再等下一次采样周期或关闭设置窗口。
- **“所在屏幕”仅多屏显示**：单主屏时不展示无意义的屏幕选择，连接并识别到第二块屏后自动出现。
- **贴片设置分栏适配**：“位置 / 风格”和“所在屏幕 / 水平微调”按跨两列卡片的实际内宽均分，
  修复“四环”被裁剪，也避免过度缩窄留下大面积空白。

**产物**

| 文件 | 大小 |
|---|---|
| `D:\WMonitor\WMonitor\`（安装版，多文件） | 373 KB |
| `WMonitor-v0.17-framework.exe` | 373 KB |
| `WMonitor-v0.17-portable.exe` | 62.9 MB |


## v0.16 — 2026-10-31

**托盘交互重做：左键开/关详情面板、右键换成自绘现代菜单、面板补齐显示项**

- **左键托盘图标 = 显示 / 关闭详情面板**（去掉了原来的「双击开设置」，设置仍可从菜单、面板齿轮进入）。
  双击会连发两次 MouseUp，350 ms 去抖后只算一次点击；从托盘打开时面板给 900 ms 失焦保护 ——
  点托盘本身会把焦点还给任务栏/溢出面板，紧接着的 Deactivate 会把刚弹出的面板关掉。
- **右键菜单改自绘**（`UI/FlyoutMenu.cs`，分层窗口 + 真 alpha）：12 px 圆角、外围自绘阴影、
  34 px 行高、hover 圆角块、开关项右侧拨动开关、单选项左侧勾号、子菜单 V 形箭头，深/浅主题各一套配色。
  **选项和分组与 v0.15 的 ContextMenuStrip 完全一致**（贴片位置 / 贴片所在屏幕 / 贴片风格 / 托盘图标
  [样式 + 主指标 + 次指标] / 主题 / 显示项 / 告警 / 详情面板… / 设置… / 暂停刷新 / 退出）。
  菜单 `WS_EX_NOACTIVATE`，右键不再把用户当前窗口踢到后台；用 WH_MOUSE_LL / WH_KEYBOARD_LL
  低级钩子判断「点在菜单外」和 Esc 来收起（`Deactivate` 关不掉不抢焦点的窗口）。
- **详情面板按「显示项」列行**：CPU / 内存 / GPU / 显存 / **网速** / 磁盘 各对应一个开关，
  关掉不画、面板高度随之变化；网速行给了 ↓/↑ 速率、对数刻度进度条与历史曲线（`HistoryMetric.Net`）。
  顺带补了显存与网速的图标，全关时至少保留 CPU 行。
- **托盘卡预览位置固定**：切单指标 / 双数字 / 双环时，隐藏的指标行照样占位，预览行永远停在
  「四段条（四行全在）」的位置，不再随少显示的行上下漂（v0.15 里是「在剩余空间居中」）。
- 诊断钩子新增 `--menu`（配合 `WMONITOR_MENU_SUB=<行号>` 展开子菜单），用来截图验收菜单，
  不必模拟鼠标。

**修复**

- 单实例判断从「Mutex 对象是否存在」改成「抢持有权」（`WaitOne(0)`，`AbandonedMutexException` 直接接管）。
  以前只要有一个残留句柄 —— 例如另一个版本的单文件 exe 还在跑、或调试时用 `OpenExisting` 探过
  —— 新实例就静默退出，用户看到的是「双击没反应」。现在被真实例占用时会明确弹出「已经在运行」。

**产物**

| 文件 | 大小 |
|---|---|
| `D:\WMonitor\WMonitor\`（安装版，多文件） | 362 KB |
| `WMonitor-v0.16-framework.exe` | 363 KB |
| `WMonitor-v0.16-portable.exe` | 62.9 MB |


## v0.15 — 2026-10-31

**滚动条闪现的真正修法：锁死托盘卡自身高度 + 平时关掉 AutoScroll**

前一版（v0.14）锁了整体窗口、把高度摊给上方卡片，属于把搅动换个地方 —— 滚动条照样有机会出现。

- **托盘卡高度按"四行都在"算**：卡片固定 374 高，切样式时只换行，网格里没有任何卡片改变尺寸，
  窗口自然也不动。少显示的两行留下的空白由预览行居中吸收（预览区看起来是留白，不是空洞）。
- **`_host.AutoScroll` 平时直接关掉**：只有内容真的超过工作区高度才打开。容器没开滚动，
  就不可能在任何一帧弹出一条代理滚动条 —— 用户看到的那条就是它。
- 布局顺序仍是「先定尺寸、再摆控件、最后设滚动范围」，配合 `WM_ERASEBKGND` 用窗口底色擦，
  窗口万一还要变（开/关告警、历史）也不会闪白。
- 撤销 v0.14 中把隐藏行高度均摊给三行卡片的做法（上方卡片高度恢复原样）。

**验证**：60 fps 录屏，双数字 ↔ 四段条 来回切两次共 102 帧，逐帧统计客户区右边缘 24 px 的
亮像素 —— 每帧都是 3（一直只有那 3 个固定像素），没有任何一帧出现滚动条。

**产物**

| 文件 | 大小 |
|---|---|
| `D:\WMonitor\WMonitor\`（安装版，多文件） | 362 KB |
| `WMonitor-v0.15-framework.exe` | 363 KB |
| `WMonitor-v0.15-portable.exe` | 62.9 MB |

---

## v0.14 — 2026-10-31

**切换托盘样式不再改窗口尺寸（修"框变大闪白条"）**

- **窗口高度锁死**：托盘卡的第三 / 第四指标行隐藏时，它们的高度照常计入，再均摊到三行卡片上。
  于是 双数字 / 双环 / 四段条 / 单指标 四种状态窗口都是 1254×925，切样式只是卡片内部重排，
  **窗口一次都不缩放** —— 缩放那一帧最容易闪白。
- **擦背景改用窗口底色**：WinForms 窗口类的默认擦除刷是系统窗口色（白），窗口长高时新露出的
  那一条会先被它刷白。现在拦 `WM_ERASEBKGND` 自己用 `WindowBg` 擦，尺寸改完立刻 `Invalidate + Update`
  同步画完，不留没画过的区域给合成器。
- **自绘控件自己刷底色**：`Segmented` / `Chip` / `Slider` / `Stepper` 之前没重写 `OnPaintBackground`，
  从隐藏变可见的第一帧会用默认白底刷一遍（切到四段条时新出现的那两行就是这么白的）。
  现在统一用 `Parent.BackColor` 擦。
- 布局顺序调整：先定窗口/容器尺寸，再摆控件，最后才设 `AutoScrollMinSize`（平时留空），
  杜绝中间那一帧"内容比视口高"导致系统滚动条闪现。

**产物**

| 文件 | 大小 |
|---|---|
| `D:\WMonitor\WMonitor\`（安装版，多文件） | 362 KB |
| `WMonitor-v0.14-framework.exe` | 363 KB |
| `WMonitor-v0.14-portable.exe` | 62.9 MB |

**验证**：双数字 ↔ 四段条来回切，窗口始终 1254×925、位置不动；60 fps 录屏逐帧统计亮像素，
切换前后无任何帧出现大面积白块（旧版同样测过，未复现 —— 见 `05-known-issues.md`）。

---

## v0.13 — 2026-10-31

**托盘卡片加实时预览；按钮投影/浅色配色修正**

- 「托盘图标」卡片底部加**预览**：用 `TrayIconRenderer` 按 40 px 画出当前样式 + 当前指标槽位，
  旁边列出槽位名（如「显存 · GPU」）。换样式或换指标立刻重画，不用盯托盘看效果
  （数值是示意数据 —— 设置窗口里没有采样器）。
- 预览行同时把这张卡的高度撑到和旁边两张卡齐平：v0.12 里双数字/双环只有两行指标，
  卡片被隔壁「行为」撑高，切回二段样式看着像"没收缩"。现在 双数字 272 → 四段条 356，
  窗口高度 841 ↔ 925，切换时肉眼可见。
- **按钮投影重做**：原来从外向内越画越深，最外圈是最重的一层，会在按钮下面留一条硬边
  （浅色底特别明显）。改成外圈最淡、内圈最深，10 层渐隐；常态完全不画投影（平贴），
  hover 才抬起。
- **浅色主题次按钮配色**：底色从 `#E8EAEE`（和窗口底色几乎一样，糊在一起）改成
  `#D8DEE6`，新色位 `Theme.SoftLight`；主按钮投影在浅色主题改用中性黑，不再带蓝。

**产物**

| 文件 | 大小 |
|---|---|
| `D:\WMonitor\WMonitor\`（安装版，多文件） | 361 KB |
| `WMonitor-v0.13-framework.exe` | 362 KB |
| `WMonitor-v0.13-portable.exe` | 62.9 MB |

**验证**：深/浅两套主题下截图核对按钮常态、hover、按下三态；双数字 ↔ 四段条来回切换，
窗口 841 ↔ 925、预览图标从两行数字变成四段条；贴片与内存（48 MB）正常。

---

## v0.12 — 2026-10-31

**托盘图标：每种样式各存一套指标**（此前四个样式共用一套槽位，切样式等于换皮不换内容）

- 新增 `TraySlotConfig`（Primary / Secondary / Third / Fourth），配置里按样式各存一份：
  `TrayDualNumber` / `TrayDualRing` / `TrayFourBar` / `TraySingle`。
  现在可以「双数字 = 显存 + GPU」「双环 = CPU + 内存」「四段条 = 显存/GPU/内存/CPU」，
  互不影响；切样式时设置窗口会立刻换成那套样式自己存的值。
- 读取入口统一成 `AppConfig.TrayCurrent`（当前样式）/ `TrayFor(style)`；
  `TraySlots` 改为按当前样式取槽位，`TrayIconRenderer` 不用改（它本来就只读 `TraySlots`）。
- 旧配置自动迁移：文件里没有 `TrayDualNumber` 键时，把原来的全局槽位原样铺给四个样式
  （升级前后图标不变），之后各改各的。旧字段 `TrayPrimary…TrayFourth` 只为迁移保留。
- 托盘右键菜单的「主指标 / 次指标」跟着当前样式走（以前改的是全局值）。
- 设置窗口「托盘图标」卡片右上角加注「每种样式各存一套指标」，避免再误以为是全局设置。

**修复**

- 配置文件里只要有一个非法值（例如手改 `Anchor` 写成枚举里不存在的名字），整份 `config.json`
  反序列化就会失败。以前 `Load()` 是静默 `catch { }` 直接返回默认值，表现是「所有设置突然被重置」，
  而且没有任何提示。现在坏文件会另存为 `config.json.bad`，原因写进 `error.log`，
  启动后弹一次托盘气泡说明本次用的是默认设置 —— 不再无声丢配置。

**产物**

| 文件 | 大小 |
|---|---|
| `D:\WMonitor\WMonitor\`（安装版，多文件） | 351 KB |
| `WMonitor-v0.12-framework.exe` | 360 KB |
| `WMonitor-v0.12-portable.exe` | 62.9 MB |

**验证**：迁移后双数字仍为「显存 + GPU」；把双数字主指标改成 CPU、再切双环，双环仍是「显存 + GPU」，
互不串值；四段条显示独立的四行槽位；配置 JSON 里四个样式各存一份。

---

## v0.11 — 2026-10-31

**设置窗口重做：宽面板 + Bento 卡片网格**（纯 UI 改动，采样/告警/历史逻辑一行没动）

- 原来 400 宽单列长滚动（内容 1000+ px，1080p 屏要滚三屏）改成 **1238 × 约 850** 的四列网格，
  正常配置下**完全不出现滚动条**；条件行出现/消失时窗口自动伸缩。
- 七张卡片，按跨列数拼：

  ```
  ┌──────────────────┬────────┬────────┐
  │ 贴片 (2)         │ 外观   │ 显示项 │
  ├──────────────────┼────────┼────────┤
  │ 托盘图标 (2)     │ 历史   │ 行为   │
  ├──────────────────┴────────┴────────┤
  │ 告警 (4)                           │
  └────────────────────────────────────┘
  ```

- 告警从「5 行竖排」改成 **5 列横向**：每列一个指标 chip + 阈值(%) + 持续(秒)，一眼看全 5 条规则。
- 顶栏（WMonitor + 版本号）/ 底栏（恢复默认 · 完成）独立于卡片网格。
- 新控件 `UI/Bento.cs`：`BentoCard` 圆角底板。圆角不用 `Region`（硬裁切有锯齿），
  而是先铺窗口底色、再画抗锯齿圆角路径，所以边缘干净；`BackColor` 保持卡片色，
  子控件 `g.Clear(Parent.BackColor)` 才不会有方块补丁。
- 卡片标题带一个指标色小圆点（CPU 蓝 / 内存紫 / GPU 绿 / 显存橙 / 历史青 / 行为粉 / 告警红）。
- `Theme` 新增 `WindowBg / CardBg / CardBorder / Soft / Accent` 五个色位，深/浅两套都配好。
- 布局模型：每张卡维护「行列表」（行高 + 显示条件 + 单元格坐标），
  `LayoutCards()` 先算内容高度，再按网格行取最大值，最后把多出来的高度按行间 28 px 上限摊掉，
  内容贴顶不飘。新增设置项只要 `AddRow`，不用碰窗口尺寸。
- **按钮重做**：`BentoButton`（自绘，无描边，圆角 10）。hover 提亮 + 投影抬高（主按钮带蓝色晕），
  按下压暗并把文字下沉 1.5 px；次按钮底色和文字色分开配，深/浅两套都保证看得清。
  系统 `Button`（FlatStyle.Flat）画不出圆角，也压不住 hover，所以直接继承 `Control` 自己画，
  实现 `IButtonControl` 后仍可当 `AcceptButton`（回车 = 完成）。

**产物**

| 文件 | 大小 |
|---|---|
| `D:\WMonitor\WMonitor\`（安装版，多文件） | 350 KB |
| `WMonitor-v0.11-framework.exe` | 355 KB |
| `WMonitor-v0.11-portable.exe` | 62.9 MB |

**验证**：贴片正常绘制（CPU/RAM/GPU/VRAM），私有内存 44 MB，`v0.11` 版本号在设置窗口右上角；
深色 / 浅色、双数字 / 四段条 / 单指标、历史开关、告警开关六种组合都截图核对过排版；
hover / 按下态实拍确认（提亮 + 投影 + 文字下沉）；点「完成」能关窗（`BentoButton` 要自己把
`DialogResult` 交给 Form，漏了这步窗口关不掉 —— 已补）。

---

## v0.10 — 2026-10-31

大版本：从「一块屏的贴片」扩到「多屏 + 有记忆 + 会报警」。

**新增**

- **副屏贴片**：枚举全部任务栏（`Shell_TrayWnd` + 每屏 `Shell_SecondaryTrayWnd`），
  设置里选「屏幕 1 / 屏幕 2 / 全部」。每片贴片按**所属显示器**的有效 DPI 排版
  （不再用 `DeviceDpi` —— 分层窗口在 Blit 前还没定位到目标屏，混合 DPI 下会取错）。
- **磁盘 IO**：PDH `\PhysicalDisk(_Total)\Disk Read/Write Bytes/sec`，贴片加 `R`/`W` 两格，
  面板第 5 行（读/写速率 + 曲线），托盘图标也可选「磁盘」。
- **滚动历史**：面板曲线支持 1 分钟 / 5 分钟 / 1 小时 / 24 小时四档。
  内存环 1 小时（144 KB），磁盘按天滚动 `history\YYYYMMDD.hist`（40 B/条，3.4 MB/天）。
  设置里有三层开关：总开关 → 跨重启保留 → 保留时长 6 小时 / 24 小时 / 3 天。
  关掉总开关是**真零开销**：不实例化、不建目录、面板退回 v0.04 的 60 点实时缓冲。
- **阈值告警**：5 条内置规则（CPU / 内存 / GPU / 显存 / GPU 温度），每条可设阈值与持续秒数。
  状态机 Idle → Armed → Firing，带迟滞（恢复线低 5% / 3 °C）和冷却期（默认 300 s）。
  四种动作：贴片整格变红、托盘图标红环闪烁、系统通知、运行脚本
  （脚本走环境变量 `WMONITOR_METRIC/VALUE/THRESHOLD`，fire-and-forget 不阻塞 UI）。
  托盘菜单可「静音 1 小时」（只停派发，贴片仍然红）。
- **锁定位置 / 点击穿透** 两个开关。穿透后贴片不再接收鼠标，面板改从托盘菜单打开。
- 设置窗口改成滚动容器（内容已超过 1080p 屏高），新增「历史记录」「告警」两个分区。

**修复**

- 面板曲线 `DrawLines` 偶发 `OverflowException`（v0.04 遗留）：NaN/Inf 坐标现在兜到边界值。
- 历史文件读取用 `FileShare.ReadWrite | Delete` —— 应用自己握着今天文件的写句柄，
  用 `File.ReadAllBytes` 会共享冲突，表现为「历史永远是空的」。

**实现要点**

- `TaskbarInfo` / `ScreenRect` 替代直接暴露 `Native.RECT`（`Native` 是 internal）。
- 贴片每 tick 重读任务栏矩形（`RefreshBarRect`），自动隐藏 / 换分辨率 / Explorer 重启照旧自愈。
- 历史抽稀每桶取最大值；首尾空桶保留 0（向前填充会把刚启动时的 24 h 窗口画成一条平线）。
- 24 h 视图每次要读 3.4 MB，面板里做了 10 s 缓存，不会每秒读盘。
- `ResetDefaults` 不再用 chip 下标映射，改成「注册时带默认值访问器」，加控件不会错位。

**产物**

| 文件 | 大小 |
|---|---|
| `D:\WMonitor\WMonitor\`（安装版，多文件） | 350 KB |
| `WMonitor-v0.10-framework.exe` | 347 KB |
| `WMonitor-v0.10-portable.exe` | 62.9 MB |

**验证**：稳态 CPU 0.26%（30 s 均值），私有内存 44–47 MB；`--alert-test` 三条断言 PASS；
24 h 历史文件 22 KB / 约 9 分钟；`--monitors` 正确识别显示器与 DPI。

---

## v0.04 — 2026-10-29

**修复**

- 托盘「双数字」图标数字太小看不清：16 px 图标里两行数字原本只有 7.4 px 字、还夹一条分隔线。
  现在字号 0.58×图标边长（16 px → 9.3 px），去掉分隔线，行中心在 0.25 / 0.75 高度。
  另外整条渲染链路改成 4 倍超采样后缩回（`BoostAlpha(1.35)` 补回边缘 alpha），笔画不再发灰发虚。
- 设置窗口「贴片水平微调」上方只有 4 px 间距、挤在胶囊开关下面：改成 16 px。
- 水平微调的数值框是系统 `NumericUpDown`（白底 + 原生上下箭头），跟这套深色自绘 UI 完全不搭：
  换成自绘 `Stepper`（− 值 +，圆角、跟主题、按住连续步进）。

**新增**

- **托盘图标可选指标**：双数字 / 双环可分别指定主指标、次指标；单指标用主指标；
  四段条可指定 4 个指标。可选 CPU / 内存 / GPU / 显存 / 网速 / GPU 温度。
  设置里是两到四行分段控件（随样式显示/隐藏），托盘右键菜单也加了「主指标 / 次指标」子菜单。
- **贴片可隐藏**：设置里「显示贴片」开关、托盘菜单「显示项 → 任务栏贴片」。
  关掉后只剩托盘图标；此时点托盘菜单「详情面板…」会以鼠标位置为锚点弹出。
- `WMonitor.exe --icons` 诊断模式：把 4 种托盘图标渲染成「真实尺寸 + 8 倍放大」对比图，
  写到 `D:/Hermes/output/results/wmonitor-icons.png`，方便以后调小图标。

**实现要点**

- `AppConfig.TraySlots` 按样式给出 1/2/2/4 个指标槽位；`TrayMetric` 是新枚举（JSON 里存名字，旧配置自动补默认值）。
- 网速/温度没有百分比：网速用对数刻度（20 KB/s ≈ 26%，100 MB/s 满），温度 30–100 °C 映射。
- `SettingsForm` 改成块式布局（`Block + LayoutBlocks()`），窗口高度随显示行变化。

**产物**

| 文件 | 大小 |
|---|---|
| `WMonitor-v0.04-framework.exe` | 299 KB |
| `WMonitor-v0.04-portable.exe` | 63 MB |

**验证**：CPU 稳态 0.16%，私有内存 45.8 MB，25 秒内句柄数 386 → 385（无 GDI 泄漏）。

---

## v0.03 — 2026-10-28

**修复**

- 浅色主题下设置窗口大标题（"WMonitor"）是白字白底、几乎不可见：切换主题时只更新了 `Label.BackColor`，没有恢复 `ForeColor`。现在用 `Tag = "header" / "caption"` 区分层级，换主题时一起恢复。
- 设置窗口底部按钮被裁掉 2px：布局累加高度超过 `ClientSize`。窗口高度改 698，按钮下留 28px 边距。

**新增**

- **贴片不透明度 0–100%**：底色和描边一起变，文字不受影响。设置里是滑块，实时预览。

**产物**

| 文件 | 大小 |
|---|---|
| `WMonitor-v0.03-framework.exe` | 288 KB |
| `WMonitor-v0.03-portable.exe` | 63 MB |

---

## v0.02 — 2026-10-28

**修复**

- 点托盘「显示隐藏的图标」箭头后贴片消失：任务栏响应点击时会把自己提到 TOPMOST 组最前，贴片被压到下层。现在每 tick 重新置顶，且 `SetWindowPos` 必须排在 `Blit` **之前**（放之后会清空分层内容）。
- NVML 调用失败时 GPU 利用率是 `NaN`，导致面板曲线 `DrawLines` 抛 `OverflowException`、贴片数值转 `int` 溢出。现在所有 NaN 归零。

**新增**

- 贴片位置：任务栏最左 / 应用图标左边 / 应用图标右边 / 托盘箭头左边 / 自定义。
- 拖动定位：按住贴片拖到任意位置，落在任务栏带内自动吸附垂直居中，落到桌面则悬浮固定。
- 「始终置顶」开关（关掉后允许被其他窗口遮挡）。
- 托盘右键菜单新增「贴片位置」子菜单。

**产物**

| 文件 | 大小 |
|---|---|
| `WMonitor-v0.02-framework.exe` | 279 KB |
| `WMonitor-v0.02-portable.exe` | 63 MB |

---

## v0.01 — 2026-10-28

首个可用版本。项目名从 `SysMonitor` 改为 `WMonitor`，迁到 `D:\WMonitor`。

**功能**

- 任务栏贴片：三种形态（数字条 / 数字 + 迷你柱 / 四环仪表），吸附托盘左侧，跟随任务栏位置、高度、自动隐藏
- 指标：CPU 占用、CPU 频率、内存、GPU 利用率、显存占用/总量、网速（上下行）、GPU 温度、GPU 功耗
- 托盘图标：四种（双数字 / 双环 / 四段条 / 单指标），随数值实时刷新
- 详情面板：四行指标 + 60 点曲线 + 温度/功耗/频率，点面板外自动收起
- 设置窗口：贴片风格、托盘图标、主题（深/浅/跟随系统）、刷新间隔（0.5/1/2/5 秒）、显示项开关、开机自启、水平微调、恢复默认
- 托盘右键菜单：快速切换 + 暂停 + 退出
- 单实例、崩溃日志、`--probe` 诊断模式

**性能**

- 采样 0.3–1.0 ms，重绘 0.6–0.9 ms
- CPU ≈ 0.3%（单核），私有内存 43 MB（框架依赖）/ 78 MB（自包含）

**交付物**

| 文件 | 大小 |
|---|---|
| `D:\WMonitor\WMonitor\`（安装版，多文件） | 278 KB |
| `D:\WMonitor\WMonitor-v0.01-framework.exe` | 276 KB |
| `D:\WMonitor\WMonitor-v0.01-portable.exe` | 63 MB |

**已知限制**：见 `docs/05-known-issues.md`。
