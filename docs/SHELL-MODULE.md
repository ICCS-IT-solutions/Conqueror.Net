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

## Choosing the dock edge

The taskbar can be anchored to any of the four screen edges, and the choice is remembered
between runs. Right-click the taskbar → **Taskbar position**:

| Option | Behaviour |
| --- | --- |
| Bottom (default) | XP's traditional position |
| Top | |
| Left | Bar runs vertically, Start at the top |
| Right | |
| Follow the Windows taskbar | Tracks the real taskbar if you move it |

![Left-docked taskbar](screenshot-shell-left.png)

The setting lives in `%LOCALAPPDATA%\Conqueror.Net\shell-settings.json`, next to the file
manager's CEF cache:

```json
{
  "Edge": "Bottom",
  "MenuFollowsBar": true
}
```

It is written the moment you choose, not on exit, so the preference survives the shell being
killed. A missing or malformed file falls back to `Bottom` — XP's position — rather than
preventing the shell from starting.

`--edge bottom|top|left|right|windows` overrides the stored value for a single run without
saving it, which is how each position is screenshotted.

### Following the Windows taskbar

`MatchWindows` infers the real taskbar's edge by comparing the screen's full bounds with its
working area, and is re-evaluated on the clock tick — so moving the Windows taskbar moves this
one too. Comparing the two rectangles rather than asking the shell directly also accounts for
a third-party bar.

### Verifying all four edges

```powershell
pwsh -File tools/test-taskbar-edges.ps1           # assert placement
pwsh -File tools/test-taskbar-edges.ps1 -Capture  # assert and screenshot each edge
```

It launches the shell once per edge and compares the resulting window rectangle against the
primary screen. All four pass:

```
OK   bottom  B     =1049  want=1080  delta=31   rect=0,1010 1920x39
OK   top     T     =0     want=0     delta=0    rect=0,0 1920x39
OK   left    L     =0     want=0     delta=0    rect=0,0 32x1080
OK   right   R     =1922  want=1920  delta=2    rect=1890,0 32x1080
```

The `bottom` delta is not an error. `GetWindowRect` reports the window's **outer frame**, and
Windows keeps an invisible resize border on a decorationless window; at the bottom edge that
frame sits *above* the content, so a bar requested at `y=1050` reports `1010..1049`. Left and
top report no offset, right reports 2 px. The test asserts the bar is flush with its edge
within the frame (40 px, deliberately generous) rather than hard-coding per-side offsets that
would change with DPI or Windows theme.

**No frame compensation is applied in the code.** That was tried and reverted: Avalonia's
`Position` already refers to the content origin, so subtracting `Window.FrameSize` pulls the
bar *away* from the edge and measurably opened a 30 px gap.

### How the layout follows the edge

One template serves all four edges. The view-model exposes the orientation-dependent values and
the XAML binds to them:

| Property | Horizontal | Vertical |
| --- | --- | --- |
| `StartDock` / `QuickLaunchDock` | `Dock.Left` | `Dock.Top` |
| `TrayDock` | `Dock.Right` | `Dock.Bottom` |
| `StartWidth` × `StartHeight` | 54 × 30 | 30 × 54 |
| `TrayOrientation` / `QuickLaunchOrientation` | Horizontal | Vertical |
| `OrbRotation` | 0° | −90° |

`DockPanel.Dock` is a bindable attached property, which is what lets a single `DockPanel` change
shape without a second layout. Derived properties are re-announced together in
`ApplyResolvedEdge`, because the source generator only raises change notification for
`IsHorizontal` itself.

Two things were wrong on the first vertical run and are fixed:

- The four quick-launch icons were laid out horizontally and squeezed into a 30 px bar, showing
  only the first one.
- The Start orb stretched to fill its 30 × 54 button and rendered as an ellipse. It is now a
  fixed 28 × 28 circle in both orientations, and only the *label* is rotated — rotating the orb
  itself distorts the gradient.

A `RotateTransform` **object** is required, not `RenderTransform="rotate({Binding ...})"`.
`TransformParser` parses that attribute at XAML-compile time, so a binding inside the string
throws `Invalid unit: {Binding OrbRotation}` and takes the whole window down.

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