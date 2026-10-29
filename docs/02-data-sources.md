# 02 · 指标数据源

所有指标都不依赖第三方库（不用 LibreHardwareMonitor / OpenHardwareMonitor）。

## 总表

| 指标 | 来源 | 单位 / 口径 |
|---|---|---|
| CPU 占用 | PDH `\Processor Information(_Total)\% Processor Utility` | 0–100，任务管理器同款；睿频可短暂 > 100，已 clamp |
| CPU 频率 | PDH `\Processor Information(_Total)\% Processor Performance` × 注册表基频 | GHz |
| CPU 基频 | `HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0` 的 `~MHz` | MHz → GHz |
| 内存 | `GlobalMemoryStatusEx` 的 `ullTotalPhys` / `ullAvailPhys` | 字节 |
| GPU 利用率 | **NVML** `nvmlDeviceGetUtilizationRates().gpu` | SM 利用率 % |
| GPU 利用率（无 N 卡） | PDH `\GPU Engine(*)\Utilization Percentage` 按 LUID 聚合 | 0–100 |
| 显存占用 | PDH `\GPU Adapter Memory(*)\Dedicated Usage` | 字节，**含系统/驱动占用** |
| 显存总量 | NVML `nvmlDeviceGetMemoryInfo().total`，回落注册表 `HardwareInformation.qwMemorySize` | 字节 |
| GPU 温度 | NVML `nvmlDeviceGetTemperature(NVML_TEMPERATURE_GPU)` | °C |
| GPU 功耗 | NVML `nvmlDeviceGetPowerUsage` | mW → W |
| GPU 名称 | NVML `nvmlDeviceGetName` | 字符串 |
| 网速 | `NetworkInterface.GetIPStatistics()` 的 `BytesReceived/Sent` 差分 | B/s |

## 为什么这些选择

### CPU 用 `% Processor Utility` 而不是 `% Processor Time`

`% Processor Time` 是"CPU 有多忙"，`% Processor Utility` 是"实际交付了多少算力"，后者才是任务管理器显示的那个数。多核 + 睿频下两者能差 20 个百分点以上。

### GPU 利用率优先 NVML

`\GPU Engine(*)` 的实例数是 **进程 × 引擎** 的笛卡尔积，本机实测数百条。`PdhCollectQueryData` 一次要刷新全部实例，实测 **8–16 ms**（占整个采样 90%）。
NVML 的 `nvmlDeviceGetUtilizationRates` 是一次 ioctl，**微秒级**。

差异：NVML 的 `gpu` 只算 SM（3D/Compute），任务管理器是"所有引擎取最大值"。游戏、渲染、AI 推理场景数值一致；纯视频解码时 NVML 会偏低。

代码路径：`MetricSampler` 构造时若 `_nvml.HasUtil == true`，就**完全不创建** GPU Engine 计数器。

### 显存走 PDH 而不是 NVML

- PDH `Dedicated Usage` = 显存控制器视角的专用显存占用，**含系统/驱动/DWM**，与任务管理器一致。
- NVML `memory.used` = 只算进程分配，通常比任务管理器低 0.5–1 GB。

用户看到的是任务管理器，所以跟它对口径。

### 显存容量不能读 `AdapterRAM`

`Win32_VideoController.AdapterRAM` 是 **32 位字段**，12 GB 的 RTX 3060 返回 `4293918720`（≈4 GB）。这是 WMI 的历史遗留，永远不要用。
正确做法：NVML `memory.total`（N 卡），或注册表 `HardwareInformation.qwMemorySize`（QWORD，通用）。

### 网卡列表缓存 30 秒

`NetworkInterface.GetAllNetworkInterfaces()` 每次调用要枚举全部适配器（本机 20+ 个，含 VMware / vEthernet / Tailscale / WSL），实测 **8–16 ms**。
缓存 `List<NetworkInterface>`，只对缓存项读 `GetIPStatistics()`，降到 **0.2–0.7 ms**。

过滤规则（`IsRealNic`）：

```
OperationalStatus == Up
类型不是 Loopback / Tunnel
名称+描述不含：loopback teredo isatap vethernet vmware virtualbox
              hyper-v tailscale wsl npcap bluetooth pseudo
```

## GPU 选择（多显卡）

`GPU Adapter Memory` 的实例名形如 `luid_0x00000000_0x00010FB0_phys_0`，每张卡一条。
默认取 **Dedicated Usage 最大且非零** 的那张（本机为 RTX 3060，核显 Intel UHD 770 通常为 0 或很小）。

`GPU Engine` 的实例名形如：

```
pid_1208_luid_0x00000000_0x00010FB0_phys_0_eng_0_engtype_3D
```

归一化规则：截取 `luid_` 之后到 `_eng_` 之前 → `0x00000000_0x00010FB0_phys_0`，与 Adapter Memory 的键对齐后按卡求和。

`cfg.LockedGpuLuid` 可锁定某张卡（当前设置界面未暴露，预留给多卡用户）。

## 验证命令（git-bash）

```bash
# 计数器是否存在（输出极大，只看前两行）
typeperf "\GPU Adapter Memory(*)\Dedicated Usage" -sc 1 | head -2

# N 卡容量与型号
nvidia-smi --query-gpu=name,memory.total,utilization.gpu,temperature.gpu,power.draw --format=csv

# 不要用这个取容量（32 位截断）
powershell -NoProfile -Command "Get-CimInstance Win32_VideoController | Select Name,AdapterRAM"

# 程序自带诊断
D:/WMonitor/WMonitor/WMonitor.exe --probe
cat D:/Hermes/output/results/wmonitor-probe.txt
```

## 本机基线（i7-12700 / 64 GB / RTX 3060 12 GB）

```
CPU 4–20%（空闲）   4.2 GHz（睿频）/ 1.5 GHz（降频）
RAM 34.4 / 63.7 GB（54%）
GPU 0–2%（空闲）
VRAM 5.5 / 12.0 GB（PDH 口径，含系统）
GPU 温度 53 °C   功耗 14–15 W
网速 0–50 KB/s
LUID：0x00010FB0 = RTX 3060，0x0001224E = UHD 770，0x00012295 = Microsoft Basic Render（恒 0）
```
