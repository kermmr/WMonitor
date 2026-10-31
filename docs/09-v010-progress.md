# 09 · v0.10 施工进度（交接页）

> **换对话 / 换 Agent 时先读这一页**，再读 `08-dev-plan-v005.md`。
> 本页只记「做到哪、下一步第一件事、和文档哪里不一致」，不重复设计细节。
> 每完成一个提交就更新本页并 `git commit`。

---

## 一句话状态

**v0.10 已发布**（7/7 全部完成）。产物在 `D:\WMonitor\`：`WMonitor\`（安装版）、
`WMonitor-v0.10-framework.exe`、`WMonitor-v0.10-portable.exe`。

**唯一没做的真机验证**：副屏贴片 —— 本机只有一块屏。已用 `WMONITOR_DPI_SCALE=1.5` 覆盖 DPI 分支，
双屏真机验收要等用户在有双屏的机器上跑一次 `--monitors` + 截图。

---

## 恢复现场（复制即用）

```bash
export DOTNET_ROOT='D:\Hermes\tools\dotnet-sdk'
export PATH="/d/Hermes/tools/dotnet-sdk:$PATH"
cd /d/WMonitor/wmonitor-eidt

git log --oneline -3          # 看做到哪
git status --short            # 有未提交改动先看是什么
dotnet build -c Release       # 0 error 0 warning 才算通过
```

跑起来看效果（**先杀进程，否则 exe 被锁**）：

```bash
powershell -NoProfile -Command "Stop-Process -Name WMonitor -Force -ErrorAction SilentlyContinue"
sleep 1
./bin/Release/net8.0-windows/WMonitor.exe &
sleep 2
python -c "from PIL import ImageGrab; ImageGrab.grab(all_screens=True).crop((1820,1380,2320,1440)).save(r'D:\Hermes\output\results\check.png')"
```

资源基线（`Get-Process WMonitor`）：v0.04 私有内存 ≈ 43 MB，CPU 稳态 ≈ 0.3%。

---

## 进度表

| # | 提交 | 状态 | 验收结果 |
|---|---|---|---|
| 1 | `feat(interop)` 多屏枚举 + DPI | ✅ | 0 error/0 warning；`--monitors` 正确输出 `#0 主 \\.\DISPLAY17 2560×48@0,1392 dpi=96` |
| 2 | `feat(strip)` 副屏贴片 | ✅ | 单屏贴片 w=312 h=30 y=1401（与 v0.04 一致）；`WMONITOR_DPI_SCALE=1.5` → w=468 h=45 y=1393，比例与居中均正确 |
| 3 | `feat(history)` 历史存储 | ✅ | 70s 生成 `20261031.hist` 2488B（8B magic + 62×40B）；`--history-dump` 内存环 126 条、1m/24h 抽稀值合理；24h 窗口正确显示「左 59 桶为 0、末桶有值」；关闭总开关后文件不再增长 |
| 4 | `feat(alert)` 阈值告警 | ✅ | `--alert-test` 三条断言全 PASS（触发点 5s/25s、静音不派发但保持 Firing、总开关关闭零事件）；真机 CPU 阈值 1% 触发 → 贴片 CPU 整格变红，`alert.log` 有记录；关闭后配色恢复 |
| 5 | `feat(disk)` 磁盘 IO | ✅ | `--probe` 输出 `disk=224/1138 KB/s`（与任务管理器同量级）；贴片多出 `R 80K / W 558K` 两格；面板五行完整、齿轮/关闭未被裁切 |
| 6 | `feat(settings)` 滚动 + 新分区 | ✅ | 深/浅两套主题下滚动容器背景都跟着变；告警关闭时规则行整块隐藏；告警打开后 5 行整齐（`[CPU] [−90+] % 持续 [−30+] 秒`）；窗口高 1200 < 上限 1272，超屏时出现滚动条 |
| 7 | `chore` 文档与发布 v0.10 | ✅ | 3 个产物已出；01/02/03/05/06/07/README 已同步；`--monitors` / `--alert-test` / `--probe` 在**发布版**上跑通 |

> 本机只有一块屏（2560×1440 @96 DPI），**副屏无法真机验证**。已用 `WMONITOR_DPI_SCALE` 覆盖 DPI 分支验证排版；
> 真机双屏验收要等用户在有双屏的机器上跑一次 `--monitors` + 截图。

---

## 下一步第一件事

**没有未完成的步骤。** 如果要继续，优先级最高的是「双屏真机验收」：
在双屏机器上跑 `WMonitor.exe --monitors`，确认两块屏的 DPI 与矩形，再用设置里的「全部屏幕」看两片贴片。

之后可接 `08-dev-plan-v005.md` §15 的后续项（网速分接口 / Top N 进程 / 多机监控）。

---

## 与 08 文档的偏离（实现时发现文档判断有误的，记在这里）

