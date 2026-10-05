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

# freedesktop-named icons, so FileIconResolver.ForMimeType can hit them by direct transform
# (application/pdf -> application-pdf.svg) instead of always collapsing to a generic icon.
# Size-gated individually: image/png alone is 2.1 MB of paths.
$manifest += [ordered]@{
    'application-zip'               = 'mimetypes/application-zip.svg'
    'application-gzip'              = 'mimetypes/application-gzip.svg'
    'application-x-tar'             = 'mimetypes/application-x-tar.svg'
    'application-x-7z-compressed'   = 'mimetypes/application-x-7z-compressed.svg'
    'application-vnd.rar'           = 'mimetypes/application-vnd.rar.svg'
    'application-x-bzip'            = 'mimetypes/application-x-bzip.svg'
    'application-x-xz'              = 'mimetypes/application-x-xz.svg'
    'application-vnd.ms-excel'      = 'mimetypes/application-vnd.ms-excel.svg'
    'application-vnd.ms-powerpoint' = 'mimetypes/application-vnd.ms-powerpoint.svg'
    'application-x-shellscript'     = 'mimetypes/application-x-shellscript.svg'
    'application-json'              = 'mimetypes/application-json.svg'
    'application-xml'               = 'mimetypes/application-xml.svg'
    'image-jpeg'                    = 'mimetypes/image-jpeg.svg'
    'image-gif'                     = 'mimetypes/image-gif.svg'
    'image-svg+xml'                 = 'mimetypes/image-svg+xml.svg'
    'image-bmp'                     = 'mimetypes/image-bmp.svg'
    'image-tiff'                    = 'mimetypes/image-tiff.svg'
    'image-webp'                    = 'mimetypes/image-webp.svg'
    'text-x-python'                 = 'mimetypes/text-x-python.svg'
    'audio-mpeg'                    = 'mimetypes/audio-mpeg.svg'
    'audio-x-wav'                   = 'mimetypes/audio-x-wav.svg'
    'audio-ogg'                     = 'mimetypes/audio-ogg.svg'
    'audio-x-flac'                  = 'mimetypes/audio-x-flac.svg'
    'video-mp4'                     = 'mimetypes/video-mp4.svg'
    'video-x-matroska'              = 'mimetypes/video-x-matroska.svg'
    'video-x-msvideo'               = 'mimetypes/video-x-msvideo.svg'
    'video-x-ms-wmv'                = 'mimetypes/video-x-ms-wmv.svg'
}

# Anything above this is reported as OVERSIZE and skipped rather than embedded. The gates that
# matter in this pack, all measured:
#   image-x-icon.svg        759 KB  63 paths, for a .ico list icon
#   image/png.svg          2144 KB  pure vector but absurd at 18 px
#   application-msword.svg  345 KB  embeds base64 raster, so not actually scalable
#   application-html.svg    111 KB  embeds base64 raster
# Per-key override of the size gate. folder-remote is 141 KB of pure vector, which the pack
# charges for a detailed network folder; it is core navigation chrome, so it is worth keeping
# even though a filetype icon of that size would not be. No raster is allowed through here.
$SizeOverrides = @{ 'folder-remote' = 150KB }

$MaxBytes = 100KB

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

    $limit = if ($SizeOverrides.ContainsKey($key)) { $SizeOverrides[$key] } else { $MaxBytes }

    if ($bytes.Length -gt $limit)
    {
        [pscustomobject]@{
            Key = $key; Status = 'OVERSIZE'
            KB = [math]::Round($bytes.Length / 1KB, 1); Paths = 0
        }
        continue
    }

    $text = [System.Text.Encoding]::UTF8.GetString($bytes)

    if ($text -match 'base64')
    {
        [pscustomobject]@{
            Key = $key; Status = 'RASTER'
            KB = [math]::Round($bytes.Length / 1KB, 1); Paths = 0
        }
        continue
    }

    [System.IO.File]::WriteAllBytes((Join-Path $dest "$key.svg"), $bytes)
    $total += $bytes.Length

    [pscustomobject]@{
        Key    = $key
        Status = 'ok'
        KB     = [math]::Round($bytes.Length / 1KB, 1)
        Paths  = ([regex]::Matches($text, '<path')).Count
    }
}

$rows | Format-Table -AutoSize
"`n{0} icons embedded, {1:N0} KB total -> {2}" -f ($rows | Where-Object Status -eq 'ok').Count, ($total / 1KB), $dest

$missing = $rows | Where-Object Status -eq 'MISSING'
$skipped = $rows | Where-Object Status -in 'OVERSIZE', 'RASTER'

if ($skipped) {
    "`nskipped:"
    $skipped | ForEach-Object { "  {0,-12} {1,7} KB  {2}" -f $_.Key, $_.KB, $_.Status }
}

if ($missing) {
    Write-Warning "Missing: $($missing.Key -join ', ')"
}
