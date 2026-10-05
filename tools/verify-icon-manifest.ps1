param([string]$SourceRoot)

$ErrorActionPreference = 'Stop'
$s = Join-Path $SourceRoot 'scalable'

# semantic key -> path relative to the pack's scalable/ directory.
# Deliberately small: this pack has 6223 SVGs (403 MB) and ships per-RID symlinks
# for every size bucket. A file manager only needs the categories below.
$manifest = [ordered]@{
    'folder'          = 'places/folder.svg'
    'folder-remote'   = 'places/folder-remote.svg'
    'folder-documents'= 'places/user-home.svg'
    'drive-harddisk'  = 'devices/drive-harddisk.svg'
    'drive-optical'   = 'devices/drive-optical.svg'
    'drive-removable' = 'devices/drive-removable-media.svg'
    'network'         = 'places/network-workgroup.svg'
    'desktop'         = 'places/user-desktop.svg'
    'home'            = 'places/user-home.svg'
    'trash'           = 'places/user-trash.svg'
    'text'            = 'mimetypes/text-plain.svg'
    'text-generic'    = 'mimetypes/text-x-generic.svg'
    'script'          = 'mimetypes/text-x-script.svg'
    'image'           = 'mimetypes/image-x-generic.svg'
    'icon'            = 'mimetypes/image-x-icon.svg'
    'audio'           = 'mimetypes/audio-x-generic.svg'
    'video'           = 'mimetypes/video-x-generic.svg'
    'archive'         = 'mimetypes/application-x-archive.svg'
    'package'         = 'mimetypes/package-x-generic.svg'
    'executable'      = 'mimetypes/application-x-executable.svg'
    'installer'       = 'mimetypes/application-x-msdownload.svg'
    'pdf'             = 'mimetypes/application-pdf.svg'
    'document'        = 'mimetypes/application-msword.svg'
    'spreadsheet'     = 'mimetypes/application-vnd.oasis.opendocument.spreadsheet.svg'
    'presentation'    = 'mimetypes/application-vnd.oasis.opendocument.presentation.svg'
    'postscript'      = 'mimetypes/application-postscript.svg'
}

$ok = 0; $missing = @()
foreach ($k in $manifest.Keys) {
    $p = Join-Path $s ($manifest[$k] -replace '/', '\')
    if (Test-Path $p) {
        $len = (Get-Item $p -Force).Length
        $link = (Get-Item $p -Force).LinkType
        "{0,-18} OK  {1,7} KB  {2,-10} {3}" -f $k, [math]::Round($len/1KB,1), $(if($link){$link}else{'file'}), $manifest[$k]
        $ok++
    } else {
        "{0,-18} MISSING {1}" -f $k, $manifest[$k]
        $missing += $k
    }
}
"`nresolved: $ok / $($manifest.Count)"
if ($missing) { "MISSING: $($missing -join ', ')" }
