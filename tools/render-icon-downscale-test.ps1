<#
.SYNOPSIS
    Renders Marchmountain Windows XP PNG icons downscaled to the sizes the
    Conqueror.Net Luna UI actually displays, so downscaling quality can be
    judged visually before any import pipeline is built.

.DESCRIPTION
    Compares three resampling strategies at 16/24/32/48 px:

      A  Naive bicubic   - one single HighQualityBicubic pass from 1024px.
                           The common default; expected to alias at 32x.
      B  Direct box      - one area-average (box filter) pass to the target.
                           Mathematically correct average for box downsampling.
      C  Halve + bicubic - progressive halving down to the nearest power of two
                           above the target, then a final bicubic pass.

    Each icon is drawn twice per size: once at 1:1 (honest real-world look) and
    once zoomed with NearestNeighbor, so the actual shipped pixels are visible -
    a 16px icon is otherwise unjudgeable on a modern display.

    Output:
      artifacts/icon-downscale-test/<method>/<size>/<icon>.png
      artifacts/icon-downscale-test/contact-sheet-<method>.png
      a size/byte summary on stdout

.NOTES
    Read-only with respect to the icon source. Writes only under $OutputDir.
#>
[CmdletBinding()]
param(
    [string]$SourceDir = (Join-Path $PSScriptRoot '..\Icons_source\marchmountain-winxp-icons-png\Windows XP Icons'),
    [string]$OutputDir = (Join-Path $PSScriptRoot '..\artifacts\icon-downscale-test'),
    [int[]]$Sizes = @(16, 24, 32, 48)
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not (Test-Path $SourceDir)) {
    throw "Icon source not found: $SourceDir"
}
$SourceDir = (Resolve-Path $SourceDir).Path

# Icons chosen to cover what FileIconResolver / MimeTypeResolver actually need.
$Icons = @(
    'Folder Closed', 'Folder Opened', 'New Folder', 'Folder Options'
    'My Computer', 'My Documents', 'My Pictures', 'My Music', 'My Videos'
    'IE Home', 'Recent Documents', 'My Network Places'
    'Floppy Disk', 'Optical Drive', 'CD-ROM', 'DVD', 'ZIP Drive', 'Network Drive'
    'Recycle Bin (empty)', 'Recycle Bin (full)'
    'Generic Document', 'Generic Text Document', 'Notepad', 'XPS document'
    'Zip folder', 'Generic Video', 'WMP Library'
)

# Zoom factor per size - keeps the contact sheet a readable width.
$Zoom = @{ 16 = 4; 24 = 3; 32 = 3; 48 = 2 }

# ---------------------------------------------------------------- C# helpers
# PowerShell interpreted loops over ~1M pixels are far too slow for a 32x
# reduction, so the resamplers are compiled instead.
# System.Drawing is split across several assemblies in .NET (Common, Primitives,
# GdiPlus), each forward-declaring types the others use, so reference all of them.
$drawingRefs = @(
    [AppDomain]::CurrentDomain.GetAssemblies() |
        Where-Object { $_.Location -and ($_.GetName().Name -like 'System.Drawing*' -or $_.GetName().Name -like 'System.Private.Windows*') } |
        Select-Object -ExpandProperty Location -Unique
)
if (-not $drawingRefs) { throw 'Could not locate System.Drawing assemblies' }

