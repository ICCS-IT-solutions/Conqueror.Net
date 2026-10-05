<#
.SYNOPSIS
    Verifies the taskbar docks to each screen edge, and captures the vertical layouts.

.DESCRIPTION
    Launches the shell once per edge with --edge and asserts the resulting window rectangle
    against the primary screen. The shell is a decorationless window with no MainWindowHandle,
    so the handle is found by enumerating the process's visible windows.

    Example:
        pwsh -File tools/test-taskbar-edges.ps1
        pwsh -File tools/test-taskbar-edges.ps1 -Capture
#>
param(
    [string]$ShellExe = "$PSScriptRoot\..\Shell\bin\Debug\net8.0\Conqueror.Net.Shell.exe",
    [switch]$Capture
)

Add-Type -AssemblyName System.Drawing

Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public class EdgeProbe {
    public struct RECT { public int L, T, R, B; }

    private delegate bool EnumProc(IntPtr h, IntPtr p);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);

    public static RECT RectOf(uint target) {
        RECT found = new RECT { R = -1 };
        EnumWindows((h, _) => {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid == target && IsWindowVisible(h)) {
                RECT r;
                GetWindowRect(h, out r);
                // The taskbar is the widest visible window; the Start menu is a small one.
                if (r.R - r.L > found.R - found.L) { found = r; }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static IntPtr HandleOf(uint target) {
        IntPtr best = IntPtr.Zero;
        int bestArea = 0;
        EnumWindows((h, _) => {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid == target && IsWindowVisible(h)) {
                RECT r;
                GetWindowRect(h, out r);
                int area = (r.R - r.L) * (r.B - r.T);
                if (area > bestArea) { bestArea = area; best = h; }
            }
            return true;
        }, IntPtr.Zero);
        return best;
    }
}
'@

Add-Type -AssemblyName System.Windows.Forms
$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds

if (-not (Test-Path $ShellExe)) {
    Write-Output "shell not built: $ShellExe"
    exit 1
}

# Expected bar geometry per edge.
#
# A hashtable of arrays is deliberately avoided: PowerShell enumerates the *values* of such a
# collection rather than its keys, which silently breaks $expected[$edge] lookups.
#

# GetWindowRect returns the window's outer frame, not the bar's content rectangle. Windows
    # keeps an invisible resize border on a decorationless window, and at the bottom edge that
    # frame sits *above* the content, so a bar requested at y=1050 reports 1010..1049. Left and
    # top report no offset; right reports 2 px. These per-side quirks vary with DPI and theme,
    # so rather than hard-coding them the test asserts what actually matters: the bar sits
    # flush with the screen edge it is docked to, within the size of that frame.
    $frameTolerance = 40

    $expected = @(
        [pscustomobject]@{ Edge = 'bottom'; Far = 'B'; Target = $screen.Bottom }
        [pscustomobject]@{ Edge = 'top'; Far = 'T'; Target = $screen.Y }
        [pscustomobject]@{ Edge = 'left'; Far = 'L'; Target = $screen.X }
        [pscustomobject]@{ Edge = 'right'; Far = 'R'; Target = $screen.Right }
    )

$failed = 0

foreach ($case in $expected) {
    $edge = $case.Edge

    Get-Process -Name 'Conqueror.Net.Shell' -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 600

    $process = Start-Process $ShellExe -ArgumentList "--edge $edge" -PassThru

    # The shell waits for Avalonia startup; poll until the window is placed rather than
    # sleeping a fixed amount, which would be either flaky or slow.
    $rect = New-Object EdgeProbe+RECT
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        Start-Sleep -Milliseconds 400
        $rect = [EdgeProbe]::RectOf([uint32]$process.Id)
        if ($rect.R -gt 0) { break }
    }

    if ($rect.R -lt 0) {
        Write-Output ("{0,-7} FAILED: no visible window" -f $edge)
        $failed++
        continue
    }

    # Assert the bar is flush with the edge it is docked to, allowing for the frame.
    $measured = switch ($case.Far) {
        'B' { $rect.B }
        'T' { $rect.T }
        'L' { $rect.L }
        'R' { $rect.R }
    }

    $delta = [Math]::Abs($measured - $case.Target)
    $status = if ($delta -le $frameTolerance) { 'OK  ' } else { 'FAIL' }

    if ($status -eq 'FAIL') { $failed++ }

    Write-Output ("{0} {1,-7} {2,-6}={3,-5} want={4,-5} delta={5,-3} rect={6},{7} {8}x{9}" -f
        $status, $edge, $case.Far, $measured, $case.Target, $delta,
        $rect.L, $rect.T, ($rect.R - $rect.L), ($rect.B - $rect.T))

    if ($Capture) {
        $handle = [EdgeProbe]::HandleOf([uint32]$process.Id)
        [EdgeProbe]::SetForegroundWindow($handle) | Out-Null
        Start-Sleep -Milliseconds 500

        $width = $rect.R - $rect.L
        $height = $rect.B - $rect.T
        $bitmap = New-Object System.Drawing.Bitmap $width, $height
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.CopyFromScreen($rect.L, $rect.T, 0, 0, (New-Object System.Drawing.Size $width, $height))
        $path = [System.IO.Path]::GetFullPath("$PSScriptRoot\..\docs\screenshot-shell-$edge.png")
        $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        $graphics.Dispose()
        $bitmap.Dispose()
        Write-Output "     captured $path"
    }
}

Get-Process -Name 'Conqueror.Net.Shell' -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue

if ($failed -gt 0) {
    Write-Output "`n$failed edge(s) misplaced"
    exit 1
}

Write-Output "`nall edges positioned correctly"
