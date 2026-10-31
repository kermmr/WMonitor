# AGENTS.md — 接手 WMonitor 的第一份说明

给任何接手的 Agent 或开发者。读完这一页就能改代码，不用先读全部源码。

---

## 1. 这是什么

Win11 任务栏系统监视器（CPU / 内存 / GPU / 显存 / 网速 / GPU 温度）。
C# .NET 8 + WinForms，**零第三方依赖**，全部 P/Invoke。
贴片是吸附在托盘左侧的分层窗口 —— Win11 已移除 Deskband，不存在真正"嵌进任务栏"的合法方式，不要再往那个方向试。

---

## 2. 环境（本机特殊，务必先设）

系统**没有**安装 .NET SDK，SDK 装在非标准路径：

```bash
export DOTNET_ROOT='D:\Hermes\tools\dotnet-sdk'
export PATH="/d/Hermes/tools/dotnet-sdk:$PATH"
```

`dotnet --list-sdks` 应显示 `8.0.427 [D:\Hermes\tools\dotnet-sdk\sdk]`。
系统自带的 `C:\Program Files\dotnet` 只有**运行时**，没有 SDK，不能编译。

Shell 是 git-bash（MSYS）。路径参数给原生程序时用 `D:/...` 正斜杠形式，不要用 `/d/...`。

---

## 3. 目录约定

```
D:\WMonitor\
├─ wmonitor-eidt\              ← 源码（你在这里改）
│  ├─ WMonitor.csproj
│  ├─ Program.cs               入口、单实例、--probe 诊断
│  ├─ MonitorAppContext.cs     托盘 + 定时器 + 菜单 + 生命周期
│  ├─ AppConfig.cs             JSON 配置（%APPDATA%\WMonitor\config.json）
│  ├─ Startup.cs               HKCU Run 开机自启
│  ├─ Dbg.cs                   诊断日志（默认关闭，SYSMON_DEBUG=1 开启）
│  ├─ app.ico                  应用图标（PIL 生成的多尺寸 ico）
│  ├─ run.sh                   开发迭代脚本：杀进程→编译→启动→截任务栏
│  ├─ Interop\Native.cs        Win32 / PDH / GDI / 多屏 / DPI P/Invoke 声明
│  ├─ Metrics\
│  │  ├─ MetricSampler.cs      所有指标采样（CPU/内存/GPU/显存/网速/磁盘）
│  │  ├─ NvmlGpu.cs            NVML 动态加载（无 N 卡自动降级）
│  │  ├─ HistoryStore.cs       历史：内存环 1 小时 + 磁盘日文件 + 抽稀查询
│  │  └─ AlertEngine.cs        阈值告警状态机（5 条规则 + 迟滞 + 冷却）
│  ├─ UI\
│  │  ├─ LayeredWindow.cs      分层窗口基类 + 圆角/字体缓存 + 点击穿透
│  │  ├─ TaskbarStrip.cs       任务栏贴片（3 种风格 + 吸附定位 + 拖动 + 告警变红）
│  │  ├─ TaskbarLocator.cs     枚举全部任务栏 + 每屏 DPI
│  │  ├─ DetailPanel.cs        详情面板（4/5 行 + 可切时间窗曲线）
│  │  ├─ TrayIconRenderer.cs   4 种托盘图标绘制（+ 告警红环）
│  │  ├─ SettingsForm.cs       设置窗口（滚动容器 + 块式布局）
│  │  ├─ Controls.cs           Segmented / Chip / Slider / Stepper
│  │  └─ Theme.cs              深/浅两套配色 + 系统主题判定 + 告警色
│  └─ docs\                    详细文档
├─ WMonitor\                   ← 安装版（framework 依赖，多文件）
├─ WMonitor-v0.10-framework.exe
└─ WMonitor-v0.10-portable.exe
```

**改代码只改 `wmonitor-eidt\`**，然后重新发布到另外两个位置。

---

## 4. 构建与发布（复制即用）

```bash
export DOTNET_ROOT='D:\Hermes\tools\dotnet-sdk'
export PATH="/d/Hermes/tools/dotnet-sdk:$PATH"
cd /d/WMonitor/wmonitor-eidt

# 编译
dotnet build -c Release

# ① 安装版（多文件，framework 依赖）→ D:\WMonitor\WMonitor
dotnet publish -c Release -r win-x64 --self-contained false \
  -p:PublishSingleFile=false -o "D:/WMonitor/WMonitor"

# ② 单文件 · 框架依赖
dotnet publish -c Release -r win-x64 --self-contained false \
  -p:PublishSingleFile=true -o build/framework
cp build/framework/WMonitor.exe "D:/WMonitor/WMonitor-v0.10-framework.exe"

