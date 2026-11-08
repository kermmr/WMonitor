# 03 · UI 设计规范（完整版）

> 2026-11-04 重写。此前本页只覆盖贴片/详情面板/设置窗口；重写后为**全项目唯一视觉权威**：
> Part A 设计语言（token / 组件 / 状态 / 规则），Part B 各界面规格，Part C 现状整改清单。
> 与本页冲突的旧约定一律以本页为准。整改清单执行完后，各控件代码里不得再出现内联色值。

**基础约定**

- 所有数值以 **96 DPI** 为基准，代码里统一乘 `k = DeviceDpi / 96f`（主面板 `_s`、设置 `_scale`）。
- 字体族 `Segoe UI`（中文自动回退微软雅黑），**一律 `GraphicsUnit.Pixel`**（走 `Gfx.F`）。禁止默认构造 `new Font(...)`（那是 pt 单位，96 DPI 下 1pt = 1.333px，是本项目的实际踩坑源）。
- 数字/文字全部 GDI+ `DrawString` + `TextRenderingHint.AntiAlias`，图形 `SmoothingMode.AntiAlias`。
- 圆角一律「先铺外层底色，再画抗锯齿圆角路径」，不用 `Region`（硬裁切锯齿）。
- WinForms 控件 `BackColor` 必须全不透明（带 alpha 抛异常），半透明叠加只允许出现在 `OnPaint` 里。

---

# Part A · 设计语言

## A1. 设计原则

1. **自绘优先**：系统控件（Button / NumericUpDown / DataGridView / ContextMenuStrip / TextBox 底色）在深色主题下不可控，一律自绘或包裹。TextBox 只保留输入本体，外壳自绘。
2. **三层底色分层**：`WindowBg`（窗口底）→ `CardBg`（卡片）→ `SecondaryBg`（控件底）。层级靠底色差 + 1px 描边表达，**不靠阴影**。
3. **阴影只给"浮起来"的东西**：按钮 hover、右键菜单、（未来的）浮层。常驻平铺的卡片/面板/表格永远平贴。
4. **深浅同构**：每个 token 深浅两套成对定义，控件代码只引用 token 名，禁止 `Color.FromArgb(255, 32, 36, 42)` 这类内联实色。
5. **无动画**：所有状态变化是下一帧重绘；唯一时间相关行为是详情面板 350 ms 防抖（见 A12）。

## A2. 颜色规范

### A2.1 语义 token 表（`Theme.cs` 现有字段）

| Token | 深色值 | 浅色值 | 用途 |
|---|---|---|---|
| `WindowBg` | `#0E1014` | `#EDEFF3` | 窗口底、卡片之外的一切 |
| `CardBg` | `#191C22` | `#FFFFFF` | 卡片 / 表格 / 面板 / 对话框底 |
| `CardBorder` | `rgba(255,255,255,.07)` | `rgba(0,0,0,.06)` | 卡片 1px 描边 |
| `SecondaryBg`* | `#262A31` | `#D8DEE6` | 次级按钮 / 输入控件底（*现 `Soft`+`SoftLight` 合并，见 C1） |
| `SelectedBg`** | `#3E444E` | `#FFFFFF` | 分段控件选中块（**新增） |
| `HeadBg`** | `#1E2127` | `#F6F7FA` | 表格表头底（**新增） |
| `InputBorder`** | `rgba(255,255,255,.15)` | `rgba(0,0,0,.10)` | 输入/选择控件 1px 描边（**新增） |
| `DisabledBg`** | `#20242B` | `#F0F1F4` | 禁用控件底（**新增） |
| `Text` | `#EEF1F6` | `#141414` | 主文字、选中态文字 |
| `Label` | `#9AA3B2` | `#5C636E` | 次要文字、占位、未选中文字、禁用文字（再叠透明度） |
| `Accent` | `#0A84FF` | `#0A84FF` | 主按钮、选中描边、行选中、焦点环 |
| `Alert` | `#FF453A` | `#D70015` | 危险按钮、告警、错误文字 |
| `Hover` | `rgba(255,255,255,.12)` | `rgba(0,0,0,.08)` | 行 / 菜单项 / 分段内块 hover 叠加 |
| `Track` | `rgba(255,255,255,.13)` | `rgba(0,0,0,.10)` | 进度槽、滑块轨道、禁用按钮内的文字底意 |
| `Sep` | `rgba(255,255,255,.11)` | `rgba(0,0,0,.09)` | 分隔线 |
| `PillBg` | `rgba(34,36,40,.965)` | `rgba(244,244,244,.97)` | 任务栏贴片底 |
| `PillBorder` | `rgba(255,255,255,.15)` | `rgba(0,0,0,.10)` | 任务栏贴片描边 |
| `PanelBg` | `rgba(24,27,32,.95)` | `rgba(247,248,250,.96)` | 详情面板底（LayeredWindow 用） |
| `MenuBg` | `rgba(30,34,41,.98)` | `rgba(251,252,254,.98)` | 右键菜单底 |
| `MenuBorder` | `rgba(255,255,255,.13)` | `rgba(0,0,0,.10)` | 右键菜单描边 |

