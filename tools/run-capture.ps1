param(
    [string]$TitleLike = '*',
    [string]$Out = "$env:TEMP\shot.png",
    [int]$WarmupSec = 15,
    [string]$ProcessName = '',
    [string]$AppArgs = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'bin\Debug\net8.0\Conqueror.Net.exe'
if (-not (Test-Path $exe)) { Write-Output "MISSING $exe"; exit 1 }

$stdout = Join-Path $env:TEMP 'conq.out.log'
$stderr = Join-Path $env:TEMP 'conq.err.log'
Remove-Item $stdout,$stderr -ErrorAction SilentlyContinue

Write-Output "launching $exe $AppArgs"
$argList = if ($AppArgs) { $AppArgs -split '\s+' } else { @() }
$p = Start-Process -FilePath $exe -ArgumentList $argList -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr -WorkingDirectory (Split-Path $exe)

# Wait for a main window to appear
$hwnd = [IntPtr]::Zero
for ($i = 0; $i -lt $WarmupSec; $i++) {
    Start-Sleep -Seconds 1
    if ($p.HasExited) { Write-Output "EXITED after ${i}s code=$($p.ExitCode)"; break }
    $p.Refresh()
    if ($p.MainWindowHandle -ne 0) { $hwnd = $p.MainWindowHandle; Write-Output "window up after ${i}s"; break }
}

if ($hwnd -eq [IntPtr]::Zero) {
    Write-Output 'NO WINDOW'
    if ($p.HasExited) { Write-Output "exit code = $($p.ExitCode)" }
    Write-Output '--- stdout ---'; if (Test-Path $stdout) { Get-Content $stdout -Tail 40 }
    Write-Output '--- stderr ---'; if (Test-Path $stderr) { Get-Content $stderr -Tail 40 }
    exit 1
}

Start-Sleep -Seconds 6
& (Join-Path $PSScriptRoot 'screenshot.ps1') -TitleLike $TitleLike -Out $Out -ProcessName $ProcessName

if (-not $p.HasExited) { $p.Refresh(); Write-Output "still alive: responding=$($p.Responding)" }
else { Write-Output "DIED during run, code=$($p.ExitCode)" }

Write-Output '--- stderr tail ---'
if (Test-Path $stderr) { Get-Content $stderr -Tail 25 }

Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
Get-Process -Name 'Conqueror.Net' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
