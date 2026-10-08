<#
.SYNOPSIS
    Converts the Windows XP PSD icon pack to a multi-resolution PNG tree.

.DESCRIPTION
    Three stages:
    1. Each PSD is rendered to a 256x256 PNG at Assets/Icons/xp/256/<stem>.png.
    2. The 256 base is downscaled to 16x16 and 32x32 for chrome at xp/16/ and xp/32/.
    3. A manifest.json maps semantic keys to their stems and available sizes.
#>
param(
    [string]$SourceRoot = 'C:\Users\Iain\Downloads\Windows.XP.Icons\Windows XP Icons',
    [string]$Destination  = 'Assets\Icons\xp',
    [string]$ImageMagick  = '',
    [switch]$Batch
)

$ErrorActionPreference = 'Stop'

# Resolve project root and destination directories
$projectRoot = Split-Path $PSScriptRoot -Parent
$dest = Join-Path $projectRoot $Destination

if (-not (Test-Path $SourceRoot)) {
    throw "PSD source not found: $SourceRoot"
}

# Locate magick.exe
$magick = $ImageMagick
if (-not $magick) {
    $cmd = Get-Command magick -ErrorAction SilentlyContinue
    if ($cmd) { $magick = $cmd.Source }
}
if (-not $magick) {
    foreach ($c in @('C:\Program Files\ImageMagick-7.1.2-Q16-HDRI\magick.exe','C:\Program Files\ImageMagick\magick.exe')) {
        if (Test-Path $c) { $magick = $c; break }
    }
}
if (-not $magick) { throw "ImageMagick not found." }

# Stem -> PSD filename mapping (alphabetical by stem).
$PsdMap = [ordered]@{
    'xp-audio'          = 'Generic Audio'
    'xp-back'           = 'Back'
    'xp-batfile'        = 'BAT'
    'xp-checkbox-check' = 'Checkbox'
    'xp-checkbox-clear' = 'Checkbox clear'
    'xp-checkbox-half'  = 'Checkbox half shaded'
    'xp-checkbox-filter' = 'Checkbox select filtered'
    'xp-checkbox-shaded' = 'Checkbox shaded'
    'xp-checklist'      = 'Checklist'
    'xp-closedfolder'   = 'Folder Closed'
    'xp-command-prompt' = 'Command Prompt'
    'xp-configfile'     = 'INF'
    'xp-connection'     = 'Connection Status'
    'xp-copy'           = 'Copy'
    'xp-cut'            = 'Cut'
    'xp-delete'         = 'Delete'
    'xp-explorer'       = 'Explorer'
    'xp-fileshortcut'   = 'Generic Document'
    'xp-forward'        = 'Forward'
    'xp-genericdocument'= 'Generic Document'
    'xp-go'             = 'Go'
    'xp-invertselect'   = 'Single Click'
    'xp-libfile'        = 'DLL'
    'xp-newfile'        = 'new file'
    'xp-newfolder'      = 'New Folder'
    'xp-openfolder'     = 'Folder Opened'
    'xp-paste'          = 'Paste'
    'xp-properties'     = 'Properties'
    'xp-program'        = 'Application Window'
    'xp-programshortcut'= 'Generic Document'
    'xp-refresh'        = 'IE Refresh'
    'xp-rename'         = 'Rename'
    'xp-selectall'      = 'Single Click'
    'xp-selectnone'     = 'Single Click'
    'xp-shortcutarrow'  = 'Shortcut overlay'
    'xp-up'             = 'Up'
    'xp-video'          = 'Generic Video'
}

$NoPsdIcons = @('xp-close')

$manifestIcons = [ordered]@{}
$stats = @{ Converted = 0; Copied = 0; Skipped = 0 }

# Ensure output size directories exist
foreach ($size in @(16, 32, 256)) {
    $sub = Join-Path $dest $size
    if (-not (Test-Path $sub)) { New-Item -ItemType Directory -Path $sub -Force | Out-Null }
}

function Invoke-Downscale {
    param([string]$InputPath, [string]$OutputPath, [int]$Size)
    & $magick $InputPath -filter Lanczos -define 'filter:blur=1' -resize "${Size}x${Size}" -define 'png:color-type=6' -define 'png:exclude-chunk=date' "PNG:$OutputPath"
}

# Build the work list. In batch mode, enumerate every PSD in the source folder
# and derive a kebab-case stem from the filename. In the default (curated) mode,
# use the hand-mapped $PsdMap so icon keys match the C# dictionary.
if ($Batch) {
    Write-Host "Batch mode: scanning all PSDs in $SourceRoot"
    $workItems = Get-ChildItem $SourceRoot -Filter '*.psd' |
        Sort-Object Name |
        ForEach-Object {
            $stem = 'xp-' + ($_.BaseName -replace '[\s_]+', '-').ToLowerInvariant()
            [PSCustomObject]@{ Stem = $stem; PsdName = $_.BaseName }
        }
} else {
    $workItems = $PsdMap.GetEnumerator() | ForEach-Object {
        [PSCustomObject]@{ Stem = $_.Key; PsdName = $_.Value }
    }
}

