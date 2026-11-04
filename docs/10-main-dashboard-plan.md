# 10 · 资源管控主面板详细方案

> 状态：**四阶段全部完成 —— 阶段 1（v0.18）、阶段 2（v0.19，GPU 进程指标）、阶段 3（v0.20，应用识别与体验）、阶段 4（v0.21，管控）；v0.22 复验修复、v0.23 UI 细节修复（2026-11-04）**  
> 阶段 1 交接与验收数据 → `docs/11-phase1-progress.md`；阶段 2 → `docs/12-phase2-progress.md`；阶段 3 → `docs/13-phase3-progress.md`；阶段 4（管控）→ `docs/14-phase4-progress.md`  
> 草图：`D:\WMonitor\main-dashboard-sketch.svg`  
> 目标：给 WMonitor 增加一个真正的主面板窗口，用于查看「CPU / 内存 / GPU / 显存是哪些应用在占用」，后续再开放安全的进程管控能力。  
> 本方案不改任务栏贴片、详情面板和现有托盘交互。

## 1. 产品结论

主面板定位为「资源占用应用面板」，不是第二个任务管理器。第一版优先保证数据准确、界面清晰、资源占用低；进程管控放在第二阶段，并全部要求二次确认。

用户目标：

1. 快速看到哪些应用占用 CPU、内存、GPU 和显存。
2. 区分按应用聚合与按进程明细，避免 Chrome、VS Code、游戏等多进程应用刷屏。
3. 主面板关闭时不能增加后台成本。
4. GPU 数据要尽量对齐任务管理器口径，同时保留多显卡扩展能力。
5. 管控操作必须显式确认，避免第一版误杀系统进程。

## 2. UI 结构

建议窗口尺寸 1280 × 800，最小 1040 × 660。使用普通 WinForms `Form`，不继承 `LayeredWindow`；原因是主面板需要正常焦点、任务栏项、缩放、最大化、失焦关闭策略、表格滚动和键盘操作，复用浮层会引入不必要的生命周期问题。

视觉沿用现有 `Theme`、`BentoCard` / `BentoButton` 的深浅色风格，但不要直接嵌 `DetailPanel`；主面板是普通窗口，拥有独立状态和布局。

### 2.1 顶部总览区

四张指标卡：

| 卡片 | 主值 | 次值 | 来源 |
|---|---|---|---|
| CPU | `43%` | `16 线程 · 4.2 GHz` | `MetricSampler` |
| 内存 | `56%` | `17.9 / 32.0 GB` | `MetricSampler` |
| GPU | `12%` | `NVIDIA · 62°C · 48W` | `MetricSampler` |
| 显存 | `35%` | `4.2 / 12.0 GB` | `MetricSampler` |

卡片右侧放主面板局部控制：

- 刷新：跟随全局、0.5s、1s、2s。
- 显示后台：默认关闭；关闭时优先通过窗口标题和可见窗口推断「有界面应用」，而不是简单把非空标题进程当成应用。
- 设置：复用现有设置窗口；不要在主面板重复承载现有全局设置。

第一版不要加总览迷你曲线，避免和详情面板功能重叠。

### 2.2 视图与筛选

分段控件：

1. `按应用`：默认视图。把同一路径 / 相同进程名的主组聚合。
2. `按进程`：每个 PID 一行。
3. `GPU`：按 PID 聚合 GPU 占用和显存；无 GPU 进程不显示。

筛选项：

- 搜索：应用名、进程名、PID、路径。
- 排序：CPU、内存、GPU、显存、名称、PID。
- 最小占用阈值：默认 0；主面板打开时可过滤长期为 0 的系统后台项。
- 显示系统进程：默认关闭，建议以 `SYSTEM`、`LOCAL SERVICE`、`NETWORK SERVICE`、关键系统路径为条件，而不是只看 PID。
- 多显卡筛选：存在多 LUID 时显示下拉；单卡隐藏。

