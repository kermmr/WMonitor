Add-Type -AssemblyName System.Drawing
$root = 'D:\Hermes\tmp\ico28'

# WMonitor gauge icon from SVG geometry (design/icons/app/*.svg, viewBox 50 50 412 412).
# Square: 52..460 in SVG units -> canvas margin = 2u each side (u = size/412), ~99% fill.
# Gauge ring r=128 (v0.28 final), strokes 37 (faithful) / 44 (small / tiny 59), dot 30/33.

function New-IconBmp([int]$size, [string]$variant) {
    $u = $size / 412.0
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    # container rounded square (2u .. 410u), rx 92
    $rx = 92 * $u
    $sq = New-Object System.Drawing.RectangleF((2 * $u), (2 * $u), (408 * $u), (408 * $u))
    $sqPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $rx * 2
    $sqPath.AddArc($sq.X, $sq.Y, $d, $d, 180, 90)
    $sqPath.AddArc($sq.Right - $d, $sq.Y, $d, $d, 270, 90)
    $sqPath.AddArc($sq.Right - $d, $sq.Bottom - $d, $d, $d, 0, 90)
    $sqPath.AddArc($sq.X, $sq.Bottom - $d, $d, $d, 90, 90)
    $sqPath.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        [System.Drawing.PointF]::new(0, (2 * $u)), [System.Drawing.PointF]::new(0, (410 * $u)),
        [System.Drawing.Color]::FromArgb(255, 0x24, 0x29, 0x31), [System.Drawing.Color]::FromArgb(255, 0x13, 0x16, 0x1B))
    $g.FillPath($brush, $sqPath)
    $brush.Dispose()

    if ($variant -eq 'faithful') {
        # inner stroke rgba(255,255,255,.08) 2.5 units
        $penW = 2.5 * $u
        $inset = $penW / 2
        $strokePath = New-Object System.Drawing.Drawing2D.GraphicsPath
        $s2 = $sq; $s2.Inflate(-$inset, -$inset)
        $d2 = ($rx - $inset) * 2
        $strokePath.AddArc($s2.X, $s2.Y, $d2, $d2, 180, 90)
        $strokePath.AddArc($s2.Right - $d2, $s2.Y, $d2, $d2, 270, 90)
        $strokePath.AddArc($s2.Right - $d2, $s2.Bottom - $d2, $d2, $d2, 0, 90)
        $strokePath.AddArc($s2.X, $s2.Bottom - $d2, $d2, $d2, 90, 90)
        $strokePath.CloseFigure()
        $sp = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(20, 255, 255, 255), $penW)
        $g.DrawPath($sp, $strokePath)
        $sp.Dispose(); $strokePath.Dispose()

        # gloss: white .08 -> 0 over top 220/408, clipped to rounded rect
        $g.SetClip($sqPath)
        $glossH = 220 * $u
        $gb = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            [System.Drawing.PointF]::new(0, (2 * $u)), [System.Drawing.PointF]::new(0, ((2 * $u) + $glossH)),
            [System.Drawing.Color]::FromArgb(20, 255, 255, 255), [System.Drawing.Color]::FromArgb(0, 255, 255, 255))
        $g.FillRectangle($gb, $sq.X, $sq.Y, $sq.Width, $glossH)
        $gb.Dispose()
        $g.ResetClip()
    }

    # glyph gradient (per-element bbox, SVG objectBoundingBox)
    $arcL = 78 * $u; $arcT = 88 * $u; $arcD = 256 * $u   # arc bbox: center (256,266) r128, canvas offset 50

    if ($variant -ne 'tiny') {
        # track rgba(255,255,255,.13): 135 deg, sweep 270
        $tw = 44 * $u
        if ($variant -eq 'faithful') { $tw = 37 * $u }
        $tp = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(33, 255, 255, 255), $tw)
        $tp.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $tp.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $g.DrawArc($tp, $arcL, $arcT, $arcD, $arcD, 135, 270)
        $tp.Dispose()
    }

    # progress arc: 135 deg, sweep 183.6 (68% of 270)
    $pw = 44 * $u
    if ($variant -eq 'faithful') { $pw = 37 * $u }
    if ($variant -eq 'tiny') { $pw = 59 * $u }
    $pg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        [System.Drawing.PointF]::new(0, $arcT), [System.Drawing.PointF]::new(0, ($arcT + $arcD)),
        [System.Drawing.Color]::FromArgb(255, 0x4C, 0xC2, 0xFF), [System.Drawing.Color]::FromArgb(255, 0x0A, 0x84, 0xFF))
    $pp = New-Object System.Drawing.Pen($pg, $pw)
    $pp.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pp.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawArc($pp, $arcL, $arcT, $arcD, $arcD, 135, 183.6)
    $pp.Dispose(); $pg.Dispose()

    if ($variant -ne 'tiny') {
        # needle (256,266)->(305.9,222.1), #4CC2FF, 25 units (32 small)
        $nw = 32 * $u
        if ($variant -eq 'faithful') { $nw = 25 * $u }
        $np = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 0x4C, 0xC2, 0xFF), $nw)
        $np.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $np.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $g.DrawLine($np, (206 * $u), (216 * $u), (255.9 * $u), (172.1 * $u))
        $np.Dispose()
    }

    # center dot r30 (33 small), glyph gradient over its own bbox
    $dotR = 30 * $u
    if ($variant -eq 'small') { $dotR = 33 * $u }
    $dg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        [System.Drawing.PointF]::new(0, ((216 * $u) - $dotR)), [System.Drawing.PointF]::new(0, ((216 * $u) + $dotR)),
        [System.Drawing.Color]::FromArgb(255, 0x4C, 0xC2, 0xFF), [System.Drawing.Color]::FromArgb(255, 0x0A, 0x84, 0xFF))
    $g.FillEllipse($dg, ((206 * $u) - $dotR), ((216 * $u) - $dotR), (2 * $dotR), (2 * $dotR))
    $dg.Dispose()

    $g.Dispose()
    return $bmp
}

$jobs = @(
    @{ v = 'faithful'; sizes = @(256, 128, 64, 48); tag = 'g-f' },
    @{ v = 'small';    sizes = @(32, 24);           tag = 'g-s' },
    @{ v = 'tiny';     sizes = @(16);               tag = 'g-t' },
    @{ v = 'faithful'; sizes = @(256, 128, 64, 48); tag = 'gl-f' },
    @{ v = 'small';    sizes = @(32, 24);           tag = 'gl-s' },
    @{ v = 'tiny';     sizes = @(16);               tag = 'gl-t' }
)
foreach ($j in $jobs) {
    foreach ($sz in $j.sizes) {
        $bmp = New-IconBmp $sz $j.v
        $out = Join-Path $root ("{0}-{1}.png" -f $j.tag, $sz)
        $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        Write-Host ("ok {0}-{1}" -f $j.tag, $sz)
    }
}