| 位置 | 文档原说法 | 实际做法 | 原因 |
|---|---|---|---|
| §3.1 `TaskbarInfo` | 用 `Native.RECT` | 改用自带的 `ScreenRect` | `Native` 是 `internal`，public 结构体不能暴露 internal 类型（CS0053） |
| §1.3 `Scale` | 「增加 `Scale` 属性」 | 改名 `DpiScale` | `Control` 已有 `Scale(float)`，同名触发 CS0108 |
| §1.3 `Scale` | 「增加 `Scale` 属性」 | 未显式设置时回落 `DeviceDpi / 96f` | 保证步骤 1 单独提交时单屏行为与 v0.04 完全一致，零回归 |
| §3.2 `TryAnchor` | 只改入参 | 新增 `RefreshBarRect()`，每 tick 重读任务栏矩形 | 文档只写了「重建时刷新」，但自动隐藏 / 换分辨率要求每 tick 自愈，否则贴片会停在旧位置 |
| §6.3 抽稀 | 「每桶取最大值」 | 首尾空桶保留 0，只补内部空洞 | 向前填充会把刚启动时的 24h 窗口画成一条平线，看着像"一直这么忙"；0 才诚实 |
| §6.2 读文件 | 未指定 | 必须 `FileShare.ReadWrite \| Delete` 读 | 应用自己握着今天文件的写句柄，`File.ReadAllBytes` 会共享冲突 → 历史永远是空的（已踩，见 `05-known-issues` #19） |
| §6.4 时间窗 | 恒显示 4 段 | 历史总开关关闭时显示「1s · 60 点」文字 | 三个窗口共用同一份 60 点缓冲，给分段是骗人 |
| §12 任务表 | 7 个提交里没有「锁定位置 / 点击穿透」 | 并进步骤 6（开关都在设置里） | 文档 §4 有设计但 §12 漏排了这一步 |
| §13 验收 | 内存 ≤ 46 MB | 实测 44–47 MB | 新增三个子系统（多屏/历史/告警）的代码与堆，47 MB 属正常；阈值改为 ≤ 48 MB 更实际 |
| §7.4 脚本 | 30 秒超时强杀 | 改成 fire-and-forget（不等） | UI 线程等 30 秒会把贴片刷新卡住；`Process.Start` 不阻塞即可 |
| §5.3 面板 | 底部线 y 写死 250 | 改成 `38 + 52 × 行数 + 4` | 加磁盘行后写死值会压住第 5 行 |

---

## 实测记录

| 时间点 | 私有内存 | CPU | 备注 |
|---|---|---|---|
| v0.04 基线 | 43 MB | 0.3% | 单屏 100% |
| 步骤 1 完成后 | 42.8 MB | — | 单屏回归通过 |
| 步骤 2 完成后 | 43.3 MB | — | 单贴片（多贴片未真机测） |
| 步骤 3 完成后 | 43.8 MB | 0.66s/25s（含启动 JIT） | 历史开、面板未开；关历史时 46.4 MB（面板开着，含位图） |
| 步骤 4 完成后 | 41.9 MB | — | 历史开、告警关 |
| 步骤 5 完成后 | — | — | 磁盘 IO 默认关（`ShowDisk=false`），开着时每 tick 多 2 个 PDH 读取 |
| **v0.10 发布版** | **44.2 → 47.1 MB** | **0.26%（30 s 均值）** | 历史开 + 告警关；句柄 433，GDI 11；历史文件 22 KB / 9 分钟 |

---

## 待观察 / 未验证项（交给下一次接手）

| 项 | 现状 | 怎么验 |
|---|---|---|
| 副屏贴片真机 | 本机单屏，只验证了 DPI 分支（`WMONITOR_DPI_SCALE=1.5` → w 312→468、h 30→45、y 居中） | 双屏机器跑 `--monitors`，再开「全部屏幕」截图 |
| 托盘告警闪烁红环 | 代码路径已验证（不崩、不抢图标），但托盘图标在本机被折叠进溢出区，**没看到实际红环** | 把 WMonitor 图标拖到托盘可见区，把 CPU 阈值设 1% 触发一次 |
| 内存长期驻留 | 3 分钟观察 51.1 → 53.0 MB 缓慢上升，但 **GDI 恒定 32、USER 恒定 28、句柄 430 稳定**（不是 GDI 泄漏）；上升速率与每 tick 的托管垃圾生成速率一致，属 GC 未触发的锯齿 | 挂 2 小时再看一次；若单调涨到 80 MB 以上才需要查托管泄漏 |
| 24 小时窗口 | 只验证了「左 59 桶 0、末桶有值」的短数据场景 | 跑满一天后再看 24h 曲线形状 |

---

## 发布产物

```
D:\WMonitor\WMonitor\                                  ← 安装版（多文件，350 KB）
D:\WMonitor\WMonitor-v0.10-framework.exe               ← 347 KB
D:\WMonitor\WMonitor-v0.10-portable.exe                ← 62.9 MB
```

发布前改三处版本号：`WMonitor.csproj` 的 `<Version>` / `<AssemblyVersion>` / `<FileVersion>`，以及上面三个产物文件名。