Add-Type -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class IconResample
{
    // One area-average (box filter) pass to an arbitrary target size.
    // Alpha-weighted so transparent pixels do not bleed dark fringes inward.
    public static Bitmap BoxResize(Bitmap src, int dw, int dh)
    {
        Bitmap dst = new Bitmap(dw, dh, PixelFormat.Format32bppArgb);
        BitmapData sd = src.LockBits(new Rectangle(0, 0, src.Width, src.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        BitmapData dd = dst.LockBits(new Rectangle(0, 0, dw, dh),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int ss = sd.Stride, ds = dd.Stride;
            byte[] s = new byte[ss * src.Height];
            byte[] o = new byte[ds * dh];
            Marshal.Copy(sd.Scan0, s, 0, s.Length);

            for (int y = 0; y < dh; y++)
            {
                int y0 = (int)((long)y * src.Height / dh);
                int y1 = (int)((long)(y + 1) * src.Height / dh);
                if (y1 <= y0) y1 = y0 + 1;
                for (int x = 0; x < dw; x++)
                {
                    int x0 = (int)((long)x * src.Width / dw);
                    int x1 = (int)((long)(x + 1) * src.Width / dw);
                    if (x1 <= x0) x1 = x0 + 1;

                    long a = 0, r = 0, g = 0, b = 0, n = 0;
                    for (int yy = y0; yy < y1; yy++)
                    {
                        int row = yy * ss;
                        for (int xx = x0; xx < x1; xx++)
                        {
                            int i = row + xx * 4;   // 32bpp BGRA
                            int pa = s[i + 3];
                            a += pa; r += s[i + 2] * (long)pa;
                            g += s[i + 1] * (long)pa; b += s[i] * (long)pa;
                            n++;
                        }
                    }
                    int j = y * ds + x * 4;
                    if (a > 0)
                    {
                        o[j + 0] = (byte)(b / a);
                        o[j + 1] = (byte)(g / a);
                        o[j + 2] = (byte)(r / a);
                        o[j + 3] = (byte)(a / n);
                    }
                }
            }
            Marshal.Copy(o, 0, dd.Scan0, o.Length);
        }
        finally { src.UnlockBits(sd); dst.UnlockBits(dd); }
        return dst;
    }

    // 2x2 area average, premultiplied to stay correct on alpha edges.
    public static Bitmap BoxHalve(Bitmap src)
    {
        int w = src.Width / 2, h = src.Height / 2;
        if (w < 1 || h < 1) return null;
        Bitmap dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        BitmapData sd = src.LockBits(new Rectangle(0, 0, src.Width, src.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        BitmapData dd = dst.LockBits(new Rectangle(0, 0, w, h),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int ss = sd.Stride, ds = dd.Stride;
            byte[] s = new byte[ss * src.Height];
            byte[] o = new byte[ds * h];
            Marshal.Copy(sd.Scan0, s, 0, s.Length);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int a = 0, r = 0, g = 0, b = 0;
                    for (int dy = 0; dy < 2; dy++)
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int i = (y * 2 + dy) * ss + (x * 2 + dx) * 4;
                            int pa = s[i + 3];
                            a += pa; r += s[i + 2] * pa; g += s[i + 1] * pa; b += s[i] * pa;
                        }
                    int j = y * ds + x * 4;
                    if (a > 0)
                    {
                        o[j + 0] = (byte)(b / a); o[j + 1] = (byte)(g / a);
                        o[j + 2] = (byte)(r / a); o[j + 3] = (byte)(a / 4);
                    }
                }
            }
            Marshal.Copy(o, 0, dd.Scan0, o.Length);
        }
        finally { src.UnlockBits(sd); dst.UnlockBits(dd); }
        return dst;
    }

    // GDI+ high-quality bicubic. CompositingMode.SourceCopy matters: without it
    // alpha is blended against an uninitialised buffer and edges go muddy.
    public static Bitmap BicubicTo(Bitmap src, int w, int h)
    {
        Bitmap dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(dst))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(src, new Rectangle(0, 0, w, h),
                0, 0, src.Width, src.Height, GraphicsUnit.Pixel);
        }
        return dst;
    }
}
'@ -ReferencedAssemblies $drawingRefs
# ----------------------------------------------------------------- rendering
function New-Checkerboard {
    param([int]$W, [int]$H, [int]$Cell = 8)
    if ($W -lt 1 -or $H -lt 1) { throw "New-Checkerboard got bad size W=$W H=$H" }
    $bmp = [System.Drawing.Bitmap]::new($W, $H, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb(255, 255, 255, 255))
    $light = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 222, 222, 222))
    $dark = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 170, 170, 170))
    for ($y = 0; $y -lt $H; $y += $Cell) {
        for ($x = 0; $x -lt $W; $x += $Cell) {
            $on = (([int]($x / $Cell) + [int]($y / $Cell)) % 2) -eq 0
            $g.FillRectangle($(if ($on) { $light } else { $dark }), $x, $y, $Cell, $Cell)
        }
    }
    $g.Dispose(); $light.Dispose(); $dark.Dispose()
    return $bmp
}

function Copy-Nearest {
    param([System.Drawing.Bitmap]$Src, [int]$Factor)
    $w = $Src.Width * $Factor; $h = $Src.Height * $Factor
    $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
    $g.DrawImage($Src, 0, 0, $w, $h)
    $g.Dispose()
    return $bmp
}

function Get-Downscaled {
    param([System.Drawing.Bitmap]$Source, [string]$Method, [int]$Size)
    switch ($Method) {
        'naive' { return [IconResample]::BicubicTo($Source, $Size, $Size) }
        'box' { return [IconResample]::BoxResize($Source, $Size, $Size) }
        'halve' {
            $bmp = $Source
            while (($bmp.Width / 2) -ge $Size) { $bmp = [IconResample]::BoxHalve($bmp) }
            if ($bmp.Width -eq $Size) { return $bmp }
            $out = [IconResample]::BicubicTo($bmp, $Size, $Size)
            $bmp.Dispose()
            return $out
        }
    }
    throw "Unknown method: $Method"
}

$methods = @(
    @{ Key = 'naive'; Label = 'A. Naive bicubic (single pass)' }
    @{ Key = 'box'; Label = 'B. Direct box filter (area average)' }
    @{ Key = 'halve'; Label = 'C. Progressive halving + bicubic' }
)

$font = New-Object System.Drawing.Font('Segoe UI', 9)
$fontBold = New-Object System.Drawing.Font('Segoe UI', 9, [System.Drawing.FontStyle]::Bold)
$brushText = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 20, 20, 20))
$brushMuted = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 110, 110, 110))
$brushLine = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 190, 190, 190))
$brushBand = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 238, 243, 250))

