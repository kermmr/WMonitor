# CI probe runner (ASCII only -- PS1 with Chinese comments needs BOM, see docs/05).
# Runs the headless (non-UI) probe subset and asserts PASS markers in result files.
# Output dir is overridden via WMONITOR_PROBE_OUT so CI does not depend on D:/Hermes.
param()
$ErrorActionPreference = 'Stop'

$out = $env:WMONITOR_PROBE_OUT
if (-not $out) { $out = Join-Path $env:GITHUB_WORKSPACE 'probe-out' }
$dll = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\bin\Release\net8.0-windows\WMonitor.dll'))
$env:WMONITOR_PROBE_OUT = $out
New-Item -ItemType Directory -Force -Path $out

if (-not (Test-Path $dll)) { Write-Host "FAIL probe dll not found: $dll"; exit 1 }

# headless-only probes: no window creation, no desktop dependency
& dotnet $dll --monitors
& dotnet $dll --alert-test           
& dotnet $dll --watchdog-test        
& dotnet $dll --startup-repair-test  
& dotnet $dll --gpu-locator          
& dotnet $dll --dashboard-probe      
& dotnet $dll --dashboard-gpu-probe  
& dotnet $dll --dashboard-action-probe
& dotnet $dll --net-probe
& dotnet $dll --update-check

$failures = New-Object System.Collections.Generic.List[string]
function Assert-Contains([string]$file, [string]$needle, [string]$label) {
  if (-not (Test-Path $file)) { $script:failures.Add("$label : missing result file $file"); return }
  $text = [System.IO.File]::ReadAllText($file)
  if (-not $text.Contains($needle)) { $script:failures.Add("$label : '$needle' not found in $(Split-Path -Leaf $file)") }
}

Assert-Contains (Join-Path $out 'wmonitor-alert-test.txt')        'PASS' 'alert-test'
Assert-Contains (Join-Path $out 'wmonitor-watchdog.txt')           'PASS' 'watchdog-test'
Assert-Contains (Join-Path $out 'wmonitor-startup-repair.txt')     'PASS' 'startup-repair-test'
Assert-Contains (Join-Path $out 'wmonitor-dashboard-probe.txt')    'procs=' 'dashboard-probe'

# monitors output text is localized; existence + non-empty is enough on CI
$monFile = Join-Path $out 'wmonitor-monitors.txt'
if (-not (Test-Path $monFile) -or (Get-Item $monFile).Length -eq 0) { $failures.Add('monitors : missing or empty') }

# update-check: existence only -- the newest-release relation changes with each publish
$updFile = Join-Path $out 'wmonitor-update-check.txt'
if (-not (Test-Path $updFile) -or (Get-Item $updFile).Length -eq 0) { $failures.Add('update-check : missing or empty') }

$actionFile = Join-Path $out 'wmonitor-action-probe.txt'
if (Test-Path $actionFile) {
  $passes = (Select-String -Path $actionFile -Pattern 'PASS').Count
  if ($passes -lt 6) { $failures.Add("action-probe : expected >=6 PASS, got $passes") }
} else { $failures.Add('action-probe : missing result file') }

# net-probe: interface lock + missing-interface fallback each emit PASS
$netFile = Join-Path $out 'wmonitor-net-probe.txt'
if (Test-Path $netFile) {
  $passes = (Select-String -Path $netFile -Pattern 'PASS').Count
  if ($passes -lt 2) { $failures.Add("net-probe : expected >=2 PASS, got $passes") }
} else { $failures.Add('net-probe : missing result file') }

if ($failures.Count -gt 0) {
  foreach ($f in $failures) { Write-Host "PROBE-FAIL $f" }
  exit 1
}
Write-Host 'ALL HEADLESS PROBES PASS'
