# Shell module — taskbar and Start menu

An XP-style taskbar and Start menu, built as its own program in `Shell/`.

![Taskbar](screenshot-shell-taskbar.png)
![Start menu](screenshot-shell-startmenu.png)

## Yes, and it is deliberately a separate process

`Shell/Conqueror.Net.Shell.csproj` builds `Conqueror.Net.Shell.exe`. It is not a window inside
the file manager. Three reasons:

1. **Lifetime.** The shell must survive the file manager closing. The file manager hosts CEF,
   and a crash in the browser engine should not take the taskbar with it.
2. **The screen edge is a shared resource.** A bar pinned to the bottom of the primary display
   outlives any one window that happens to be open.
3. **Blast radius.** Two small processes are easier to reason about than one process running a
   file manager, an embedded Chromium instance and a desktop surface.

Neither project references the other. The only thing shared is the icon folder, which the shell
*links* rather than copies:

```xml
<AvaloniaResource Include="../Assets/Icons/*.svg" Link="Assets/Icons/%(Filename)%(Extension)" />
```

So there is one copy of the artwork and no build-order dependency. A project reference would
have dragged CEF and the whole browser stack into a program that only draws a 30-pixel bar.

### Run it

```powershell
dotnet run --project Shell/Conqueror.Net.Shell.csproj
```

Or from VS Code: the `run:shell` task, or the `Launch (shell)` debug configuration.

## What "standalone" means, and the thing that is *not* possible

This is an **additional** shell that draws over the desktop. It does **not** replace the Windows
taskbar, and it cannot be made to.

Replacing the real taskbar means injecting into `explorer.exe`, which is what
[ExplorerPatcher](https://github.com/valinet/ExplorerPatcher) and
[Windhawk](https://windhawk.dev) do. That work is native code, keyed to specific Windows builds,
maintained per-version, and — in ExplorerPatcher's case — closed source "for legal reasons".

Anything built that way is not something to hand to a user of this repository. A standalone
managed process is the same idea with none of those problems.

## How the taskbar works

The window is deliberately unremarkable to Avalonia:

```xml
SystemDecorations="None"
CanResize="False"
ShowInTaskbar="False"
Topmost="True"
Background="Transparent"
TransparencyLevelHint="Transparent"
```

- `SystemDecorations="None"` removes the title bar and resize frame.
- `Topmost="True"` keeps the bar above ordinary windows.
- `ShowInTaskbar="False"` stops it appearing as its own taskbar entry, which would be circular.

`ShellWindowManager.Reposition` pins it to the bottom of the primary screen, converting between
screen pixels and DIPs via `Screen.Scaling`. Verified on a 1920×1080 display:

```
screen bounds=0, 0, 1920, 1080 scale=1 thickness=30 pos=0, 1050 size=1920x30
```

It is called on `Opened` and again on the clock timer. Polling is deliberate: Avalonia raises no
notification when a monitor is unplugged or the scale factor changes, and the alternative is
`WM_DISPLAYCHANGE` P/Invoke for a bar that repaints every ten seconds anyway.

### `GetWindowRect` reports 39 px, not 30 px

Windows keeps an invisible resize border on a window even when decorations are off, so
`GetWindowRect` returns 39 for a 30-pixel bar. This is expected and is why
`tools/screenshot.ps1` grew a `-ByProcess` mode: a decorationless window has **no**
`MainWindowHandle` and no `MainWindowTitle`, so title matching cannot find it.

## Start menu

A second top-level window rather than a popup, so it can sit above the taskbar and above other
applications without being clipped to the taskbar's bounds. Toggled by the Start orb; closing it
via `IsVisible = false` rather than `Close()`, so the window object is reused and `OnExplicitShutdown`
keeps the taskbar alive.

The menu uses a `UniformGrid Columns="2"`, not `WrapPanel`. `WrapPanel` wraps only at the width it
is offered, and inside a `DockPanel` the `ItemsPanel` is offered unbounded width, so the menu
rendered as a single column. `UniformGrid` cannot regress that way.

## Launching the file manager

`ShellWindowManager` looks for `Conqueror.Net.exe` beside its own executable and in the two
usual development output folders, and returns `null` rather than guessing when it is not found.
Folder shortcuts open through `UseShellExecute`, so they land in Explorer rather than spawning
whatever application happens to be registered for `.txt`.

## Known limitations

- **Windows only.** Positioning uses `System.Windows.Forms.Screen` via Avalonia's screen API and
  the decorationless-window assumptions are Win32-specific. There is no macOS or Linux path.
- **Single monitor.** It pins to `Screens.Primary` only. Spanning displays, and docking to a side
  rather than the bottom, are not implemented.
- **No window buttons.** The taskbar has quick-launch and a clock, but no list of open windows,
  because that needs cross-process window enumeration (`EnumWindows` plus per-process title
  reads) and a decision about what counts as a task.
- **The real taskbar is still there.** Both are visible, and the real one is usually on top
  unless this process is restarted afterwards.
- **Duplicated theme values.** `Shell/Themes/Palette.axaml` repeats the Luna colours rather than
  sharing `Themes/Palette.axaml`, because the shell is a separate assembly. If one changes, the
  other must follow.