# ③ 单文件 · 自包含
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -o build/portable
cp build/portable/WMonitor.exe "D:/WMonitor/WMonitor-v0.10-portable.exe"
```

**发布前必须先杀掉运行中的进程**，否则 exe 被锁、编译报 `MSB3027 / MSB3021`：

```bash
powershell -NoProfile -Command "Stop-Process -Name WMonitor -Force -ErrorAction SilentlyContinue"
sleep 1
```

版本号规则：`v0.1N`，`WMonitor.csproj` 里同时改 `<Version>`、`<AssemblyVersion>`、`<FileVersion>`；成品文件名同步。

---

## 5. 改完必须验证的三件事

1. **能编译**：`dotnet build -c Release` 0 error 0 warning。
2. **贴片真的画出来了**：启动后截图任务栏右下角，用视觉确认（不要只看进程活着）。
3. **资源没退化**：`Get-Process WMonitor | Select PrivateMemorySize64, TotalProcessorTime`。
   基线：私有内存 ≈ 43 MB，CPU 稳态 ≈ 0.3%。

截图（不要用鼠标模拟，会干扰用户）：

```bash
python -c "
from PIL import ImageGrab
im = ImageGrab.grab(all_screens=True)
im.crop((1820,1380,2320,1440)).save(r'D:\Hermes\output\results\check.png')
"
```

---

## 6. 关键决策（别推翻，除非有更好理由）

| 决策 | 原因 |
|---|---|
| WinForms 而不是 WPF | 1 Hz 数字刷新，GDI+ 单次重绘 < 1 ms；WPF 渲染管线开销不划算 |
| 分层窗口（UpdateLayeredWindow） | 圆角/半透明是真 alpha，Region 圆角会有锯齿 |
| GPU 利用率优先 NVML | PDH 的 `GPU Engine(*)` 有数百实例，collect 一次 10 ms；NVML 是微秒级 |
| 显存走 PDH `GPU Adapter Memory` | 与任务管理器同口径（含系统占用）；NVML 只算进程占用 |
| 网卡列表缓存 30 秒 | `GetAllNetworkInterfaces()` 每次 8–16 ms（虚拟网卡多） |
| 采样在 UI 线程 | 1 秒一次、< 1 ms，跨线程 Post 反而引入抖动和竞态 |
| 主题读 `SystemUsesLightTheme` | 任务栏跟随「Windows 模式」，不是「应用模式」（`AppsUseLightTheme`） |

---

## 7. 绝对不要做

- ❌ 不要用 `Win32_VideoController.AdapterRAM` 取显存容量 —— 32 位字段，12 GB 报成 4 GB。
- ❌ 不要给 WinForms 控件的 `BackColor` 赋带 alpha 的颜色 —— 抛 `Control does not support transparent background colors`。用 `Theme.PanelBgOpaque`。
- ❌ 不要给贴片加 `WS_EX_NOACTIVATE` 以外的激活样式；贴片一旦抢焦点就会打断用户当前窗口。
- ❌ 不要把详情面板做成 `NoActivate = true` —— 它靠 `Deactivate` 事件关闭，NOACTIVATE 窗口不会触发。
- ❌ 不要在 `Blit` 里忘记 `DestroyIcon` / `DeleteObject`（托盘图标每秒重建，漏一个就 GDI 泄漏）。
- ❌ 不要并行改同一个文件（多个 Agent 同时写会互相覆盖）。
- ❌ 不要"顺手"改目录名 `wmonitor-eidt`。

---

## 8. 排障速查

| 现象 | 原因 / 处理 |
|---|---|
| 编译报 `MSB3027` 文件被锁 | 进程还在跑，先 `Stop-Process -Name WMonitor -Force` |
| 贴片窗口存在但内容透明 | 检查绘制是否抛异常（`%APPDATA%\WMonitor\error.log`）；确认 `blit` 返回 true |
| 设置窗口一闪就没了 | 某控件 `BackColor` 带了 alpha，异常被吞；看 error.log |
| 贴片位置偏了 | `TryAnchor` 找 `Shell_TrayWnd` + `TrayNotifyWnd`；自动隐藏时 rect 高度 < 12 会跳过 |
| GPU 显示 0% | 卡空闲是真实值；用 `nvidia-smi` 交叉验证 |
| 温度显示 `—` | NVML 不可用（无 N 卡 / 驱动旧），属正常降级 |
| 开机自启没生效 | 检查 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 的 `WMonitor` 值 |

诊断模式：`WMonitor.exe --probe` 采样 8 次写到 `D:/Hermes/output/results/sysmon-probe.txt`；`WMonitor.exe --icons` 渲染 4 种托盘图标对比图到 `D:/Hermes/output/results/wmonitor-icons.png`（真实 16px + 8 倍放大，改小图标必看）。

v0.10 新增诊断（都不开 UI，跑完就退，结果写 `D:/Hermes/output/results/`）：

| 参数 | 用途 |
|---|---|
| `--monitors` | 枚举全部任务栏与各自 DPI（副屏贴片的前提） |
| `--history-dump` | 内存环条数、今日文件大小、1m/24h 抽稀结果 |
| `--alert-test` | 离线跑告警状态机，输出 PASS/FAIL（触发点、静音、总开关） |

调试钩子：`WMONITOR_DPI_SCALE=1.5` 可在 96 DPI 机器上预览 150% 排版；`WMONITOR_DEBUG=1` 写 `debug.log`（blit 坐标与尺寸）。

---

## 9. 文档地图

- 架构与数据流 → `docs/01-architecture.md`
- 指标来源与口径 → `docs/02-data-sources.md`
- 视觉规格（尺寸/字号/色值）→ `docs/03-ui-spec.md`
- 构建发布细节 → `docs/04-build-release.md`
- 已知限制与踩坑记录 → `docs/05-known-issues.md`
- 后续计划 → `docs/06-roadmap.md`
- 版本历史 → `docs/07-changelog.md`
- **v0.05 施工图（多屏 / 磁盘 IO / 历史曲线 / 阈值告警）→ `docs/08-dev-plan-v005.md`**
- **v0.10 施工进度与交接页 → `docs/09-v010-progress.md`**
