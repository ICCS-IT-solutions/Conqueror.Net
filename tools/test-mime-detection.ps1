$ErrorActionPreference = 'Stop'

# Exercises MimeTypeResolver against real files on disk, so accuracy and cost are measured
# rather than assumed. Reflection avoids pulling the whole app up as a UI process.
$root = Split-Path -Parent $PSScriptRoot
$dll = Join-Path $root 'bin\Debug\net8.0\Conqueror.Net.dll'
if (-not (Test-Path $dll)) { throw "Build first: $dll not found" }

$app = [System.Reflection.Assembly]::LoadFrom($dll)

$resolverType = $app.GetType('Conqueror.Net.FileBrowserUi.Services.MimeTypeResolver')
$iconType     = $app.GetType('Conqueror.Net.FileBrowserUi.Icons.FileIconResolver')

if (-not $resolverType -or -not $iconType) { throw 'Types not found in assembly' }

$resolver = $resolverType.GetProperty('Shared').GetValue($null)
$sniff    = $resolverType.GetMethod('SniffFile')
$resolve  = $resolverType.GetMethod('Resolve')

# FileIconResolver.ForMimeType probes avares:// through Avalonia's AssetLoader, which needs a
# live Avalonia application; that half is verified by running the app itself. This script covers
# the MIME half, which is pure logic plus a file read.

$testDir = Join-Path $env:TEMP 'mime-probe'
New-Item -ItemType Directory -Path $testDir -Force | Out-Null

# A .png that is really a PDF: the case where sniffing must beat the extension.
$misnamed = Join-Path $testDir 'actually-a-pdf.png'
[System.IO.File]::WriteAllBytes($misnamed, [byte[]]@(
    0x25,0x50,0x44,0x46,0x2D,0x31,0x2E,0x37, 0x0A,0x25,0xE2,0xE3,0xCF,0xD3,0x0A,0x00))

# A real PNG.
$realPng = Join-Path $testDir 'real.png'
[System.IO.File]::WriteAllBytes($realPng, [byte[]]@(
    0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A, 0x00,0x00,0x00,0x0D,0x49,0x48,0x44,0x52))

# No extension at all - sniffing is the only option.
$noExt = Join-Path $testDir 'README'
[System.IO.File]::WriteAllText($noExt, "plain text, no extension`n")

# Markdown: the raw matcher calls this message/rfc822, so Resolve() must override it.
$markdown = Join-Path $testDir 'notes.md'
[System.IO.File]::WriteAllText($markdown, "# Title`nsome prose`n")

# Genuinely binary - must NOT be reported as text.
$binary = Join-Path $testDir 'blob.dat'
[System.IO.File]::WriteAllBytes($binary, [byte[]]@(0x00, 0x01, 0x02, 0xFF, 0x00, 0x00))

$missing = Join-Path $testDir 'does-not-exist.qqq'

"`n{0,-22} {1,-22} {2}" -f 'file', 'Resolve()', 'SniffFile()'
'-' * 66

foreach ($f in @($realPng, $misnamed, $markdown, $noExt, $binary, $missing)) {
    $path = [string]$f
    $resolved = [string]$resolve.Invoke($resolver, [object[]]@($path))
    $sniffed = [string]$sniff.Invoke($resolver, [object[]]@($path))
    "{0,-22} {1,-22} {2}" -f (Split-Path $path -Leaf), $resolved, $sniffed
}

"`n--- sniff cost (20 iterations each) ---"
$measure = $resolverType.GetMethod('MeasureSniff')
foreach ($f in @($realPng, $misnamed)) {
    $t = $measure.Invoke($null, [object[]]@([string]$f, 20))
    "{0,-22} {1}" -f (Split-Path $f -Leaf), $t
}

Remove-Item $testDir -Recurse -Force -ErrorAction SilentlyContinue