第一版可先做搜索、排序和「按应用 / 按进程 / GPU」切换；其余放到第二或第三阶段。

### 2.3 主列表

统一列定义：

| 列 | 内容 | 说明 |
|---|---|---|
| 应用 | 图标 + 名称 + 路径 | 图标缓存到磁盘，失败时用首字母圆点；路径截中显示 |
| PID | 数字 | 按进程视图显示；按应用视图显示聚合 PID 数 |
| CPU | 百分比 | 1 秒差分；一行内显示总量 |
| 内存 | MB / GB | 按应用聚合 WorkingSet，并显示 Private Bytes 详情 |
| GPU | 百分比 | PDH 按进程聚合 |
| 显存 | MB / GB | 专用显存为主值，共享显存放详情 |
| 状态 | Running / Suspended / Elevated | 第二阶段显示 |
| 操作 | 详情 / 结束 | 管控动作第二阶段启用 |

按应用聚合规则：

1. 主键优先使用进程组主窗口拥有者：如果多个 PID 里有可见主窗口，选它作为组代表。
2. 其次使用完整路径 + 文件版本信息里的产品名/文件描述。
3. 最后使用进程名。同名但路径不同不合并。
4. 可见主窗口、前台窗口、有 GPU 占用、非空标题可提升排名。
5. 聚合 CPU、内存、GPU、显存，保留成员数和主 PID。

不采用「同 exe 文件名即应用」的简单规则，避免把不同安装目录的工具合并。

### 2.4 底部详情区

选中行后显示：

- 展开该应用下的全部 PID。
- 主 PID、启动时间、架构、状态、权限。
- CPU、内存、GPU、显存、主要 GPU 引擎类型。
- 路径、命令行、公司/描述、启动时间。
- 第二阶段显示动作按钮：打开文件位置、结束任务、挂起/恢复、降优先级。

第一版底部区域可以先做只读详情，或先只做选中 PID 高亮；如果为了尽快交付，建议先保留展开子进程列表，因为这对判断浏览器等多进程应用很有价值。

## 3. 数据架构

当前 `MetricSampler` 继续负责全局快照，不建议把进程级采样直接塞进去。新增独立采样器，再由一个 hub 统一合成。

```text
System.Windows.Forms.Timer
  └─ MainDashboardTick
       ├─ MetricSampler.Sample()          // 继续或复用主循环的全局快照
       ├─ ProcessSampler.Sample()         // PID / 名称 / 路径 / CPU / 内存
       ├─ GpuProcessSampler.Sample()      // GPU 占用 / 进程显存 / 引擎
       ├─ AppMetricsHub.Combine()
       └─ MainDashboardForm.UpdateMetrics()
```

推荐主面板启用期间使用 1 秒 tick。由于 `GpuProcessSampler` 需要读取数百个 GPU Engine 实例，GPU 子采样可每 2 秒执行一次，并在两次更新间保持上一帧数值。

### 3.1 `Metrics/ProcessSampler.cs`

职责：

1. `EnumProcesses` 或 `Process.GetProcesses()` 获取 PID 列表。
2. 打开进程句柄，读取基本名称。
3. `QueryFullProcessImageName` 读完整路径。
4. `GetProcessTimes` 记录当前内核 + 用户 CPU 时间。
5. 与上一帧差分：
   `CPU% = (newTotal - oldTotal) / interval / Environment.ProcessorCount × 100`
6. `GetProcessMemoryInfo` 读 WorkingSet、Private Bytes。
7. `IsWow64Process2` 判断架构。
8. 使用低权限打开失败时保留旧路径/名称，并标记受限。

字段建议：

```csharp
public sealed class ProcessMetric
{
    public int Pid;
    public int ParentPid;
    public string Name = "";
    public string? Path;
    public string? Company;
    public string? Description;
    public string? WindowTitle;
    public bool HasVisibleWindow;
    public bool IsForeground;
    public bool IsTopmost;
    public long WorkingSet64;
    public long PrivateMemory64;
    public double CpuPercent;
    public long KernelTime100Ns;
    public long UserTime100Ns;
    public DateTime? StartedAt;
    public bool AccessLimited;
    public bool Suspended;
    public string? Architecture;
}
```

