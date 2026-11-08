$ErrorActionPreference = 'Stop'
$p = Get-Process WMonitor
Write-Output ("Pid={0}" -f $p.Id)

# ---- CPU baseline over 6s ----
$c1 = $p.TotalProcessorTime.TotalMilliseconds
Start-Sleep -Seconds 6
$p.Refresh()
$c2 = $p.TotalProcessorTime.TotalMilliseconds
$pct = ($c2 - $c1) / 6000.0 / [Environment]::ProcessorCount * 100
Write-Output ("CPU steady: {0:N2}% of total ({1:N2}% of one core)" -f $pct, (($c2-$c1)/6000.0*100))

Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class WV {
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    public delegate bool EnumProc(IntPtr h, IntPtr lp);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetUpdateRect(IntPtr h, out RECT r, bool erase);
    [DllImport("user32.dll")] public static extern bool InvalidateRect(IntPtr h, IntPtr rect, bool erase);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
}
"@

# ---- find strip window (visible small layered window of this process) ----
$strip = [IntPtr]::Zero
$cb = [WV+EnumProc]{ param($h, $lp)
    [uint32]$wpid = 0
    [WV]::GetWindowThreadProcessId($h, [ref]$wpid) | Out-Null
    if ($wpid -eq $p.Id -and [WV]::IsWindowVisible($h)) {
        $r = New-Object WV+RECT
        [WV]::GetWindowRect($h, [ref]$r) | Out-Null
        $hgt = $r.B - $r.T; $wd = $r.R - $r.L
        # strip: wide and short (h 28..70)
        if ($hgt -ge 20 -and $hgt -le 70 -and $wd -ge 80) { $script:strip = $h }
    }
    return $true
}
[WV]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
if ($strip -eq [IntPtr]::Zero) { Write-Output "strip window NOT FOUND"; exit 1 }
$r2 = New-Object WV+RECT
[WV]::GetWindowRect($strip, [ref]$r2) | Out-Null
Write-Output ("Strip hwnd=0x{0:X} size={1}x{2}" -f $strip.ToInt64(), ($r2.R-$r2.L), ($r2.B-$r2.T))

# ---- dirty check before ----
$u = New-Object WV+RECT
$dirty0 = [WV]::GetUpdateRect($strip, [ref]$u, $false)
Write-Output ("Dirty before force: {0}" -f $dirty0)

# ---- force-invalidate = simulate TDR/DWM dirty event (the v0.28 storm trigger) ----
[WV]::InvalidateRect($strip, [IntPtr]::Zero, $true) | Out-Null
Start-Sleep -Milliseconds 300
$dirty1 = [WV]::GetUpdateRect($strip, [ref]$u, $false)
Write-Output ("Dirty 300ms after InvalidateRect: {0}" -f $dirty1)

# ---- watch CPU for 5s after the forced dirty (old build would burn ~100% of one core here) ----
$p.Refresh(); $c3 = $p.TotalProcessorTime.TotalMilliseconds
Start-Sleep -Seconds 5
$p.Refresh(); $c4 = $p.TotalProcessorTime.TotalMilliseconds
Write-Output ("CPU after forced dirty: {0:N2}% of one core" -f (($c4-$c3)/5000.0*100))
$dirty2 = [WV]::GetUpdateRect($strip, [ref]$u, $false)
Write-Output ("Dirty after 5s: {0}" -f $dirty2)
if (-not $dirty1 -and -not $dirty2 -and (($c4-$c3)/5000.0) -lt 0.2) { Write-Output "VERIFY-PASS" } else { Write-Output "VERIFY-FAIL" }
