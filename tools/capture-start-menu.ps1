<#
.SYNOPSIS
    Screenshots the shell's Start menu.

.DESCRIPTION
    The Start menu is a second top-level window, so it is found by enumerating the shell
    process's visible windows and taking the one narrower than 600 px - that is the menu,
    while the taskbar spans the whole screen. Both are SystemDecorations=None, so neither has
    a MainWindowHandle and Process.MainWindowTitle cannot be used.
#>
param(
    [string]$Out = "$PSScriptRoot\..\docs\screenshot-shell-startmenu.png",
    [switch]$Open
)

Add-Type -AssemblyName System.Drawing

Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public class ShellCap {
    public struct RECT { public int L, T, R, B; }

    private delegate bool EnumProc(IntPtr h, IntPtr p);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, IntPtr e);

    public static List<IntPtr> Visible(uint target) {
        var found = new List<IntPtr>();
        EnumWindows((h, _) => {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid == target && IsWindowVisible(h)) found.Add(h);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static RECT Rect(IntPtr h) { RECT r; GetWindowRect(h, out r); return r; }

    /// <summary>Presses the Start orb, which sits at the far left of the bar.</summary>
    public static void ClickStart(int barTop) {
        SetCursorPos(27, barTop + 17);
        System.Threading.Thread.Sleep(250);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(80);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
    }
}
'@

$proc = Get-Process -Name 'Conqueror.Net.Shell' -ErrorAction Stop | Select-Object -First 1
$handles = [ShellCap]::Visible([uint32]$proc.Id)

if ($handles.Count -eq 0) {
    Write-Output 'shell has no visible window'
    exit 1
}

$taskbar = $handles | ForEach-Object { [ShellCap]::Rect($_) } |
    Sort-Object { ($_.R - $_.L) * ($_.B - $_.T) } -Descending | Select-Object -First 1

if ($Open) {
    [ShellCap]::SetForegroundWindow(($handles | Where-Object { [ShellCap]::Rect($_).L -eq $taskbar.L })[0]) | Out-Null
    Start-Sleep -Milliseconds 500
    [ShellCap]::ClickStart($taskbar.T)
    Start-Sleep -Seconds 2
    $handles = [ShellCap]::Visible([uint32]$proc.Id)
}

# The menu is the narrow window; the taskbar spans the full screen width.
$menu = $handles |
    ForEach-Object { [ShellCap]::Rect($_) } |
    Where-Object { ($_.R - $_.L) -lt 600 } |
    Select-Object -First 1

if (-not $menu) {
    Write-Output 'Start menu is not open'
    exit 1
}

$w = $menu.R - $menu.L
$h = $menu.B - $menu.T
Write-Output "menu rect=$($menu.L),$($menu.T) ${w}x${h}"

$bitmap = New-Object System.Drawing.Bitmap $w, $h
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.CopyFromScreen($menu.L, $menu.T, 0, 0, (New-Object System.Drawing.Size $w, $h))

$resolved = [System.IO.Path]::GetFullPath($Out)
$bitmap.Save($resolved, [System.Drawing.Imaging.ImageFormat]::Png)
$graphics.Dispose()
$bitmap.Dispose()

Write-Output "saved $resolved"
