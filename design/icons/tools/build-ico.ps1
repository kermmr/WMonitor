Add-Type -AssemblyName System.Drawing

# Assemble a Windows ICO: 256 frame stored as PNG, smaller frames as 32bpp BMP (XOR bottom-up BGRA + zero AND mask, DWORD-aligned).
# usage: build-ico.ps1 <out.ico> <png256> <png128> <png64> <png48> <png32> <png24> <png16>
$out = $args[0]
$pngs = $args[1..7]

function Get-BgraBottomUp([string]$path) {
    $bmp = [System.Drawing.Bitmap]::new($path)
    if ($bmp.PixelFormat -ne [System.Drawing.Imaging.PixelFormat]::Format32bppArgb) {
        $old = $bmp
        $bmp = $bmp.Clone([System.Drawing.Rectangle]::new(0, 0, $bmp.Width, $bmp.Height), [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $old.Dispose()
    }
    $w = $bmp.Width; $h = $bmp.Height
    $rect = [System.Drawing.Rectangle]::new(0, 0, $w, $h)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $row = New-Object byte[] ($w * 4)
    $xor = New-Object byte[] ($w * 4 * $h)
    for ($y = 0; $y -lt $h; $y++) {
        [System.Runtime.InteropServices.Marshal]::Copy([IntPtr]::Add($data.Scan0, $y * $stride), $row, 0, $w * 4)
        [Array]::Copy($row, 0, $xor, ($h - 1 - $y) * $w * 4, $w * 4)
    }
    $bmp.UnlockBits($data)
    $bmp.Dispose()
    return ,@($xor, $w, $h)
}

$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]7)

$entries = @()
foreach ($p in $pngs) {
    $bmp = [System.Drawing.Bitmap]::new($p)
    $sz = $bmp.Width
    $bmp.Dispose()
    if ($sz -eq 256) {
        $raw = [System.IO.File]::ReadAllBytes($p)
        $entries += ,@($sz, $sz, $raw, $true)
    } else {
        $pair = Get-BgraBottomUp $p
        $xor = $pair[0]; $w = $pair[1]; $h = $pair[2]
        $maskStride = [Math]::Ceiling($w / 32.0) * 4
        $mask = New-Object byte[] ($maskStride * $h)
        # real AND mask from alpha (bit=1 -> transparent), like ImageMagick writes; plus biSizeImage = XOR length
        for ($y = 0; $y -lt $h; $y++) {
            $maskRow = ($h - 1 - $y) * $maskStride      # mask is top-down
            for ($x = 0; $x -lt $w; $x++) {
                $a = $xor[($y * $w + $x) * 4 + 3]
                if ($a -le 8) { $mask[$maskRow + ($x -shr 3)] = $mask[$maskRow + ($x -shr 3)] -bor (0x80 -shr ($x -band 7)) }
            }
        }
        $bih = New-Object byte[] 40
        $tmp4 = [BitConverter]::GetBytes([uint32]40);        [Array]::Copy($tmp4, 0, $bih, 0, 4)
        $tmp4 = [BitConverter]::GetBytes([uint32]$w);        [Array]::Copy($tmp4, 0, $bih, 4, 4)
        $tmp4 = [BitConverter]::GetBytes([uint32]($h * 2));  [Array]::Copy($tmp4, 0, $bih, 8, 4)
        $tmp2 = [BitConverter]::GetBytes([uint16]1);         [Array]::Copy($tmp2, 0, $bih, 12, 2)
        $tmp2 = [BitConverter]::GetBytes([uint16]32);        [Array]::Copy($tmp2, 0, $bih, 14, 2)
        $tmp4 = [BitConverter]::GetBytes([uint32]$xor.Length); [Array]::Copy($tmp4, 0, $bih, 20, 4)
        $bmpData = New-Object byte[] (40 + $xor.Length + $mask.Length)
        [Array]::Copy($bih, 0, $bmpData, 0, 40)
        [Array]::Copy($xor, 0, $bmpData, 40, $xor.Length)
        [Array]::Copy($mask, 0, $bmpData, 40 + $xor.Length, $mask.Length)
        $entries += ,@($sz, $sz, $bmpData, $false)
    }
}

$offset = 6 + 16 * 7
foreach ($e in $entries) {
    $wByte = if ($e[0] -ge 256) { 0 } else { $e[0] }
    $bw.Write([byte]$wByte); $bw.Write([byte]$wByte); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$e[2].Length); $bw.Write([uint32]$offset)
    $offset += $e[2].Length
}
foreach ($e in $entries) { $bw.Write($e[2]) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($out, $ms.ToArray())
$bw.Dispose(); $ms.Dispose()
Write-Host ("wrote {0} ({1} bytes)" -f $out, (Get-Item $out).Length)
