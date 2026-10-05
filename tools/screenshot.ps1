param(
    [string]$TitleLike = '*',
    [string]$Out = "$env:TEMP\shot.png",
    [string]$ProcessName = ''
)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int ht, bool repaint);
}
"@

$candidates = Get-Process | Where-Object { $_.MainWindowHandle -ne 0 }

if ($ProcessName) {
    $candidates = $candidates | Where-Object { $_.ProcessName -eq $ProcessName }
}

$proc = $candidates | Where-Object { $_.MainWindowTitle -like $TitleLike } | Select-Object -First 1

if (-not $proc) {
    Write-Output "NO WINDOW matching '$TitleLike'"
    Write-Output 'visible windows were:'
    $candidates | ForEach-Object { "   [$($_.ProcessName)] $($_.MainWindowTitle)" }
    exit 1
}

$h = $proc.MainWindowHandle
[void][Win]::ShowWindow($h, 9)
[void][Win]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 700

$r = New-Object Win+RECT
[void][Win]::GetWindowRect($h, [ref]$r)
$w = $r.Right - $r.Left
$ht = $r.Bottom - $r.Top
Write-Output "window='$($proc.MainWindowTitle)' rect=$($r.Left),$($r.Top) ${w}x${ht}"

if ($w -le 0 -or $ht -le 0) { Write-Output 'BAD RECT'; exit 1 }

$bmp = New-Object System.Drawing.Bitmap $w, $ht
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size $w, $ht))
$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "saved $Out ($([math]::Round((Get-Item $Out).Length/1KB)) KB)"