$labelW = 250
$headerH = 62
$pad = 8
$cellOf = { param($s) $Zoom[$s] * $s }
$rowH = ($Sizes | ForEach-Object { & $cellOf $_ } | Measure-Object -Maximum).Maximum + $pad
$sheetW = $labelW + (($Sizes | ForEach-Object { 2 * (& $cellOf $_) + $pad } | Measure-Object -Sum).Sum) + $pad
$sheetH = $headerH + ($Icons.Count * $rowH) + $pad

$summary = @()

foreach ($m in $methods) {
    Write-Host "Rendering $($m.Key) ..."
    $methodDir = Join-Path $OutputDir $m.Key
    foreach ($s in $Sizes) { New-Item -ItemType Directory -Force -Path (Join-Path $methodDir $s) | Out-Null }

    $sheet = New-Object System.Drawing.Bitmap($sheetW, $sheetH, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($sheet)
    $g.Clear([System.Drawing.Color]::White)
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit

    $g.DrawString($m.Label, $fontBold, $brushText, 10, 10)
    $g.DrawString('icons at 1:1 (real size) and nearest-neighbour zoom (actual shipped pixels)  |  source 1024x1024 PNG', $font, $brushMuted, 10, 30)

    $x = $labelW
    foreach ($s in $Sizes) {
        $c = & $cellOf $s
        $g.DrawString("$s px", $fontBold, $brushText, $x + 2, 46)
        $g.DrawString('1:1', $font, $brushMuted, $x + 2, 46)
        $g.DrawString("$($Zoom[$s])x zoom", $font, $brushMuted, $x + $c + $pad + 2, 46)
        $x += 2 * $c + $pad
    }

    $row = 0
    foreach ($name in $Icons) {
        $srcPath = Join-Path $SourceDir "$name.png"
        if (-not (Test-Path $srcPath)) { Write-Warn "missing: $name.png"; $row++; continue }
        $src = New-Object System.Drawing.Bitmap($srcPath)

        $y = $headerH + ($row * $rowH)
        if ($row % 2 -eq 0) { $g.FillRectangle($brushBand, 0, $y, $sheetW, $rowH) }
        $g.DrawString($name, $font, $brushText, 8, $y + ($rowH / 2) - 8)

        $x = $labelW
        foreach ($s in $Sizes) {
            $small = Get-Downscaled -Source $src -Method $m.Key -Size $s
            $smallPath = Join-Path $methodDir "$s\$name.png"
            $small.Save($smallPath, [System.Drawing.Imaging.ImageFormat]::Png)

            $c = & $cellOf $s
            $cb = New-Checkerboard -W $c -H $c
            $g.DrawImage($cb, $x, $y + $pad / 2)
            $ox = $x + [int](($c - $s) / 2); $oy = $y + $pad / 2 + [int](($c - $s) / 2)
            $g.DrawImage($small, $ox, $oy, $s, $s)
            # NB: must not be named $zoom - PowerShell variables are
            # case-insensitive, so $zoom would clobber the $Zoom hashtable.
            $zoomBmp = Copy-Nearest -Src $small -Factor $Zoom[$s]
            $g.DrawImage($zoomBmp, $x + $c + $pad, $y + $pad / 2)
            $g.DrawRectangle($brushLine, $x, $y + $pad / 2, $c, $c)
            $g.DrawRectangle($brushLine, $x + $c + $pad, $y + $pad / 2, $c, $c)

            if ($m.Key -eq 'naive') {
                $summary += [pscustomobject]@{
                    Icon = $name; Size = $s; PngBytes = (Get-Item $smallPath).Length
                }
            }

            $zoomBmp.Dispose(); $cb.Dispose(); $small.Dispose()
            $x += 2 * $c + $pad
        }
        $src.Dispose()
        $row++
    }

    $g.Dispose()
    $sheetPath = Join-Path $OutputDir "contact-sheet-$($m.Key).png"
    $sheet.Save($sheetPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $sheet.Dispose()
    Write-Host "  -> $sheetPath"
}

$font.Dispose(); $fontBold.Dispose()
$brushText.Dispose(); $brushMuted.Dispose(); $brushLine.Dispose(); $brushBand.Dispose()

Write-Host ''
Write-Host '=== 1:1 icon size on disk (1024px source averaged ~285 KB) ==='
Write-Host ''
foreach ($s in $Sizes) {
    $cell = @($summary | Where-Object Size -eq $s)
    $one = ($cell | Where-Object Icon -eq 'Folder Closed').PngBytes
    $tot = ($cell | Measure-Object PngBytes -Sum).Sum
    Write-Host ('  {0,3}px : {1,5} bytes for one icon, {2,7:N0} KB for all {3} icons' -f $s, $one, ($tot / 1KB), $cell.Count)
}
Write-Host ''
Write-Host "Sheets: $OutputDir"
