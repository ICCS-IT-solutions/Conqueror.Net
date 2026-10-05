<#
.SYNOPSIS
    Compares the per-method downscale outputs produced by
    render-icon-downscale-test.ps1, so the choice is made on measured pixel
    differences rather than on eyeballing three near-identical contact sheets.

.DESCRIPTION
    Emits, per size:
      * mean / max RGBA difference and % differing pixels between each pair
      * a "sigma" sharpness figure (laplacian variance on the composited
        luminance) - a blobby, over-averaged result has visibly lower sigma
      * a 3x zoomed A|B|C strip for the icons that stress thin detail

    Output: artifacts/icon-downscale-test/comparison-<size>.png
#>
[CmdletBinding()]
param(
    [string]$Root = (Join-Path $PSScriptRoot '..\artifacts\icon-downscale-test'),
    [int[]]$Sizes = @(16, 24, 32, 48)
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$methods = @('naive', 'box', 'halve')

# Icons whose source artwork carries the thinnest strokes: drive lettering,
# stand edges, the CD highlight. These are where the methods diverge most.
$Detail = @('CD-ROM', 'DVD', 'Optical Drive', 'Network Drive', 'My Computer', 'Recent Documents')

# Compare on an opaque white backdrop - alpha edge handling is part of what
# is being judged, and premultiplied vs straight alpha would otherwise skew it.
function Get-Pixels {
    param([string]$Path)
    $bmp = [System.Drawing.Bitmap]::new($Path)
    $w = $bmp.Width; $h = $bmp.Height
    $rect = [System.Drawing.Rectangle]::new(0, 0, $w, $h)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $stride = $data.Stride
        $buf = [byte[]]::new($stride * $h)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $buf, 0, $buf.Length)
    } finally { $bmp.UnlockBits($data) }
    $bmp.Dispose()
    $px = [int[]]::new($w * $h * 4)
    for ($y = 0; $y -lt $h; $y++) {
        for ($x = 0; $x -lt $w; $x++) {
            $s = $y * $stride + $x * 4
            $d = ($y * $w + $x) * 4
            $a = $buf[$s + 3]
            $px[$d + 0] = [int](255 + ($buf[$s + 2] - 255) * $a / 255)  # R over white
            $px[$d + 1] = [int](255 + ($buf[$s + 1] - 255) * $a / 255)  # G
            $px[$d + 2] = [int](255 + ($buf[$s + 0] - 255) * $a / 255)  # B
            $px[$d + 3] = 255
        }
    }
    return , $px
}

function Get-Sharpness {
    param([int[]]$Px, [int]$W, [int]$H)
    $sum = 0.0; $sumSq = 0.0; $n = 0
    for ($y = 1; $y -lt $H - 1; $y++) {
        for ($x = 1; $x -lt $W - 1; $x++) {
            $i = ($y * $W + $x) * 4
            $c = ($Px[$i] + $Px[$i + 1] + $Px[$i + 2]) / 3.0
            $l = ($Px[$i - 4] + $Px[$i - 3] + $Px[$i - 2]) / 3.0
            $r = ($Px[$i + 4] + $Px[$i + 5] + $Px[$i + 6]) / 3.0
            $u = ($Px[$i - $W * 4] + $Px[$i - $W * 4 + 1] + $Px[$i - $W * 4 + 2]) / 3.0
            $d = ($Px[$i + $W * 4] + $Px[$i + $W * 4 + 1] + $Px[$i + $W * 4 + 2]) / 3.0
            $lap = 4 * $c - $l - $r - $u - $d
            $sum += $lap; $sumSq += $lap * $lap; $n++
        }
    }
    if ($n -eq 0) { return 0.0 }
    $mean = $sum / $n
    return ($sumSq / $n) - ($mean * $mean)
}

$names = @()
Get-ChildItem (Join-Path $Root $methods[0]) -Recurse -Filter '*.png' |
    ForEach-Object { $names += $_.BaseName }
$names = $names | Sort-Object -Unique