> 上面是接口草案。实现时把窗口句柄、标题、可见性等字段展开为具体 C# 类型，不要保留伪代码。

`Process.GetProcesses()` 会创建大量托管对象，长期 1 Hz 会产生 GC 压力。第一版允许先用它快速落地，但代码里必须隔离 PID 枚举、句柄读取和缓存逻辑，后续切换到 `NtQuerySystemInformation` 才不会大面积重写。

第一版不强制读取命令行。原因：64 位 / 提权进程读取 `PEB` 或 WMI `CommandLine` 都有额外权限和性能问题，主列表的路径 + 文件描述通常已经足够定位应用。

### 3.2 `Metrics/GpuProcessSampler.cs`

数据源：

| 指标 | 数据源 | 口径 |
|---|---|---|
| GPU 占用 | PDH `\GPU Engine(*)\Utilization Percentage` | 按 `pid_xxx_luid_yyy_phys_n_engtype_z` 拆 PID、LUID、引擎类型，按 PID/LUID 聚合 |
| 专用显存 | PDH `\GPU Process Memory(*)\Dedicated Usage` | 与任务管理器进程页签的专用显存口径接近 |
| 共享显存 | PDH `\GPU Process Memory(*)\Shared Usage` | 次要指标 |
| 总 GPU 利用率 / 温度 / 功耗 | 现有 `MetricSampler` + NVML | 总览和校验 |

实例名解析：

```text
GPU Engine:
pid_1208_luid_0x00000000_0x00010FB0_phys_0_eng_0_engtype_3D

GPU Process Memory:
pid_1208_phys_0_eng_0_engtype_3D
```

解析要点：

1. PID 是稳定锚点，不要用实例序号。
2. 引擎类型保留原文：`3D`, `Copy`, `VideoDecode`, `VideoEncode`, `VideoProcessing`, `Cuda`, `Security`, `DWM`, `VR`, `Overlay`, `Encryption/Decryption`。
3. 按 `PID + LUID` 汇总。
4. GPU Engine 的一次占用值本质是瞬时值；建议做 3 帧指数滑动平均，避免跳动。
5. LUID 对齐现有 `MetricSampler` 的 `LockedGpuLuid` / 主卡选择逻辑。
6. PDH 读取前检查 PDH 返回值；`PDH_MORE_DATA` 需要扩容重试，其他错误降级显示「GPU 进程数据不可用」。

注意：现有 `MetricSampler` 只在无 NVML 时创建 GPU Engine query。主面板需要独立的 `GpuProcessSampler` 和独立 PDH query；不要和全局采样器共享句柄，避免实例变动、扩容和 dispose 竞争。

### 3.3 `Metrics/AppMetricsHub.cs`

职责是聚合和生命周期：

1. 持有 `ProcessSampler`、`GpuProcessSampler`。
2. 只在主面板可见时 `Start()`，主面板关闭后 `Dispose()`。
3. 对 PID 做 5–15 秒缓存：名称、路径、公司、描述、图标、架构、启动时间。
4. 对短生命周期 PID 保留 10–15 秒，避免瞬时进程闪烁。
5. 每帧输出不可变快照 `AppMetricsSnapshot`。
6. 暴露 `event Action<AppMetricsSnapshot>`，UI 通过 `BeginInvoke` 更新，或在现有 UI timer 中 `TryDequeue`。
7. 若使用后台线程，必须只产出不可变快照，UI 只消费快照；不要让 UI 和采样线程共享可变集合。
8. 第一次打开主面板做同步一次采样，避免空列表。
9. 打开期间 GC 压力大时，复用内部集合并在输出前快照拷贝；不要用 LINQ 产生大量临时对象。