指标色（只许用在「该指标自己的数据」上：圆点、进度条、曲线、表格数值着色）：

| 指标 | 深色 | 浅色 |
|---|---|---|
| CPU | `#4CC2FF` | `#0A84FF` |
| 内存 | `#B39DFF` | `#7C5CE0` |
| GPU | `#3DDC97` | `#11A36B` |
| 显存 | `#FFB340` | `#D18400` |
| 网速 | `#6EE7D7` | `#0E9C94` |
| 磁盘 | `#FF8FB1` | `#C2255C` |

### A2.2 使用规则

- 正文用 `Text`，说明/辅助用 `Label`；禁止用指标色写普通文字。
- 危险动作（结束任务）用 `Alert` 做按钮底 + 白字；错误回显文字用 `Alert`。
- 选中 = 内容变 `Text` + 结构强调（`SelectedBg` 块 / `Accent` 淡底 / `Accent` 描边），三者按控件类型取一，见 A8。
- 叠加层顺序约定：hover 叠加永远画在内容之下、底色之上。
- 对比度基线：`Text`/底 ≥ 7:1，`Label`/底 ≥ 4.5:1（两套主题现状均满足，改色后需复测）。

## A3. 字体规范

**规则（用户定，硬约束）：最小 12px；向上步进一律偶数**（12 / 14 / 16 / … / 30）。px 单位（`Gfx.F`，`GraphicsUnit.Pixel`），禁止 pt。

| Token | 规格 | 用途 |
|---|---|---|
| Display | 30 Bold | 指标卡大数值 |
| Title | 14 Bold | 窗口标题栏标题、对话框标题、详情条/详情面板进程名、表格行名、卡片标题 |
| Value | 16 Bold | 详情面板行数值（14 与 30 之间的数值强调档） |
| Body | 12 Regular | **默认 UI 文字**：按钮、分段、Chip、表格数值、详情行、状态栏、路径、对话框正文、空态、卡片注释 |
| Body Bold | 12 Bold | 表头、主按钮字、Stepper 值 |

正文与次要文字同为 12px，层级靠颜色分（`Text` / `Label`），不靠字号。
**唯一豁免**：托盘图标（16px 位图内的数字/笔画 9~13px），物理上不可能 ≥12（见 B8）。
旧值映射（整改期参考）：≤12 → 12；12.5~13.5 → 14；15 → 16；30 不变。
`SearchBox` 输入文字 10pt(≈13.3px) → **12px**；`BentoCard` 标题 10.5pt → 14px（渲染尺寸恰相等，仅单位规范化）。

## A4. 间距规范

尺度 `4 / 8 / 12 / 16 / 20 / 24`（96 DPI 基准，全部随 k 缩放）：

