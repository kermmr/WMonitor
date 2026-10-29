# 04 · 构建与发布

## 环境

| 项 | 值 |
|---|---|
| OS | Windows 11（本机 26300.9711） |
| SDK | .NET 8.0.427，路径 `D:\Hermes\tools\dotnet-sdk`（**非系统路径**） |
| 运行时 | `C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\8.0.26`（系统已有） |
| Shell | git-bash（MSYS），`terminal` 工具走的是这个 |
| 依赖 | 无第三方 NuGet 包 |

### 每次开新会话都要设

```bash
export DOTNET_ROOT='D:\Hermes\tools\dotnet-sdk'
export PATH="/d/Hermes/tools/dotnet-sdk:$PATH"
```

验证：

```bash
dotnet --list-sdks
# 8.0.427 [D:\Hermes\tools\dotnet-sdk\sdk]
```

### 换机器怎么装 SDK

```bash
curl --ssl-no-revoke -sL -o dotnet-install.ps1 https://dot.net/v1/dotnet-install.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File dotnet-install.ps1 \
  -Channel 8.0 -Quality GA -InstallDir D:/Hermes/tools/dotnet-sdk -NoPath
```

（`--ssl-no-revoke` 是必须的：本机 git-bash 的 curl 走 schannel，吊销列表离线会直接失败。）

---

## 构建

```bash
cd /d/WMonitor/wmonitor-eidt
dotnet build -c Release
```

产物：`bin/Release/net8.0-windows/win-x64/WMonitor.exe`（调试用，不建议直接运行）。

**构建前必须杀进程**，否则 exe 被占用会报 `MSB3027 / MSB3021`：

```bash
powershell -NoProfile -Command "Stop-Process -Name WMonitor -Force -ErrorAction SilentlyContinue"
sleep 1
```

---

## 三种发布

### ① 安装版（多文件，框架依赖）→ `D:\WMonitor\WMonitor\`

```bash
dotnet publish -c Release -r win-x64 --self-contained false \
  -p:PublishSingleFile=false -o "D:/WMonitor/WMonitor"
```

产物 4 个文件，共 278 KB：

```
WMonitor.exe              173 KB   启动器
WMonitor.dll               97 KB   实际代码
WMonitor.deps.json                依赖清单
WMonitor.runtimeconfig.json       运行时配置
```

这是日常使用的位置 —— 启动最快、私有内存最低（43 MB）。

### ② 单文件 · 框架依赖 → `D:\WMonitor\WMonitor-v0.01-framework.exe`

```bash
dotnet publish -c Release -r win-x64 --self-contained false \
  -p:PublishSingleFile=true -o build/framework
cp build/framework/WMonitor.exe "D:/WMonitor/WMonitor-v0.01-framework.exe"
```

276 KB。需要目标机器装 .NET 8 桌面运行时。

### ③ 单文件 · 自包含 → `D:\WMonitor\WMonitor-v0.01-portable.exe`

```bash
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -o build/portable
cp build/portable/WMonitor.exe "D:/WMonitor/WMonitor-v0.01-portable.exe"
```

63 MB。首次启动会解压到 `%TEMP%\.net\`，私有内存 78 MB。

> **不要加 `PublishTrimmed=true`** —— WinForms 不支持裁剪，会在运行期抛 `MissingMethodException`。

---

## 版本号规则

三处必须同步：

1. `WMonitor.csproj`：`<Version>` / `<AssemblyVersion>` / `<FileVersion>`
2. 成品文件名：`WMonitor-v0.0N-framework.exe`、`WMonitor-v0.0N-portable.exe`
3. `docs/07-changelog.md` 增加一条

当前：**v0.01**，对应 csproj `0.0.1`。

---

## 发布检查清单

```
[ ] 杀进程 → 编译 0 error 0 warning
[ ] 安装版发布到 D:\WMonitor\WMonitor\
[ ] 两个单文件复制到 D:\WMonitor\，文件名版本号正确
[ ] 从 D:\WMonitor\WMonitor\WMonitor.exe 启动，贴片可见
[ ] 托盘图标出现，右键菜单能打开（SyncMenu 不会崩）
[ ] 点贴片出面板，点面板外收起
[ ] 齿轮 / 双击托盘 → 设置窗口能开，切主题不崩
[ ] PrivateMemorySize64 ≈ 43 MB，CPU 稳态 ≈ 0.3%
[ ] 关闭程序，确认 %APPDATA%\WMonitor\config.json 已写入
```

截图验证（不要用鼠标模拟，会干扰用户）：

```bash
python -c "
from PIL import ImageGrab
im = ImageGrab.grab(all_screens=True)
im.crop((1820,1380,2320,1440)).save(r'D:\Hermes\output\results\check.png')
"
```

---

## 常见编译/运行错误

| 错误 | 原因 | 处理 |
|---|---|---|
| `MSB3027` / `MSB3021` 文件被锁 | 进程还在跑 | `Stop-Process -Name WMonitor -Force` |
| `CS0103 名称不存在` | 批量重命名时字段大小写被改（`_cGpuMem` → `_gpuMem`） | 检查字段声明 |
| `Control does not support transparent background colors` | 控件 `BackColor` 带了 alpha | 用 `Theme.PanelBgOpaque` |
| 编译成功但运行无窗口 | 异常在构造期被吞 | 看 `%APPDATA%\WMonitor\error.log` |
| `dotnet: 未找到 SDK` | 没设 `DOTNET_ROOT` | 用 `D:\Hermes\tools\dotnet-sdk`（Windows 路径形式） |