### 3.4 图标与路径缓存

- 缓存键：完整路径 + 文件修改时间。
- 磁盘目录：`%APPDATA%\WMonitor\icon-cache\`。
- 内存 LRU：约 200 个 32px 图标。
- 抽取 API：`SHDefExtractIcon` 或 `PrivateExtractIcons`。
- 提取失败时保存空标记文件，避免每帧重试。
- 文件描述与产品名用 `GetFileVersionInfo` 缓存。

### 3.5 权限模型

| 场景 | 表现 |
|---|---|
| 普通应用 | 可读名称、路径、CPU、内存、GPU |
| 提权应用 | 名称通常可读；路径/命令行/结束操作可能受限，显示「受限」 |
| 系统进程 | 默认隐藏；打开系统进程后只读优先，路径可能为空 |
| 主程序以管理员运行 | 可读性更高，但默认不建议；只作为手动选项 |

如果需要提权重启，用 `runas` verb 启动自身，加 `--elevated` 参数，并提示用户确认；第二版前不要自动提权。

## 4. 类与接入点

### 4.1 新增类

| 文件 | 职责 |
|---|---|
| `UI/MainDashboardForm.cs` | 主面板窗口、总览卡、筛选、表格、详情区、局部状态 |
| `Metrics/ProcessSampler.cs` | 进程枚举、CPU/内存差分、路径和基本信息 |
| `Metrics/GpuProcessSampler.cs` | PDH GPU Engine / GPU Process Memory 采样和解析 |
 | `Metrics/AppMetricsHub.cs` | 调度采样、缓存、合成快照 |

### 4.2 现有文件修改

| 文件 | 修改点 |
|---|---|
| `AppConfig.cs` | 增加主面板字段：启用、默认视图、排序、刷新间隔、显示后台、显示系统进程、最小占用阈值 |
| `MonitorAppContext.cs` | 持有/创建/销毁主面板；主面板可见时调度 AppMetricsHub |
| `DetailPanel.cs` | 增加主面板入口按钮 |
| `FlyoutMenu.cs` 或 `MonitorAppContext.cs` | 托盘菜单增加「资源面板…」 |
| `Native.cs` | 按需增加进程、窗口、图标、性能计数器辅助 API |

### 4.3 生命周期

1. 程序启动：不创建主面板，也不创建 `AppMetricsHub`。
2. 用户打开主面板：创建 `MainDashboardForm`，启动 hub，第一次采样同步返回。
3. 主面板关闭：dispose hub，清除临时列表；图标磁盘缓存保留。
4. 主面板最小化：可暂停渲染更新，建议继续采样 30 秒，避免任务栏恢复后长时间空数据。
5. 设置窗口打开时不暂停主面板采样；两个窗口不互相抢状态。
6. 程序退出：随 `MonitorAppContext.Quit()` 一起 dispose。

### 4.4 配置草案

```csharp
public bool DashboardEnabled { get; set; } = true;
public int DashboardRefreshMs { get; set; } = 1000;
public int DashboardGpuRefreshMs { get; set; } = 2000;
public string DashboardDefaultView { get; set; } = "App";
public string DashboardSort { get; set; } = "Cpu";
public bool DashboardShowBackground { get; set; } = false;
public bool DashboardShowSystemProcesses { get; set; } = false;
public double DashboardMinimumPercent { get; set; } = 0;
public bool DashboardRememberLayout { get; set; } = true;
```

配置写入 `%APPDATA%\WMonitor\config.json`，使用 `System.Text.Json` 和现有枚举/JSON 风格。建议把默认视图和排序改成强类型枚举，`DashboardDefaultView` / `DashboardSort` 草案字段先保留字符串，落地时统一改为 enum。

## 5. 性能预算

主面板关闭：

- 目标：与 v0.17 当前基线一致。
- 不允许常驻 hub、进程枚举和 GPU Engine query。

主面板打开：

| 采样项 | 频率 | 预算 |
|---|---|---|
| 全局指标 | 1 Hz | 继续复用主循环，不重复创建全局 PDH |
| 进程 CPU/内存 | 1 Hz | ≤ 4 ms |
| GPU Engine + GPU Process Memory | 0.5 Hz | ≤ 8 ms，目标 ≤ 5 ms |
| UI 绘制 | 1 Hz | 仅重绘数据单元格，不重建控件 |
| 图标/路径缓存 | 5–15 秒或缺失时刷新 | 平均摊薄到 < 0.5 ms/tick |

预期主面板打开时额外内存 15–30 MB，稳态 CPU 增量不超过 1%。首帧或刷新图标时可短暂超过，但不能持续超过 3 秒。

若 GPU Engine 读取持续超过 12 ms，做以下降级：

1. GPU 进程采样改为 3–5 秒。
2. 显示「GPU 应用：暂缓刷新」。
3. 总览仍用现有 NVML / 全局 PDH。

## 6. 数据准确性验收

准备两个对照源：

1. 任务管理器「进程」页签：应用 CPU、内存、GPU、显存。
2. 任务管理器「性能 → GPU」或 `nvidia-smi`：总 GPU 利用率、专用显存。

验收场景：

| 场景 | 检查点 |
|---|---|
| 空载 | 总览与现有详情面板一致；无异常大占用行 |
| Chrome 多进程 | 按应用视图聚合成一行；按进程视图能看到子进程 |
| 视频播放 / 视频导出 | GPU 进程能定位到播放器/导出器，引擎类型合理 |
| 游戏 / 3D | GPU 占用集中在主渲染 PID；总览与 NVML 数值趋势一致 |
| AI 推理 | GPU 占用和专用显存集中在推理进程 |
| 系统进程隐藏 | 默认列表不出现 Registry、System、csrss 等系统项 |
| 权限受限 | 不误报 0%；显示受限并保留旧名称 |
| 多进程闪现 | 短生命周期进程不闪烁 |
| 主面板关闭 5 分钟 | WMonitor 私有内存回到接近原基线；无残留 PDH/NVML 句柄 |
| 长时间开启 1 小时 | 内存稳定，不出现持续增长 |
| 深浅主题 | 主面板颜色可读、状态和按钮对比度足够 |
| DPI 100/125/150% | 文本不裁切、行高稳定 |

数值容差：

- CPU：任务管理器本身按 1 秒窗口抖动，稳定负载下 ±5 个百分点内可接受。
- 内存：说明口径；第一版主列表优先 WorkingSet，详情显示 Private Bytes。
- GPU 占用：与任务管理器趋势一致，±8 个百分点内可接受。
- 显存：专用显存与任务管理器进程页签相差 ≤ 128 MB；大进程可放宽到 256 MB。

## 7. 施工顺序

### 阶段 1：只读主面板与 CPU/内存

1. 新增 `AppConfig` 主面板字段。
2. 新增 `ProcessSampler`，实现 PID、名称、路径、CPU 差分、WorkingSet、PrivateMemory。
3. 新增 `AppMetricsHub`，先只合全局快照和进程指标。
4. 新增 `MainDashboardForm`：总览卡、按应用/按进程切换、搜索、排序、表格、选中详情。
5. 接入 `MonitorAppContext`，详情面板和托盘菜单加入口。
6. 验证 1 Hz CPU/内存，资源预算达标。

### 阶段 2：GPU 进程指标

1. 新增 `GpuProcessSampler`。
2. 解析 GPU Engine 和 GPU Process Memory 实例名。
3. 聚合 PID/LUID/引擎。
4. 列表增加 GPU、显存列。
5. 增加 GPU 视图和滑动平均。
6. 对照任务管理器验收。

### 阶段 3：应用识别与体验

1. 图标缓存。
2. 文件版本信息缓存。
3. 窗口标题 / 可见主窗口 / 前台进程识别。
4. 按应用聚合增强。
5. 显示后台、系统进程过滤、多卡筛选、最小占用阈值。
6. 空态、受限态、加载态和错误提示。

实际施工时允许阶段 1 提前实现简单按应用聚合：完整图标与窗口状态可以在阶段 3 补齐。

### 阶段 4：管控

1. 只对用户确认后的目标进程执行。
2. 操作前显示名称、PID、路径、影响范围。
3. 按应用聚合行默认只展示确认目标，不批量杀全部子进程；需要批量时明确提示数量。
4. `TerminateProcess` / `Process.Kill` 结束进程。
5. `NtSuspendProcess` / `NtResumeProcess` 挂起/恢复。
6. `SetPriorityClass` 降优先级。
7. 每次操作写 `%APPDATA%\WMonitor\dashboard-actions.log`。
8. 操作失败按错误码给出中文原因：权限不足、已退出、句柄失效、需要管理员。

安全规则：

1. 不提供系统进程默认列表。
2. 系统进程的操作入口默认禁用。
3. 提权进程显示受限，不静默失败。
4. 结束任务必须二次确认，并要求输入/勾选确认；不要只用 OK。
5. 挂起 DWM、Explorer、AudioDutyCycleDriver 等关键进程应特别提示风险。
6. 不自动结束进程，除非用户后续明确要求规则化自动管控，并单独设计白名单/黑名单和告警前置条件。

## 8. 测试与诊断

建议新增诊断参数：

| 参数 | 用途 |
|---|---|
| `--dashboard-probe` | 采 8 次进程/GPU 指标，输出 JSON 或文本，不打开窗口 |
| `--dashboard-gpu-probe` | 单独验证 GPU Engine / GPU Process Memory 解析 |
| `--dashboard-ui` | 只打开主面板，不启动贴片与详情面板，方便截图和调试 |
| `--dashboard-bench` | 输出每帧采样耗时直方图 |

验证命令：

1. `dotnet build -c Release`，要求 0 error 0 warning。
2. 启动后打开主面板，观察进程列表与任务管理器。
3. 开启 Chrome 多进程、视频播放、AI 推理或游戏负载做对照。
4. 关闭主面板后等 5 分钟，检查 `Get-Process WMonitor | Select-Object PrivateMemorySize64, TotalProcessorTime`。
5. 检查 `%APPDATA%\WMonitor\error.log`。
6. `--dashboard-probe` 保存结果，确认 PID、CPU、内存、GPU、显存字段有效。

## 9. 明确不做的事

1. 第一版不做自动杀进程。
2. 不做「GPU 限速」，Windows 驱动没有稳定公开的按进程 GPU 限制 API；只能挂起/结束进程或由应用自降负载。
3. 第一版不持久化每个进程的历史曲线。
4. 不把主面板替换详情面板。
5. 不做第三方进程监控库依赖。
6. 不在主面板重复承载全部全局设置。
7. 不承诺读取所有提权进程的完整命令行。
8. 不使用 `Win32_VideoController.AdapterRAM` 判断显存容量。

## 10. 交接注意

1. 只改 `wmonitor-eidt/`。
2. 编译环境按 `AGENTS.md` 设置 `DOTNET_ROOT`。
3. 发布前必须杀掉 `WMonitor*` 进程。
4. 新增 WinForms 控件不使用带 alpha 的 `BackColor`。
5. 主面板是普通 `Form`，不要改成 `LayeredWindow` 或 `NoActivate`。
6. 主面板与全局采样器只交换不可变快照，不要共享 PDH query。
7. GPU Engine 开销高，独立建 query，并控制采样频率。
8. 图标、路径、版本信息必须缓存。
9. UI 刷新前判断 `IsHandleCreated` 和 `IsDisposed`，避免关闭瞬间回调崩溃。
10. 完成每个阶段后更新本文档状态和 `docs/07-changelog.md`。