| 场景 | 值 |
|---|---|
| 页面四周内边距（主面板/表格/详情条与窗口边） | 16 |
| 区块之间（卡↔卡、卡↔表格、表格↔详情条） | 12 |
| 同组控件之间（按钮组、Chip 组、段控与相邻控件） | 8 |
| 卡片内边距 | 16（Bento 卡 18 保留，见 B5） |
| 对话框内边距 | 20 |
| 设置窗口网格 Pad / Gap | 24 / 12（Gap 14→12，ColW 公式自适应） |
| 控件内部（图标↔文字） | 6（紧凑）/ 10（宽松） |

现状 10→12、14→16 收敛；其余 4/8/20/24 不动。

## A5. 圆角规范

| Token | 值 | 适用 |
|---|---|---|
| R4 | 4 | ConfirmCheck 复选方块（15px 小元素） |
| R6 | 6 | 分段控件内选中/悬浮块、Stepper 按钮区 |
| R8 | 8 | 输入类容器：Segmented、Stepper；任务栏贴片（7→8） |
| R10 | 10 | 按钮、右键菜单 |
| R12 | 12 | **所有卡片级容器**：指标卡(14→12)、表格、详情条、对话框、详情面板、Bento 卡(14→12)、TrayPreview |
| Pill | 高度÷2 | Chip、搜索框、胶囊开关、滚动条 thumb、进度条(6 高→3) |

规则：同一屏上的同级容器圆角必须相同；按钮(R10)永远比它所在的卡片(R12)小一档。

## A6. 控件高度规范

交互控件（按钮/段控/Chip/输入/步进）**统一视觉高 32**：

| 控件 | 现状 | 目标 |
|---|---|---|
| BentoButton（默认/详情条/顶栏） | 44 / 32 / 32 | 32 |
| Segmented | 默认 34、顶栏 32、工具行 34 | 32 |
| Chip | 默认 30、顶栏摆放 32 | 32（默认值即 32） |
| SearchBox / Stepper | 34 / 30 | 32 |
| 对话框按钮 | 40 | 32 |
| 设置窗口按钮/段控/胶囊/步进 | 34 / 34 / 30 / 30 | 32 |

非交互例外：表格行高 48、表头 38（列表节奏，不在控件体系内）；标题栏 44（B1）；详情面板行高 52。
阴影预留规则不变：`BentoButton.Height = 视觉高 + Shadow(6)`，投影不出控件边界。

## A7. 按钮规范

四类。全部：无描边、圆角 R10、高 32。**宽度规则**：
① **同字数同宽** —— 不同类型按钮相同字数时宽度一致；内容宽按**主按钮 Bold 字宽**为基准计算（Bold 不产生额外宽度），文字宽 + 32（水平内边距 16×2）。
② **组内等宽** —— 并排在一起的按钮组（详情条动作组、对话框按钮行、顶栏按钮）全部以**组内最宽者**为准：

| 类别 | 底色（默认） | 文字 | 用途 |
|---|---|---|---|
| Primary 主按钮 | `Accent` | 白 / **Bold** | 每屏至多一个的主动作：确认执行、确定 |
| Danger 危险按钮 | `Alert`（`PrimaryColor` 覆写实现） | 白 / **Bold** | 结束任务（按钮与对话框标题同色呼应） |
| Secondary 次级按钮 | `SecondaryBg` | `Text` / Regular | 打开位置、优先级、挂起、取消、设置 |
| Ghost 幽灵按钮 | 透明 | `Label` / Regular，hover 变 `Text` | 低强调操作（标题栏窗口钮、表格列头、状态栏操作）。**暂无 BentoButton 实例，实现时 `Ghost=true` 分支** |

状态表（`BentoButton.OnPaint` 目标行为）：

| 状态 | 底色 | 文字 | 投影 | 其他 |
|---|---|---|---|---|
| Default | 见上表 | 见上表 | 无（平贴） | 光标 Hand |
| Hover | Primary/Danger：混白 20%；Secondary：混白 14%（浅色混黑 7%） | 不变 | 10 层渐隐下沉（A10） | |
| Pressed | Primary/Danger：混黑 24%；Secondary：混黑 30%（浅 14%） | 不变 | 收到 2 层、peak 14 | 文字下沉 1.5px |
| **Disabled** | `DisabledBg` | `Label` @ alpha 190 | 无 | 现状缺失（C1-P1）：WinForms UserPaint 控件禁用时不自动置灰，必须在 OnPaint 里判 `Enabled` |
| Focus（键盘） | 不变 | 不变 | 不变 | 内缩 2.5px 处 1px `TextColor()@47%` 焦点环（现状已有，仅 BentoButton） |

