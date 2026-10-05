param(
    [string]$TitleLike = '*',
    [string]$Out = "$env:TEMP\shot.png",
    [string]$ProcessName = '',

    # A shell window is SystemDecorations=None, so Windows reports no MainWindowHandle and no
    # MainWindowTitle for it. Screenshot by enumerating the process's own top-level windows and
    # taking the first visible one, which is how the taskbar is captured.
    [switch]$ByProcess
)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public class Win {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int ht, bool repaint);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr p);

    private delegate bool EnumProc(IntPtr h, IntPtr p);

    /// <summary>Visible top-level windows owned by one process, largest first.</summary>
    /// <remarks>
    /// Needed because a window with SystemDecorations=None has no MainWindowHandle, so
    /// Process.MainWindowHandle is IntPtr.Zero and title matching cannot find it.
    /// </remarks>
    public static List<IntPtr> VisibleWindows(uint target) {
        var found = new List<IntPtr>();
        EnumWindows((h, _) => {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid == target && IsWindowVisible(h)) found.Add(h);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static string Title(IntPtr h) {
        var sb = new StringBuilder(512);
        GetWindowText(h, sb, 512);
        return sb.ToString();
    }
}
"@

if ($ByProcess) {
    # Take the largest visible window owned by the process; a shell has one, the taskbar.
    $target = Get-Process -Name $ProcessName -ErrorAction Stop | Select-Object -First 1
    $handles = [Win]::VisibleWindows([uint32]$target.Id)

    if ($handles.Count -eq 0) {
        Write-Output "NO VISIBLE WINDOW for process '$ProcessName'"
        exit 1
    }

    $sizes = foreach ($handle in $handles) {
        $rect = New-Object Win+RECT
        [void][Win]::GetWindowRect($handle, [ref]$rect)
        [pscustomobject]@{
            Handle = $handle
            Title = [Win]::Title($handle)
            Area = ($rect.Right - $rect.Left) * ($rect.Bottom - $rect.Top)
        }
    }

    $proc = $sizes | Sort-Object Area -Descending | Select-Object -First 1
    $h = $proc.Handle
}
else {
    $candidates = Get-Process | Where-Object { $_.MainWindowHandle -ne 0 }

    if ($ProcessName) {
        $candidates = $candidates | Where-Object { $_.ProcessName -eq $ProcessName }
    }

    $match = $candidates | Where-Object { $_.MainWindowTitle -like $TitleLike } | Select-Object -First 1

    if (-not $match) {
        Write-Output "NO WINDOW matching '$TitleLike'"
        Write-Output 'visible windows were:'
        $candidates | ForEach-Object { "   [$($_.ProcessName)] $($_.MainWindowTitle)" }
        exit 1
    }

    $h = $match.MainWindowHandle
}

[void][Win]::ShowWindow($h, 9)
[void][Win]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 700

$r = New-Object Win+RECT
[void][Win]::GetWindowRect($h, [ref]$r)
$w = $r.Right - $r.Left
$ht = $r.Bottom - $r.Top
Write-Output "window='$($proc.Title)' rect=$($r.Left),$($r.Top) ${w}x${ht}"

if ($w -le 0 -or $ht -le 0) { Write-Output 'BAD RECT'; exit 1 }

$bmp = New-Object System.Drawing.Bitmap $w, $ht
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size $w, $ht))
$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "saved $Out ($([math]::Round((Get-Item $Out).Length/1KB)) KB)"
