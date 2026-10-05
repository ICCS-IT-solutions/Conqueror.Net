# Conqueror.Net

A Windows XP–style file manager built with [Avalonia UI](https://avaloniaui.net), with tabs that
can hold either a **folder** or a **web page** rendered by an embedded **Chromium (CEF)** engine.

## What it does

- **Tabbed shell** — one window, any mix of folder tabs and browser tabs. `Ctrl+T` new web tab,
  `Ctrl+N` new folder tab, `Ctrl+W` close, `Ctrl+Tab` cycle.
- **One set of navigation controls for every tab.** The address bar, Back/Forward/Up and the
  status bar all drive whichever tab is active, whether it is showing a folder or a web page.
- **XP Explorer file pane** — Folders task pane (special folders + drives), four view modes
  (Icons / List / Details / Tiles), sortable Details columns, search-as-you-type, hidden-file
  toggle, per-tab back/forward history, and a status bar showing object count and free space.
- **Embedded Chromium** — real browser tabs powered by CEF 120, with zoom, DevTools, and
  `target=_blank` / `window.open` links opening as new tabs rather than new windows.
- **Windows XP "Luna" theming** — the original Luna palette (#ECE9D8 face, #316AC5 highlight,
  #0A246A→#A6CAF0 title gradient) applied through Avalonia styles, with vector icons so nothing
  rasterises or depends on platform icon caches.
- **SVG icon theme** — file, folder, drive and task-pane icons come from a scalable XP icon
  theme rendered by `Avalonia.Svg.Skia`, with per-extension mapping (PDF, Word, images, media,
  archives, executables) and a size per view mode. See [docs/ICON-THEME.md](docs/ICON-THEME.md),
  which also covers the licensing position.

## Running it

```powershell
dotnet run
```

You can also point it at a start location; a URL opens a browser tab, a path opens a folder tab:

```powershell
dotnet run -- https://news.ycombinator.com
dotnet run -- C:\Users\Iain\Documents
```

## Architecture

```
Conqueror.Net/
├── Core/Tabs/            ITabViewModel — the contract every tab kind implements
├── WindowRoot/           The shell: menu bar, toolbar, address bar, tab strip, status bar
├── FileBrowserUi/        Folder tabs
│   ├── Models/           FileSystemEntry, ShellFolder, view/sort enums
│   ├── Services/         IFileSystemService — all real disk access lives behind this
│   ├── ViewModels/       Navigation history, filtering, sorting (split into partials)
│   └── Views/
├── WebBrowserUi/         Browser tabs (Chromium)
├── Themes/               Palette.axaml, Icons.axaml, Luna.Controls.axaml
└── tools/                Screenshot / smoke-test helpers used during development
```

The key idea is `ITabViewModel`. `MainWindowViewModel` never knows whether a tab is a folder or a
web page — it binds to `Title`, `Location`, `CanGoBack`, `StatusText` and calls `Navigate`. Tab
content is resolved by Avalonia `DataTemplate` from the view-model type, so the single
`ContentControl` swaps between `FileBrowserView` and `BrowserView` transparently.

```
MainWindowViewModel ──owns──> ObservableCollection<ITabViewModel>
                                        ├── FileBrowserViewModel  (folder)
                                        └── BrowserTabViewModel   (Chromium)
```


## Technology choices

| Concern | Choice | Why |
| --- | --- | --- |
| UI framework | Avalonia 11.3.22 | Cross-platform; the scaffold was already Avalonia 11.0.10 |
| MVVM | CommunityToolkit.Mvvm 8.4.2 | Source generators match `AvaloniaUseCompiledBindingsByDefault` |
| Browser engine | WebViewControl-Avalonia 3.120.13 | Actively maintained wrapper over CefGlue (CEF 120), ships native binaries |
| SVG rendering | Avalonia.Svg.Skia 11.3.0 | MIT, tracks Avalonia 11.3, exposes `EnableCache` so one parsed vector serves every row |

### Why Avalonia 11 and not 12

Avalonia 12 is released, but `WebViewControl-Avalonia` requires `Avalonia >= 11.0.10` and its
Avalonia implementation is written against the 11.x API. Upgrading to 12 would break embedded
Chromium, so the project stays on 11.3.22 until the browser control supports it.

`Xilium.CefGlue` (CEF 75, last released 2019) and `CefSharp.Avalonia` (882 downloads, IPC-based)
were evaluated and rejected as stale or unproven.

### Why `global.json` exists

The .NET 10.0.401 SDK installed on this machine **cannot restore any NuGet project** — restore
reports `NU1503: Skipping restore for project ... Unable to find a project to restore!` for every
project, including unrelated ones. SDK 9.0.318 restores correctly, so `global.json` pins 9.0.318.
This is a machine-level problem, not a project one; see the troubleshooting note below.

## Known limitations

- **The title bar is the host OS's, not Luna.** Drawing a custom title bar
  (`ExtendClientAreaToDecorationsHint`) is the obvious next step for authenticity, but it was
  deliberately left off because Avalonia's extended client area is known to misplace the native
  child HWND that Chromium draws into. Worth attempting behind a flag.
- **File icons are generic.** Folders and files are distinguished by shape and colour, but there
  is no per-extension icon set. Real XP used per-type icons; adding them means shipping an icon
  cache or extracting shell icons via `SHGetFileInfo`.
- **Folder enumeration is synchronous.** Fine for normal folder sizes; a very large directory
  (100k+ entries) would block the UI thread.
- **Thumbnails view is aliased to Tiles.** The menu entry exists but uses the Tiles layout.
- **No file operations beyond "New Folder"** — no copy/move/delete/rename yet.
- **Chromium is x64 only.** The project pins `PlatformTarget=x64`; there is no ARM64 build.

## Development notes

Formatting uses [CSharpier](https://csharpier.com) (`.csharpierrc.json`, 100 columns):

```powershell
dotnet csharpier .
```

`tools/` contains helpers used to smoke-test the UI on Windows: `screenshot.ps1` captures a
window by handle, and `run-capture.ps1` launches the app, waits for its window, screenshots it
and reports any unhandled exception from stderr.

```powershell
pwsh -File tools/run-capture.ps1 -ProcessName 'Conqueror.Net' -AppArgs 'https://example.com'
```

### Swapping the icon theme

The upstream pack is not committed — `Icons_source/` is gitignored. To populate
`Assets/Icons/` from a theme on disk:

```powershell
pwsh -File tools/verify-icon-manifest.ps1 -SourceRoot .\Icons_source\<theme-dir>   # dry run
pwsh -File tools/import-icon-theme.ps1  -SourceRoot .\Icons_source\<theme-dir>     # copy
```

The import script dereferences the pack's symlinks, flattens names to stable semantic keys
and reports the size of each icon it copies. To use a different theme, edit the manifest in
`tools/import-icon-theme.ps1` and the extension map in `FileBrowserUi/Icons/FileIconResolver.cs`;
nothing else hard-codes an icon path. See [docs/ICON-THEME.md](docs/ICON-THEME.md).

### Troubleshooting

- **`NU1503` on restore** — you are on the broken .NET 10 SDK. Run `dotnet --version` in the
  project directory; it must report `9.0.318` thanks to `global.json`.
- **`dotnet csharpier` not found** — the global tool's shim is missing from
  `%USERPROFILE%\.dotnet\tools`. Repair with
  `dotnet tool uninstall --global csharpier && dotnet tool install --global csharpier`, or invoke
  the tool DLL directly from `%USERPROFILE%\.dotnet\tools\.store\...`.
- **Blank browser tab** — `BrowserView` attaches the view-model on `Loaded` *and*
  `DataContextChanged`. Do not bind `WebView.Address` in XAML: it starts navigation before those
  handlers are wired and the load callbacks are lost.