投影颜色：深色主题 Primary/Danger 用 `Accent色 混 68% 黑`（蓝晕/红晕），其余一律中性黑；浅色主题全部中性黑。

## A8. 选择控件规范（单选 / 开关 / 勾选）

**使用场景规则**（先选对控件再谈样式）：

- 互斥单选（视图、刷新间隔、阈值、优先级）→ **Segmented**
- 开关型过滤（显示后台、系统进程）→ **Chip**
- 强确认勾选（对话框内）→ **ConfirmCheck**

### Segmented（单选段控）

| 项 | 规格 |
|---|---|
| 容器 | 高 32，圆角 R8，底 `SecondaryBg`，1px `InputBorder` |
| 内块 | 左右各缩 3、上下各缩 3，圆角 R6 |
| 选中块 | 底 `SelectedBg`，文字 `Text` |
| Hover 块（未选中项） | 底 `Hover`，文字不变 `Label` |
| 未选中文字 | `Label`，字号 Body 12 |
| Disabled | 整体：容器 `DisabledBg`、全部文字 `Label@190`、无 hover |

### Chip（开关胶囊）

| 项 | 规格 |
|---|---|
| 形状 | 高 32，圆角 Pill，宽 = 文字 + 30 |
| 未选中 | 底 `SecondaryBg`，1px `InputBorder`，文字 `Label` |
| 选中 | 底 `Accent@18%`（深）/`Accent@15%`（浅），1px `Accent@59%`（深）/`@47%`（浅），文字 `Text` |
| Hover（**现状缺失，C1-P1**） | 未选：底提亮一档（`Hover` 叠加）；选中：底 `Accent@24%` |
| Disabled | 同 Segmented 整体禁用规则 |

### ConfirmCheck（复选行）

方块 15px 圆角 R4；未选底 `SecondaryBg` + `InputBorder`；选中底 `Accent` + 白勾（1.8px 圆头）；文字 Body 12 `Text`，整行可点。

## A9. 输入控件

| 控件 | Default | Hover | Focus | 备注 |
|---|---|---|---|---|
| SearchBox | 胶囊 Pill，底 `CardBg`，1px `CardBorder`，放大镜 1.4px `Label` | 描边提亮一档（`InputBorder`） | 描边 `Accent@55%`，占位符消失 | 内嵌无边框 TextBox，占位走 EM_SETCUEBANNER（别改回父控件绘制） |
| Stepper | 容器 R8 底 `SecondaryBg` + `InputBorder`，± 区各 26 宽 | ± 区圆角 R6 底 `Hover` | 无 | 值 Body Bold 12；按住 380ms 后 55ms 连发 |
| Slider | 轨道 4px `Track` 圆头，已填充 `Accent`，滑块 14px 白 + 1px 黑@27% 边 | — | — | 拖拽即时生效 |

## A10. 阴影规范（使用规则）

1. **常驻元素（卡片/面板/表格/贴片/对话框主体）永远无阴影**，靠三层底色 + 描边分层。
2. 允许阴影的三种情况：
   - 按钮 **Hover 抬起**：10 层由外向内渐隐（外圈最淡、内圈最深，反向会留硬边），peak alpha 深 30 / 浅 24，下沉 reach = `Shadow − 1.5`，inset `0.4 + 2.2k`；
   - 按钮 **Pressed 收回**：2 层、peak 14；
   - **右键菜单**：14 层常驻，`Radius + i×0.7` 逐层外扩。
