# 15 · 应用图标提案（三方案 × 明暗主题）

> 2026-11-05 立项。背景：现用 `app.ico` 与 UI 规范脱节，需要一套跟随设计语言的应用图标。
> 用户已拍板的约束：**无字母抽象图形**、**统一现有 UI 语言**（#0A84FF + 圆角方块 + 克制渐变）、
> 每方案出**深浅两版**（跟随应用主题切换，exe 主图标 Windows 机制上固定一套），
> **先提案、选定后落 .ico**。

## 1. 设计基调（全部方案共享）

- 512 设计网格，容器为圆角方块 `x=52 y=52 w=408 rx=92`（≈22.5%，macOS squircle 比例），图形安全区约 60%。
- 容器：纵向线性渐变 + 1px 内描边 + 顶部克制的 gloss 高光 + 柔和投影（深色版投影不透明度更高）。
- 图形主色走 Accent 蓝，辅助点用 GPU 绿（「健康/存活」语义）；深浅两版颜色直接映射 `Theme.cs` token：

| 元素 | 深色版 | 浅色版 | 来源 token |
|---|---|---|---|
| 容器渐变 | `#242931 → #13161B` | `#FFFFFF → #EFF2F6` | CardBg 系 |
| 容器描边 | `rgba(255,255,255,.08)` | `rgba(0,0,0,.06)` | CardBorder |
| 主图形渐变 | `#4CC2FF → #0A84FF` | `#3FA1FF → #0A84FF` | CPU 深色 / Accent |
| 采样轨道 / 环 | `rgba(255,255,255,.13)` | `rgba(0,0,0,.10)` | Track |
| live 点 / 目标点 | `#3DDC97` | `#11A36B` | GPU 深 / 浅 |
| 次级点 | `#4CC2FF` | `#3FA1FF` | CPU 深色系 |

## 2. 三方案

| 方案 | 隐喻 | 图形 | 语义 |
|---|---|---|---|
| **A · 脉搏** | 实时采样活性 | 灰色采样轨 + ECG 波形 + 绿色 live 点 | 看护的是「活着的进程」 |
| **B · 仪表** | 资源水位与阈值 | 270° 轨道 + 68% 进度弧 + 指针 + 中心点 | 阈值触发重启的核心机制 |
| **C · 雷达** | 看护巡视 | 双环 + 渐隐扫描扇面 + 双色目标点 + 中心点 | watchdog 的「巡视/发现」 |

小尺寸表现（等比缩略目测）：A 与 B 在 16px 仍可辨认；C 依赖扇面渐变，32px 以下需按规范简化（落 ico 时单独处理）。

## 3. 文件与流水线

```
design/icons/
├── proposals/
│   ├── A-pulse.dark.svg / A-pulse.light.svg      # 源文件（唯一事实来源，512 网格）
│   ├── B-gauge.dark.svg / B-gauge.light.svg
│   ├── C-radar.dark.svg / C-radar.light.svg
│   └── previews/                                  # 交付预览（2x PNG）
│       ├── proposals-overview.png                 # 全页总览（标题+总览条+三方案）
│       ├── proposals-A-pulse.png / -B-gauge.png / -C-radar.png
│       ├── proposals-strip.png                    # 六版一排总览条
│       └── sheets/*.svg                           # 纯 SVG 版面（光栅化源）
└── tools/
    ├── build-fragment.mjs                         # 生成内联 SVG 会话预览页（无 <use>）
    └── build-previews.mjs                         # 从源 SVG 生成 sheets/*.svg
```

改图标 = 只改 6 个源 SVG → 跑两个脚本重建预览与展示页。

## 4. 已知渲染坑（会话工具链）

- 内嵌页里 `<symbol>/<use>` + 渐变在部分渲染路径下丢失半透明层（56px 可见、120px 消失）——
  展示页一律**全量内联 SVG**（`build-fragment.mjs` 产出，0 个 use 引用）。
- 浏览器原生截图合成层不可信（fullPage 平铺重复、clip 丢半透明渐变层）——
  PNG 一律走 `img → canvas → toDataURL` 光栅化 `sheets/*.svg`（canvas 像素经采样比对为忠实渲染）。

## 5. 待决策

1. ~~用户三选一（或组合微调，如「C 的扇面 + A 的 live 点」）。~~ **已决策：方案 B · 仪表（2026-11-05 用户拍板）。**
2. ~~选定后落 `.ico`。~~ **已落地（v0.24，2026-11-05）**：`app.ico` 已替换（exe 主图标固定**深色版**——任务栏深色为默认场景；浅色版存 `design/icons/app/app-light.ico` 可一键换装）。尺寸链 256/128/64/48（faithful）+ 32/24（small 变体：去光泽/内描边、笔画加粗 36/26/r27）+ 16（tiny 变体：去暗轨去指针，进度弧 48 + 中心点 r24——直缩 16px 会糊）。生产 SVG（feDropShadow → SVG1.1 滤镜链，RSVG 2.40 兼容）与 256px PNG 归档 `design/icons/app/`；组装用 ImageMagick（256 帧 PNG、其余 BMP）；旧图标备份 `backups/app-v0.23-old.ico`。托盘 16px 自绘（B8）未同步换新。
   **v0.28 满幅重建（用户实测「icon 比一般 icon 小」）**：v0.24 版画布 512 里主体只占 52..460（79.7%），
   四周 52px 透明留白 + faithful 投影滤镜，导致任务栏/托盘/标题栏整体比满幅邻居小一圈。
   重建：viewBox 收紧为 `50 50 412 412`（≈99% 满幅）、去投影；渲染弃用 magick/Edge 光栅化
   （48px 截断 / 小窗口截图损坏），改 GDI+ 按 SVG 几何直绘（`design/icons/tools/render-gdi.ps1`），
   组装用同目录 `build-ico.ps1`（256 PNG + 其余 BMP、真实 AND mask——magick 的 mask 行距按 WORD
   对齐会让 GDI 花屏）。shell 解码器逐帧验收 16~128 全部 100% 满幅。旧图标备份
   `backups/app-v0.27-old.ico`；`*.tight.svg` 为当前生产源（原 512 画布版仅存档）。
   **v0.28 复检再放大（用户仍觉小）→ 方块修复后回调（定稿）**：任务栏亮度图逐像素测量发现 ——
   深色容器方块在深色任务栏上**完全隐形**，「可见图标」只是蓝色表盘。方块渲染修好（窗口
   ICON_SMALL 24px + 图标缓存重建,见 changelog）之前,表盘怎么调都不可见;修好后 94% 又太满。
   用户以三档对比图（r104/128/168）拍板定稿 **r128（外缘 ~70%）**：环 128、笔画 37（small 44/
   tiny 59）、圆点 30（small 33）、指针 25。**经验：评估任务栏图标观感必须比「可见墨迹宽度」
   且先确认方块渲染尺寸；表盘占比是独立旋钮（render-gdi.ps1 顶部几何常量）。**
3. 托盘 16px 自绘图标（B8 豁免规范）与标题栏图标是否同步换新视觉，属 Part C 整改外事项，单独排期。**标题栏 16px 图标已随 C1 自绘标题栏上线（取 Form.Icon，即本 .ico 的 32px 帧），深浅主题共用深色版。**
