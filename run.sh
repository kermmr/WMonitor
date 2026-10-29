#!/usr/bin/env bash
# 开发迭代：停掉旧实例 → 编译 → 启动 → 截任务栏
set -e

export DOTNET_ROOT='D:\Hermes\tools\dotnet-sdk'
export PATH="/d/Hermes/tools/dotnet-sdk:$PATH"
export WMONITOR_DEBUG=1

PROJ="D:/WMonitor/wmonitor-eidt"
cd "$PROJ"

powershell -NoProfile -Command "Stop-Process -Name WMonitor -Force -ErrorAction SilentlyContinue" >/dev/null 2>&1 || true
sleep 1

if ! dotnet build -c Release > /tmp/wmonitor-build.log 2>&1; then
  tail -25 /tmp/wmonitor-build.log
  echo "BUILD FAILED"
  exit 1
fi
tail -3 /tmp/wmonitor-build.log

powershell -NoProfile -Command "Start-Process 'D:\WMonitor\wmonitor-eidt\bin\Release\net8.0-windows\WMonitor.exe'" >/dev/null 2>&1
sleep 4

python - <<'PY'
from PIL import ImageGrab
im = ImageGrab.grab(all_screens=True)
w, h = im.size
im.crop((max(0, w - 1100), h - 70, w, h)).save(r'D:\Hermes\output\results\wmonitor-strip.png')
print('shot', im.size)
PY