3. 控件高度必须预留投影空间：`Height = 视觉高 + 6`（菜单 `W=252` 侧另计 `Shadow=14`）。
4. 阴影色规则见 A7；除菜单外投影只向下（offset y，不横向）。

## A11. 图标与图形语言

| 图形 | 规格 |
|---|---|
| 指标圆点（卡标签/列表） | 直径 6，实色指标色 |
| 搜索放大镜 | 圆 ∅7 + 柄 4.2，线 1.4px `Label` |
| 排序三角 | 自绘多边形边长 4（禁用 ▼ 字形，字体回退会发虚），`Label` |
| 复选勾 | 1.8px 白，圆头 |
| 表格回退图标 | 28px 圆角 R7 色块（8 色板按名称哈希）+ 首字母 14 Bold 白 |
| 系统进程 dim | 名称/图标压暗：图标叠 `CardBg@59%`，文字用 `Label` |
| 滚动条 | 宽 10，thumb 圆头 Pill，白/黑@27% |

## A12. 动效与交互时序

- 无过渡动画；一切状态 = 下帧重绘（1s 采样节奏下加过渡反而卡）。
- 详情面板开合 350 ms 去抖；从托盘打开给 900 ms 失焦保护。
- Stepper 连发：380 ms 首延迟 → 55 ms/步。
- 列表每帧重建时按 Key 恢复选中，滚动位置 Clamp 不跳。

---

# Part B · 界面规格

## B1. 主面板（MainDashboardForm）

**窗口**：默认 1280×800，最小 1040×660，位置记忆；标题「WMonitor 资源面板」。

**标题栏（目标态，自绘无边框——用户已拍板，现状为系统栏+面板内重复标题）**：

| 项 | 规格 |
|---|---|
| 形制 | `FormBorderStyle.None` + DWM `CORNER_PREFERENCE=ROUNDED`（Win11 系统圆角，顶部）；Aero Snap/贴边分屏由 WM_NCHITTEST 返回 HTCAPTION/HT* 自动获得 |
| 高度 | 44，底色 `WindowBg`，无描边、无分隔线（内容区卡片自带边界） |
| 左侧 | 应用图标 **24**（B1 初版 16，v0.28 按用户拍板与任务栏按钮同步放大 ×1.5）+ 标题 Title 14 Bold `Text`，左距 16 |
| 右侧窗口钮 | 最小化 / 最大化 / 关闭，各 **46×32** 垂直居中；图标 10px 线 1px `Text` |
| 窗口钮 Hover | 底 `Hover`；**关闭钮 `#C42B1C`** + 白图标（Win11 惯例，深浅同值）；Pressed 加深一档 |
| 最大化态 | 顶部圆角自动消失（DWM）；Snap Layouts：最大化钮 hover 须返回 `HTMAXBUTTON` |
| 主题 | 图标色跟随 `Text`（无需 DWM 深色标题栏属性——已无边框） |

**布局**（pad 16 / gap 12，现状 14/10 收敛）：

```
┌ 44 ─ 标题栏：◈ WMonitor 资源面板        ▁ ▢ ✕ ┐
│ 16 ─ 控制行 44：刷新段控│显示后台│系统进程│设置 ─ 12
│ 12 ─ 指标卡 ×4，高 106，圆角 R12             ─ 12
│ 12 ─ 工具行 40：视图段控│搜索│GPU 筛选│阈值   ─ 12
│ 12 ─ 表格（弹性高，头 38 / 行 48）            ─ 12
│ 12 ─ 详情条 94                                ─ 12
│ 状态栏 24（Body 12，Label）                  ─ 16
└──────────────────────────────────────────────┘
```

- 控制行控件高 32 垂直居中；右对齐顺序：刷新段控(280) → 显示后台 → 系统进程 → 设置按钮(96)。
- 工具行：视图段控(300) 左对齐；搜索框弹性（最大 430）；GPU 筛选(240，单卡隐藏)、阈值段控(230) 右对齐；行内控件高 32。
- 指标卡：标签 12 Regular `Label` + 圆点 6；数值 Display 30 Bold `Text`（y 34）；进度条高 6 Pill（槽 `Track`，填充指标色）；副行 Body 12 `Label`；内边距 16。
- 状态栏左：进程/应用/采样/GPU/最近刷新；右：「排序：点击列头切换升降序」。

