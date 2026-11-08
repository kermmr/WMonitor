$ErrorActionPreference = 'Continue'
$p = Get-Process -Id 29812
Write-Output ("Pid={0} Path={1}" -f $p.Id, $p.Path)
Write-Output ("MainWindowTitle='{0}' Responding={1}" -f $p.MainWindowTitle, $p.Responding)
Write-Output ("TotalCPU_s={0} WS_MB={1}" -f [int]$p.TotalProcessorTime.TotalSeconds, [int]($p.WorkingSet64/1MB))

# snapshot twice to find hot threads
$s1 = @{}
foreach ($t in $p.Threads) { $s1[$t.Id] = $t.TotalProcessorTime.TotalMilliseconds }
Start-Sleep -Seconds 3
$delta = @()
foreach ($t in $p.Threads) {
    $old = $s1[$t.Id]
    if ($old -ne $null) {
        $d = $t.TotalProcessorTime.TotalMilliseconds - $old
        $delta += [pscustomobject]@{
            Id = $t.Id
            State = $t.ThreadState
            Wait = $t.WaitReason
            Start = $t.StartTime
            DeltaMs = [math]::Round($d,0)
            Total_s = [math]::Round($t.TotalProcessorTime.TotalSeconds,1)
        }
    }
}
Write-Output "`n=== Threads by CPU delta over 3s ==="
$delta | Sort-Object DeltaMs -Descending | Select-Object -First 15 | Format-Table -AutoSize
Write-Output ("ThreadCount={0}" -f $p.Threads.Count)
