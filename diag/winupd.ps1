Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class WU {
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    public delegate bool EnumProc(IntPtr h, IntPtr lp);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool GetUpdateRect(IntPtr h, out RECT r, bool erase);
    [DllImport("user32.dll")] public static extern int GetUpdateRgn(IntPtr h, IntPtr rgn, bool erase);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateRectRgn(int a,int b,int c,int d);
    [DllImport("gdi32.dll")] public static extern int GetRegionData(IntPtr rgn, uint size, out RGNDATA data);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
    [StructLayout(LayoutKind.Sequential)]
    public struct RGNDATAHEADER { public int dwSize, iType, nCount, nRgnSize; public RECT rcBound; }
    [StructLayout(LayoutKind.Sequential)]
    public struct RGNDATA { public RGNDATAHEADER rdh; }
}
"@
$found = New-Object System.Collections.ArrayList
$cb = [WU+EnumProc]{ param($h, $lp)
    [uint32]$pid2 = 0
    [WU]::GetWindowThreadProcessId($h, [ref]$pid2) | Out-Null
    if ($pid2 -eq 29812) {
        $r = New-Object WU+RECT
        [WU]::GetWindowRect($h, [ref]$r) | Out-Null
        $ur = New-Object WU+RECT
        $hasUpd = [WU]::GetUpdateRect($h, [ref]$ur, $false)
        $cn = New-Object System.Text.StringBuilder 256
        [WU]::GetClassName($h, $cn, 256) | Out-Null
        $tt = New-Object System.Text.StringBuilder 256
        [WU]::GetWindowText($h, $tt, 256) | Out-Null
        $script:found.Add([pscustomobject]@{
            Hwnd = "0x" + $h.ToString("X")
            Class = $cn.ToString()
            Title = $tt.ToString()
            Vis = [WU]::IsWindowVisible($h)
            Dirty = $hasUpd
            DirtyRect = if ($hasUpd) { "$($ur.L),$($ur.T) $($ur.R-$($ur.L))x$($ur.B-$($ur.T))" } else { "" }
            Rect = "$($r.R-$r.L)x$($r.B-$r.T)"
        }) | Out-Null
    }
    return $true
}
[WU]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
$found | Format-Table -AutoSize