## B2. 表格（DashboardTable）

| 项 | 值 |
|---|---|
| 容器 | 圆角 R12，底 `CardBg`，1px `CardBorder`，内裁剪 1px |
| 表头 | 高 38，底 `HeadBg`，下沿 1px `Sep`；文字 Body Bold 12；排序列 = **紧贴列头内容的对称圆角块**（内容盒 = 文字 + 排序三角，左右 pad 7s、R6、块高 32，底 `SelectedBg` + 文字 `Text`；**A8 互斥单选语言**；v0.28 复检定稿：整列块左空当远大于右，用户拍板「减少左边宽度和右边一样」，列宽/文字/数据位置不动）；未选 `Label` |
| 排序三角 | 文字右侧 6px（名称列）/ 右缩 14（数值列） |
| 列 | 名称(弹性 36%) / PID 88 / CPU 92 / 内存 106 / GPU 88 / 显存 106；数值列右对齐右缩 16 |
| 行 | 高 48，图标 28；名称 Title 14 Bold `Text`，路径 Body 12 `Label`，值 12 |
| 数值着色 | ≥1% 指标色 + Bold；0.05~1% `Text`；<0.05% 或 — `Label` |
| 表头 Hover | 排序列复用选中块；未选叠同规格贴内容圆角块 `Hover`，文字 `Label→Text` |
| Hover 行 | 圆角 R8 `Hover` 叠加 |
| 选中行 | 圆角 R8 `Accent@24%`（深）/`@10%`（浅） |
| 滚动条 | 见 A11；滚轮 3 行/格，thumb 可拖、轨道翻页 |
| 空态 | 主行与副提示均 Body 12，`Label` 居中 |

## B3. 详情条（DetailBar）

圆角 R12、底 `CardBg`、1px `CardBorder`、高 94；左图标 36 + 三行文字（名称 Title 14 Bold；元信息 / 路径与指标均 Body 12 `Label`）。
右端按钮组：打开位置 / 优先级 / 挂起 / 结束任务，**组内等宽**（= 组内最宽者，约 86；原 78/72/72/86 混宽废弃），高 32，间距 8，右缩 16；无选中行时提示「选中列表中的一行查看详情」+ 按钮全 Disabled（A7 禁用态）。
挂起/恢复互斥：按钮永远显示当前可执行的动作名。

## B4. 对话框（ActionConfirmDialog / MessageDialog）

无边框自绘、`CenterParent`、圆角 R12、底 `CardBg`、1px `CardBorder`、外圈 `WindowBg`；宽 470（消息 440），pad 20。

| 项 | 值 |
|---|---|
| 标题 | Title 14 Bold；终止类标题与确认钮同为 `Alert`，其余 `Text` |
| 目标行 | Body 12 `Text`；路径/影响范围 Body 12 `Label` |
| 关键提示 | 外壳组件警告 / 受限说明 / 错误回显 = Body 12 `Alert` / `Label`；错误不关窗、可重试 |
| 优先级段控 | Segmented 5 档，高 32，整宽 |
| 复选 | ConfirmCheck；确认钮勾选前 Disabled（「不要只用 OK」） |
| 按钮行 | 取消 Secondary / 确认 Primary(Danger)，**组内等宽**（= 最宽者，约 120），高 32，间距 8，右下 |
| 键盘 | Esc=取消；Enter=确认（Enabled 时）；取消钮为 CancelButton |

## B5. 设置窗口（SettingsForm，Bento 网格）