Write-Host "Processing $($workItems.Count) icons..."

# Stage 1: PSD-based icons -- render to 256, downscale to 16/32
$idx = 0
foreach ($item in $workItems) {
    $stem = $item.Stem
    $psdName = $item.PsdName
    $psdPath = Join-Path $SourceRoot "$psdName.psd"
    $idx++

    if (-not (Test-Path $psdPath)) {
        if (-not $Batch) {
            # Curated mode only: fall back to existing flat PNG for known-missing PSD
            $ex16 = Join-Path $dest "xp-${stem}16.png"
            $ex32 = Join-Path $dest "xp-${stem}32.png"
            if (Test-Path $ex16) { Copy-Item $ex16 (Join-Path (Join-Path $dest "16") "$stem.png") -Force; $stats.Copied++ }
            if (Test-Path $ex32) { Copy-Item $ex32 (Join-Path (Join-Path $dest "32") "$stem.png") -Force; $stats.Copied++ }
            $src = if (Test-Path $ex32) { $ex32 } elseif (Test-Path $ex16) { $ex16 } else { $null }
            if ($src) { Invoke-Downscale -InputPath $src -OutputPath (Join-Path (Join-Path $dest "256") "$stem.png") -Size 256; $stats.Copied++ }
            $manifestIcons[$stem] = [ordered]@{ '256' = $null; '16' = $null; '32' = $null }
        }
        continue
    }

    $baseOut = Join-Path (Join-Path $dest "256") "$stem.png"

    # Try the first layer ([0]); fall back to the last layer if [0] is blank or invalid.
    & $magick "$($psdPath)[0]" -resize 256x256 -define 'png:color-type=6' -define 'png:exclude-chunk=date' "PNG:$baseOut" 2>$null
    if ($LASTEXITCODE -ne 0) {
        & $magick "$($psdPath)[-1]" -resize 256x256 -define 'png:color-type=6' -define 'png:exclude-chunk=date' "PNG:$baseOut" 2>$null
    }
    $stats.Converted++
    Invoke-Downscale -InputPath $baseOut -OutputPath (Join-Path (Join-Path $dest "16") "$stem.png") -Size 16
    Invoke-Downscale -InputPath $baseOut -OutputPath (Join-Path (Join-Path $dest "32") "$stem.png") -Size 32

    $manifestIcons[$stem] = [ordered]@{ '256' = "256/$stem.png"; '16' = "16/$stem.png"; '32' = "32/$stem.png" }
    Write-Progress -Activity "Importing XP icons from PSD" -Status $stem -PercentComplete ($idx / $workItems.Count * 100)
}

# Stage 2: Non-PSD icons -- copy existing flat PNGs, upscale to 256 (curated mode only)
if (-not $Batch) {
    foreach ($stem in $NoPsdIcons) {
        $old16 = Join-Path $dest "xp-${stem}16.png"
        $old32 = Join-Path $dest "xp-${stem}32.png"
        $src = if (Test-Path $old32) { $old32 } elseif (Test-Path $old16) { $old16 } else { $null }

        if ($src) {
            Invoke-Downscale -InputPath $src -OutputPath (Join-Path (Join-Path $dest "256") "$stem.png") -Size 256
        } else {
            Write-Warning "No source PNG for $stem"; $stats.Skipped++
        }

        $manifestIcons[$stem] = [ordered]@{
            '256' = if (Test-Path (Join-Path (Join-Path $dest "256") "$stem.png")) { "256/$stem.png" } else { $null }
            '16'  = if (Test-Path (Join-Path (Join-Path $dest "16") "$stem.png")) { "16/$stem.png" } else { $null }
            '32'  = if (Test-Path (Join-Path (Join-Path $dest "32") "$stem.png")) { "32/$stem.png" } else { $null }
        }
    }
}

# Stage 3: Write manifest.json
$manifest = [ordered]@{
    source = 'Windows XP Icon Pack (PSD, CC0-1.0)'
    method = 'PSD rendered at 256x256; chrome sizes are Lanczos downscale of 256 base'
    baseSize = 256
    chromeSizes = @(16, 32)
    icons = $manifestIcons
}
Set-Content (Join-Path $dest 'manifest.json') -Value ($manifest | ConvertTo-Json -Depth 5) -Encoding UTF8

"Done: $($stats.Converted) PSD-derived, $($stats.Copied) copied/upscaled, $($stats.Skipped) skipped."

# Remove legacy flat PNGs
Get-ChildItem $dest -Filter '*.png' -File | Remove-Item -Force
