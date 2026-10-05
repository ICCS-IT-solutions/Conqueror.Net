<#
.SYNOPSIS
    Copies a curated subset of an SVG icon theme into the project's Assets/Icons folder.

.DESCRIPTION
    Upstream icon themes are far too large to embed (this one is 6223 SVGs / 403 MB) and
    every size bucket in it is a symlink to a single scalable/ folder. This script copies
    only the categories a file manager actually shows, dereferences the symlinks, and
    flattens the filenames to stable semantic keys.

    Selection rules that were applied by hand after inspecting the pack:
      * Skip SVGs that embed base64 raster (<image ... base64>) - they are not scalable and
        weigh hundreds of KB each (e.g. application-msword.svg = 345 KB).
      * Skip pathologically heavy vectors - the OpenDocument sheet/slides icons are ~784 KB
        of paths apiece, absurd for a 16 px list icon.
      * Prefer pure-vector icons in the 15-90 KB range.

    Run from the repository root:
        pwsh -File tools/import-icon-theme.ps1 -SourceRoot .\Icons_source\<theme>
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$SourceRoot,

    [string]$Destination = 'Assets\Icons'
)

$ErrorActionPreference = 'Stop'

$scalable = Join-Path $SourceRoot 'scalable'
if (-not (Test-Path $scalable)) {
    throw "Not an icon theme: $scalable not found"
}

# semantic key -> path within scalable/. Keep this list small on purpose.
# Notes on what was rejected while curating:
#   mimetypes/image-x-icon.svg            759 KB of paths for a .ico list icon - dropped
#   mimetypes/text-x-generic.svg          symlink to text-plain, so it was a duplicate
#   mimetypes/application-msword.svg      345 KB, embeds base64 raster - not scalable
#   mimetypes/application-x-msdownload.svg 332 KB, embeds base64 raster
#   mimetypes/application-vnd.oasis.*     ~784 KB each of raw paths - absurd at 16 px
$manifest = [ordered]@{
    'folder'        = 'places/folder.svg'
    'folder-remote' = 'places/folder-remote.svg'
    'drive-harddisk'= 'devices/drive-harddisk.svg'
    'drive-optical' = 'devices/drive-optical.svg'
    'network'       = 'places/network-workgroup.svg'
    'desktop'       = 'places/user-desktop.svg'
    'home'          = 'places/user-home.svg'
    'trash'         = 'places/user-trash.svg'
    'text'          = 'mimetypes/text-plain.svg'
    'script'        = 'mimetypes/text-x-script.svg'
    'image'         = 'mimetypes/image-x-generic.svg'
    'audio'         = 'mimetypes/audio-x-generic.svg'
    'video'         = 'mimetypes/video-x-generic.svg'
    'archive'       = 'mimetypes/application-x-archive.svg'
    'package'       = 'mimetypes/package-x-generic.svg'
    'executable'    = 'mimetypes/application-x-executable.svg'
    'install'       = 'mimetypes/text-x-install.svg'
    'pdf'           = 'mimetypes/application-pdf.svg'
    'document'      = 'mimetypes/application-postscript.svg'
}

$dest = Join-Path (Get-Location) $Destination
New-Item -ItemType Directory -Path $dest -Force | Out-Null

$total = 0
$rows = foreach ($key in $manifest.Keys) {
    $src = Join-Path $scalable ($manifest[$key] -replace '/', '\')

    if (-not (Test-Path $src)) {
        [pscustomobject]@{ Key = $key; Status = 'MISSING'; KB = 0; Paths = 0 }
        continue
    }

    # ReadAllBytes dereferences symlinks; Copy-Item does not reliably do so.
    $bytes = [System.IO.File]::ReadAllBytes($src)
    [System.IO.File]::WriteAllBytes((Join-Path $dest "$key.svg"), $bytes)
    $total += $bytes.Length

    $text = [System.Text.Encoding]::UTF8.GetString($bytes)
    [pscustomobject]@{
        Key    = $key
        Status = 'ok'
        KB     = [math]::Round($bytes.Length / 1KB, 1)
        Paths  = ([regex]::Matches($text, '<path')).Count
    }
}

$rows | Format-Table -AutoSize
"`n{0} icons, {1:N0} KB total -> {2}" -f $rows.Count, ($total / 1KB), $dest

$missing = $rows | Where-Object Status -eq 'MISSING'
if ($missing) {
    Write-Warning "Missing: $($missing.Key -join ', ')"
}