| 项 | 值 |
|---|---|
| 窗口 | 1238×925；4 列×3 行网格；`Pad=24`、`Gap=12`（14→12）、`ColW` 按窗口宽公式算 |
| 标题栏 | **随 B1 改自绘**（现 DWM 深色系统栏）；顶栏 64 / 底栏 56 不进网格 |
| 卡片 | 圆角 R12（14→12），内边距左右 18 / 标题区 50 / 底 18；底 `CardBg`，1px `CardBorder` |
| 卡标题 | Title 14 Bold（10.5pt 仅单位规范化）；注释 Body 12 `Label` |
| 控件 | 一律按 A6 收敛为高 32；卡片内告警卡按钮 30→32 |
| 布局模型 | 卡片 `List<Row>` + `LayoutCards()` 三步摆卡，行间盈余按 28px 上限摊掉；卡片高度不得写死 |
| 托盘卡 | 固定 374 高；`AutoScroll` 平时关 |

## B6. 任务栏贴片（TaskbarStrip）

高 30、圆角 **8**（7→8）、水平内边距 10、1px `PillBorder`、距托盘左 6。
数字条（字号按 A3 最小 12 规则放大）：圆点 5（指标色）→6→标签 12 Regular 指标色@65%→5→数值 14 Bold `Text`（右对齐固定盒宽，防跳宽）→3→单位 12 Regular `Label`；项间竖线 1×12 `Sep` 左右各 8。**注意**：字号放大后贴片总宽约 +30%，发布时目检与托盘的间距（整改清单 C8）。
迷你柱：5 根 3 宽间 2，最高 14 底对齐；四环：∅26 环宽 3 圆头，起 −90°，底环 `Track`。告警命中整格变 `Alert`。

## B7. 详情面板（DetailPanel）

344×314（磁盘 IO 366），圆角 R12，pad 16；标题 Title 14 Bold；时间窗段 26×16 圆角 5 间距 4；行高 52，图标底 26 圆角 7 指标色@10%；行名 14 Bold、副标题 Body 12；进度条高 3 Pill；数值 Value 16 Bold；曲线 92×24 线 1.5@65% 填充 13%；底栏 Body 12；齿轮/关闭 18 圆。历史窗口诚实原则：没数据源的分段不画。

## B8. 托盘图标（TrayIconRenderer）

基准 16px（实际 `SmallIconSize`）；4× 超采样 + `HighQualityBicubic` + `BoostAlpha(1.35)`；样式与槽位规格见旧版（双数字/双环/四段条/单指标，字号比例 0.58/0.80 等）不变；每秒最多重建一次（Key 变才画）。**A3 的 12px 下限对托盘图标豁免**：位图内文字按比例绘制（约 9~13px），放大反而糊。

## B9. 右键菜单（FlyoutMenu）

宽 252、行高 34、圆角 R10、内边距纵向 6、阴影 14 层（A10）；底 `MenuBg`、1px `MenuBorder`；
行 hover 底 `Hover`，危险行 hover `Alert@15%` + 文字 `Alert`；子菜单右箭头，勾选槽位左置；`WS_EX_NOACTIVATE` 不抢焦点，外部点击/Esc 关闭。

---

# Part C · 现状整改清单

> 目标值全部出自 Part A/B。执行顺序建议按优先级分批；每批改完跑 `--dashboard` 截图对比。
>
> **执行状态（2026-11-05）**：**P1（C1~C8）✅ 已随 v0.24 发布；P2（C9~C17）✅ 已随 v0.25 发布；P3（C18~C22）✅ 已随 v0.26 发布——Part C 全部收官**（实施细节与偏差记录见 docs/14 施工日志；P1 见 2026-11-05 (3)，P2 见 (5)，P3 见 (6)；其中多卡筛选 240→264 为 A3 硬约束优先的已批准偏差；C19 的「标题栏窗口钮即首个 Ghost 用户」改为入档偏差：窗口钮按 B1 保留专属绘制，Ghost 分支待表格列头/状态栏操作控件化时接入）。

## P1 · 视觉一致性（低风险高收益）

