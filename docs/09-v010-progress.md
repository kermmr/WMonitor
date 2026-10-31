# 09 · v0.10 施工进度（交接页）

> **换对话 / 换 Agent 时先读这一页**，再读 `08-dev-plan-v005.md`。
> 本页只记「做到哪、下一步第一件事、和文档哪里不一致」，不重复设计细节。
> 每完成一个提交就更新本页并 `git commit`。

---

## 一句话状态

**步骤 2/7 完成**：多屏枚举 + 每屏 DPI + 多贴片实例（单屏回归通过，1.5× 排版实测正确）。
下一步是步骤 3：历史存储 `HistoryStore` + 面板时间窗。

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
| 3 | `feat(history)` 历史存储 | ⬜ | — |
| 4 | `feat(alert)` 阈值告警 | ⬜ | — |
| 5 | `feat(disk)` 磁盘 IO | ⬜ | — |
| 6 | `feat(settings)` 滚动 + 新分区 | ⬜ | — |
| 7 | `chore` 文档与发布 v0.10 | ⬜ | — |

> 本机只有一块屏（2560×1440 @96 DPI），**副屏无法真机验证**。已用 `WMONITOR_DPI_SCALE` 覆盖 DPI 分支验证排版；
> 真机双屏验收要等用户在有双屏的机器上跑一次 `--monitors` + 截图。

---

## 下一步第一件事

**步骤 3**：新建 `Metrics/HistoryStore.cs`（40 B 定长记录 + 3600 条内存环 + 按天滚动磁盘文件），
再把 `DetailPanel` 标题行右侧换成 4 个可点击时间窗段（详见 `08-dev-plan-v005.md` §6）。

---

## 与 08 文档的偏离（实现时发现文档判断有误的，记在这里）

| 位置 | 文档原说法 | 实际做法 | 原因 |
|---|---|---|---|
| §3.1 `TaskbarInfo` | 用 `Native.RECT` | 改用自带的 `ScreenRect` | `Native` 是 `internal`，public 结构体不能暴露 internal 类型（CS0053） |
| §1.3 `Scale` | 「增加 `Scale` 属性」 | 改名 `DpiScale` | `Control` 已有 `Scale(float)`，同名触发 CS0108 |
| §1.3 `Scale` | 「增加 `Scale` 属性」 | 未显式设置时回落 `DeviceDpi / 96f` | 保证步骤 1 单独提交时单屏行为与 v0.04 完全一致，零回归 |
| §3.2 `TryAnchor` | 只改入参 | 新增 `RefreshBarRect()`，每 tick 重读任务栏矩形 | 文档只写了「重建时刷新」，但自动隐藏 / 换分辨率要求每 tick 自愈，否则贴片会停在旧位置 |

---

## 实测记录

| 时间点 | 私有内存 | CPU | 备注 |
|---|---|---|---|
| v0.04 基线 | 43 MB | 0.3% | 单屏 100% |
| 步骤 1 完成后 | 42.8 MB | — | 单屏回归通过 |
| 步骤 2 完成后 | 43.3 MB | — | 单贴片（多贴片未真机测） |

---

## 发布产物（步骤 7 才做）

```
D:\WMonitor\WMonitor\                                  ← 安装版（多文件）
D:\WMonitor\WMonitor-v0.10-framework.exe               ← 299 KB 级
D:\WMonitor\WMonitor-v0.10-portable.exe                ← 63 MB 级
```

发布前改三处版本号：`WMonitor.csproj` 的 `<Version>` / `<AssemblyVersion>` / `<FileVersion>`，以及上面三个产物文件名。