foreach ($s in $Sizes) {
    Write-Host ''
    Write-Host "=== ${s}px ==="
    $cache = @{}
    foreach ($m in $methods) {
        $cache[$m] = @{}
        foreach ($n in $names) {
            $p = Join-Path $Root "$m\$s\$n.png"
            if (Test-Path $p) { $cache[$m][$n] = Get-Pixels $p }
        }
    }

    # sharpness, averaged over all icons
    $sharp = @{}
    foreach ($m in $methods) {
        $acc = 0.0; $cnt = 0
        foreach ($n in $cache[$m].Keys) {
            $acc += Get-Sharpness -Px $cache[$m][$n] -W $s -H $s
            $cnt++
        }
        $sharp[$m] = $acc / [math]::Max($cnt, 1)
    }
    Write-Host ('  sharpness (laplacian var, higher = more edge detail): ' +
        (($methods | ForEach-Object { "$_=$([math]::Round($sharp[$_],1))" }) -join '  '))

    # pairwise differences
    for ($i = 0; $i -lt $methods.Count; $i++) {
        for ($j = $i + 1; $j -lt $methods.Count; $j++) {
            $a = $methods[$i]; $b = $methods[$j]
            $sum = 0.0; $max = 0; $diffPx = 0; $cnt = 0
            foreach ($n in $names) {
                if (-not $cache[$a].ContainsKey($n) -or -not $cache[$b].ContainsKey($n)) { continue }
                $pa = $cache[$a][$n]; $pb = $cache[$b][$n]
                $worst = 0
                for ($k = 0; $k -lt $pa.Length; $k += 4) {
                    $d = [math]::Abs($pa[$k] - $pb[$k]) + [math]::Abs($pa[$k + 1] - $pb[$k + 1]) + [math]::Abs($pa[$k + 2] - $pb[$k + 2])
                    $sum += $d; $cnt++
                    if ($d -gt 0) { $diffPx++ }
                    if ($d -gt $worst) { $worst = $d }
                }
                if ($worst -gt $max) { $max = $worst }
            }
            $mean = $sum / $cnt
            $pct = 100.0 * $diffPx / $cnt
            Write-Host ('  {0,-6} vs {1,-6} : mean delta {2,5:N2}/765   max {3,3}   pixels differing {4,5:N1}%' -f $a, $b, $mean, $max, $pct)
        }
    }
}


# ------------------------------------------------- A|B|C visual comparison
# Zoom factor chosen so a 16px icon is large enough to actually judge.
$labels = @{ naive = 'A naive'; box = 'B box'; halve = 'C halve' }

foreach ($s in $Sizes) {
$z = switch ($s) { 16 { 6 } 24 { 5 } 32 { 4 } default { 3 } }

$cell = $s * $z
$labelW = 190; $headerH = 44; $pad = 10
$rowH = $cell + $pad
$sheetW = $labelW + ($methods.Count * $cell) + (($methods.Count + 1) * $pad)
$sheetH = $headerH + ($Detail.Count * $rowH) + $pad

$fnt = [System.Drawing.Font]::new('Segoe UI', 10)
$fntB = [System.Drawing.Font]::new('Segoe UI', 10, [System.Drawing.FontStyle]::Bold)
$brT = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 20, 20, 20))
$brM = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 110, 110, 110))
$brL = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 195, 195, 195))
$brB = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 238, 243, 250))

$sheet = [System.Drawing.Bitmap]::new($sheetW, $sheetH, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($sheet)
$g.Clear([System.Drawing.Color]::White)
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half

$g.DrawString("${s}px - nearest-neighbour x$z", $fntB, $brT, 10, 8)
$g.DrawString('same source pixel data, three resamplers - judge stroke loss, ringing and muddiness', $fnt, $brM, 10, 26)

$x = $labelW
foreach ($m in $methods) { $g.DrawString($labels[$m], $fntB, $brT, $x, 26); $x += $cell + $pad }

$row = 0
foreach ($n in $Detail) {
    $y = $headerH + ($row * $rowH)
    if ($row % 2 -eq 0) { $g.FillRectangle($brB, 0, $y, $sheetW, $rowH) }
    $g.DrawString($n, $fnt, $brT, 8, $y + ($rowH / 2) - 7)

    $x = $labelW
    foreach ($m in $methods) {
        $p = Join-Path $Root "$m\$s\$n.png"
        if (Test-Path $p) {
            $img = [System.Drawing.Bitmap]::new($p)
            $g.DrawImage($img, $x, $y + $pad / 2, $cell, $cell)
            $img.Dispose()
        }
        $g.DrawRectangle($brL, $x, $y + $pad / 2, $cell, $cell)
        $x += $cell + $pad
    }
    $row++
}
$g.Dispose()
$out = Join-Path $Root "comparison-${s}px.png"
$sheet.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$sheet.Dispose()
$fnt.Dispose(); $fntB.Dispose(); $brT.Dispose(); $brM.Dispose(); $brL.Dispose(); $brB.Dispose()
Write-Host "  -> $out"
}