| # | 现状 | 目标 | 位置 |
|---|---|---|---|
| C1 | 标题栏三种做法并存；主面板标题画两遍 | 按 B1 全自绘无边框标题栏（主面板+设置窗），删 OnPaint 重复大标题 | `MainDashboardForm` / `SettingsForm` |
| C2 | `Segmented`/`Stepper`/`Chip`/`ConfirmCheck` 内联色 3 套（rgb 32,36,42 / 34,38,44 / Soft） | 全部引用 token：容器/未选底 `SecondaryBg`、描边 `InputBorder`、选中块 `SelectedBg` | `Controls.cs` / `ActionConfirmDialog.cs` |
| C3 | 按钮无 Disabled 态（未选中行时「优先级/挂起」与可用按钮同貌） | A7 Disabled：`DisabledBg` + `Label@190`，OnPaint 判 `Enabled` | `BentoButton` |
| C4 | Chip 无 hover 反馈；Segmented hover 用内联 alpha | A8 hover 规则，统一 `Hover` token | `Chip` / `Segmented` |
| C5 | 按钮字 9.5px（过小，中文难读） | Body 12（主 Bold/次 Regular）；宽度按 A7 规则：同字数同宽（Bold 基准）、组内等宽 | `BentoButton` |
| C6 | 卡片圆角 14/12 混用 | 统一 R12：MetricCard、BentoCard 14→12；贴片 7→8 | `DashboardWidgets` / `Bento` / `TaskbarStrip` |
| C7 | 未选描边 alpha 混用（38/90） | `InputBorder` 统一 .15（深）/ .10（浅） | 同 C2 |
| C8 | 贴片字号 8.5 / 12 / 9 | 12 / 14 / 12（A3 最小 12 规则）；总宽 +约 30%，需目检托盘间距 | `TaskbarStrip` |

## P2 · 单位与度量（逐处小改，注意联动）

| # | 现状 | 目标 | 备注 |
|---|---|---|---|
| C9 | 全站档外字号（9.5/10/10.5/11/11.5/12.5/15） | 按 A3 映射：≤12→12，12.5~13.5→14，15→16 | 全文件搜 `Gfx.F` 逐处核对 |
| C10 | `SearchBox` 输入字 10pt；`BentoCard` 标题/注释 pt 单位；`MainDashboardForm.Font` 9pt | 全部 px（A3）；SearchBox 13.3→12px 是真变值 | `Gfx.F` 强制 |
| C11 | 控件高 30/32/34/40/44 五种 | 统一 32（A6 表） | Chip 默认宽随字号新算 |
| C12 | 主面板 pad 14 / gap 10 | 16 / 12 | 只改 `DoLayout` 两个变量 |
| C13 | 设置网格 Gap 14 | 12 | `ColW` 公式自适应，验证 1238 宽下不换行 |
| C14 | 表头底 rgb(30,33,39) 内联 | `HeadBg` token（新增） | `DashboardTable.DrawHeader` |
| C15 | 分段选中块 rgb(62,68,78) 内联 | `SelectedBg` token（新增） | `Segmented` |
| C16 | `Soft`/`SoftLight` 双 token（浅色仅 SoftLight 有实际用户） | 合并 `SecondaryBg`：深 `#262A31` / 浅 `#D8DEE6` | Theme.cs + 引用处 |
| C17 | 对话框按钮 40 高 | 32 | 取消/确认组内等宽（≈120）；详情条四钮组内等宽（≈86） |

## P3 · 可选增强

| # | 项 |
|---|---|
| C18 | Segmented/Chip 支持 Tab 焦点 + A7 焦点环（现仅 BentoButton 有） |
| C19 | Ghost 按钮分支落地（`BentoButton.Ghost`），标题栏窗口钮即首个用户 |
| C20 | 无边框窗口右键标题栏弹系统菜单（SC_MINIMIZE 等，P3 可不做） |
| C21 | 验证 borderless 后 Win11 系统圆角与自绘 R12 不双画（对话框/主面板各截一角） |
| C22 | `MessageDialog` 正文行距 17 → 按 Body 12 行高重算 |

## Theme.cs 需新增字段

`SelectedBg`、`HeadBg`、`InputBorder`、`DisabledBg`（深浅两套值见 A2.1）；`Soft`/`SoftLight` 合并为 `SecondaryBg`。
