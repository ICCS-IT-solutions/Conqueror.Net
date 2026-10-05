param(
    [string]$SourceRoot = 'Icons_source\marchmountain-winxp-icons-png\Windows XP Icons',
    [string]$Destination = 'Assets\Icons\xp'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# semantic key -> file name in the Marchmountain pack. Deliberately tiny: these are the UI
# chrome icons only. File-entry icons keep using the scalable SVG theme, because a 1024 px
# raster downscaled to 16 px is a worse result than the vector for a plain manila folder.
$manifest = [ordered]@{
    'xp-explorer'       = 'Explorer'
    'xp-command-prompt' = 'Command Prompt'
    'xp-connection'     = 'Connection Status'
    'xp-back'           = 'Back'
    'xp-forward'        = 'Forward'
    'xp-up'             = 'Up'
    'xp-go'             = 'Go'
}

# Sizes actually rendered by the UI: 16 px in the tab strip and toolbar, 32 px for the
# larger toolbar buttons. 24 is skipped - nothing in the chrome draws at 24 px, and
# shipping an unused size is dead weight in the assembly.
$sizes = @(16, 32)

# The pack is CC0-1.0, so redistribution needs no attribution, but the licence is recorded
# in the manifest anyway so it travels with the assets.
$manifestOut = [ordered]@{
    source = 'Marchmountain Windows XP Icons (CC0-1.0)'
    method = 'alpha-weighted direct box filter'
    sizes  = $sizes
    icons  = [ordered]@{}
}

$dest = Join-Path (Get-Location) $Destination
New-Item -ItemType Directory -Path $dest -Force | Out-Null

function Invoke-BoxDownscale {
    <#
      Alpha-weighted area average. Each destination pixel is the mean of the source pixels it
      covers, weighted by their alpha, so transparent pixels contribute no colour and cannot
      bleed a dark fringe into the edge.

      This is method B from the downscale evaluation. At 1024 -> 16 (64:1) a naive bicubic
      samples sparsely and invents detail; an area average cannot alias, which is why it beat
      progressive halving at the non-power-of-two sizes there.
    #>
    param(
        [System.Drawing.Bitmap] $Source,
        [int] $Size
    )

    $srcRect = New-Object System.Drawing.Rectangle 0, 0, $Source.Width, $Source.Height
    $data = $Source.LockBits(
        $srcRect,
        [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb
    )

    try {
        $stride = $data.Stride
        $bytes = New-Object byte[] ($stride * $Source.Height)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)

        $dest = New-Object System.Drawing.Bitmap $Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $outRect = New-Object System.Drawing.Rectangle 0, 0, $Size, $Size
        $out = $dest.LockBits(
            $outRect,
            [System.Drawing.Imaging.ImageLockMode]::WriteOnly,
            ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        )
try {
            $outStride = $out.Stride
            $outBytes = New-Object byte[] ($outStride * $Size)

            for ($y = 0; $y -lt $Size; $y++) {
                # Source row range for this destination row, as [start, end).
                $sy0 = [int][math]::Floor($y * $Source.Height / $Size)
                $sy1 = [int][math]::Ceiling(($y + 1) * $Source.Height / $Size)
                if ($sy1 -le $sy0) { $sy1 = $sy0 + 1 }

                for ($x = 0; $x -lt $Size; $x++) {
                    $sx0 = [int][math]::Floor($x * $Source.Width / $Size)
                    $sx1 = [int][math]::Ceiling(($x + 1) * $Source.Width / $Size)
                    if ($sx1 -le $sx0) { $sx1 = $sx0 + 1 }

                    [long]$sumR = 0; [long]$sumG = 0; [long]$sumB = 0
                    [long]$sumA = 0; [long]$count = 0

                    for ($sy = $sy0; $sy -lt $sy1; $sy++) {
                        $rowOff = $sy * $stride

                        for ($sx = $sx0; $sx -lt $sx1; $sx++) {
                            $i = $rowOff + $sx * 4
                            $a = $bytes[$i + 3]

                            # Weight colour by alpha so a transparent neighbour contributes
                            # nothing rather than pulling the edge toward black.
                            $sumR += $bytes[$i + 2] * $a
                            $sumG += $bytes[$i + 1] * $a
                            $sumB += $bytes[$i] * $a
                            $sumA += $a
                            $count++
                        }
                    }

                    $o = ($y * $outStride) + ($x * 4)

                    if ($sumA -eq 0) {
                        # Fully transparent region: leave the pixel empty.
                        $outBytes[$o] = 0
                        $outBytes[$o + 1] = 0
                        $outBytes[$o + 2] = 0
                        $outBytes[$o + 3] = 0
                    }
                    else {
                        $outBytes[$o] = [int]($sumB / $sumA)
                        $outBytes[$o + 1] = [int]($sumG / $sumA)
                        $outBytes[$o + 2] = [int]($sumR / $sumA)
                        $outBytes[$o + 3] = [int]($sumA / $count)
                    }
                }
            }

            [System.Runtime.InteropServices.Marshal]::Copy($outBytes, 0, $out.Scan0, $outBytes.Length)
        }
        finally { $dest.UnlockBits($out) }

        return $dest
    }
    finally { $Source.UnlockBits($data) }
}
$total = 0
$rows = foreach ($key in $manifest.Keys) {
    $src = Join-Path (Get-Location) (Join-Path $SourceRoot "$($manifest[$key]).png")

    if (-not (Test-Path $src)) {
        [pscustomobject]@{ Key = $key; Status = 'MISSING'; Files = 0 }
        continue
    }

    $image = [System.Drawing.Bitmap]::new($src)
    $written = 0

    try {
        $manifestOut.icons[$key] = [ordered]@{}

        foreach ($size in $sizes) {
            $small = Invoke-BoxDownscale -Source $image -Size $size
            $path = Join-Path $dest "$key$size.png"
            $small.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
            $small.Dispose()

            $total += (Get-Item $path).Length
            $written++
            $manifestOut.icons[$key][[string]$size] = Split-Path $path -Leaf
        }

        [pscustomobject]@{ Key = $key; Status = 'ok'; Files = $written }
    }
    finally { $image.Dispose() }
}

$rows | Format-Table -AutoSize
"`n{0} icons x {1} sizes = {2} files, {3:N0} KB total -> {4}" -f $manifest.Count, $sizes.Count, ($sizes.Count * $manifest.Count), ($total / 1KB), $Destination

$manifestOut | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $dest 'manifest.json') -Encoding UTF8
'wrote manifest.json'

$missing = $rows | Where-Object Status -eq 'MISSING'
if ($missing) { Write-Warning "Missing: $($missing.Key -join ', ')" }