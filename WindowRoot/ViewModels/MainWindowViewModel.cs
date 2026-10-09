using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Conqueror.Net.CodeEditorUi.ViewModels;
using Conqueror.Net.Core;
using Conqueror.Net.Core.Tabs;
using Conqueror.Net.Core.Terminal;
using Conqueror.Net.FileBrowserUi.Models;
using Conqueror.Net.FileBrowserUi.Services;
using Conqueror.Net.FileBrowserUi.Services.Vfs;
using Conqueror.Net.FileBrowserUi.ViewModels;
using Conqueror.Net.WebBrowserUi.ViewModels;

namespace Conqueror.Net.WindowRoot.ViewModels;

/// <summary>
/// Owns the tab strip and the shared navigation chrome. Every tab kind is driven through
/// <see cref="ITabViewModel"/>, so one address bar and one set of Back/Forward/Up buttons
/// serve both the file browser and the embedded Chromium browser.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private const string DefaultStartPage = "https://www.google.com";

    private readonly IFileSystemService _fileSystem;


    /// <summary>The tab whose property changes currently drive the window chrome.</summary>
    private ITabViewModel? _subscribedTab;

    [ObservableProperty]
    private ITabViewModel? _selectedTab;

    //Common edit actions. These route to whichever tab is active: file tabs (and
    //dual-pane via its active pane) get real file operations, browser tabs get the CEF
    //edit commands, and terminal tabs get input-box editing. CanExecute consults the
    //tab's Can* capabilities; NewFile/NewFolder are additionally guarded to file tabs.
    public RelayCommand NewFileCommand => new(
        () => ActiveFileTab?.NewFileCommand.Execute(null),
        () => ActiveFileTab is not null);

    public RelayCommand NewFolderCommand => new(
        () => ActiveFileTab?.NewFolderCommand.Execute(null),
        () => ActiveFileTab is not null);

    public RelayCommand CutCommand => new(
        () => DispatchEdit(t => t.Cut()),
        () => CanDispatchEdit(t => t.CanCut));

    public RelayCommand CopyCommand => new(
        () => DispatchEdit(t => t.Copy()),
        () => CanDispatchEdit(t => t.CanCopy));

    public RelayCommand PasteCommand => new(
        () => DispatchEdit(t => t.Paste()),
        () => CanDispatchEdit(t => t.CanPaste));

    public RelayCommand DeleteCommand => new(
        () => DispatchEdit(t => t.Delete()),
        () => CanDispatchEdit(t => t.CanDelete));

    public RelayCommand RenameCommand => new(
        () => ActiveFileTab?.RenameCommand.Execute(null),
        () => ActiveFileTab is not null);

    public RelayCommand PropertiesCommand => new(
        () => ActiveFileTab?.ShowPropertiesCommand.Execute(null),
        () => ActiveFileTab is not null);

    public RelayCommand SelectAllCommand => new(
        () => DispatchEdit(t => t.SelectAll()),
        () => CanDispatchEdit(t => t.CanSelectAll));

    public RelayCommand SelectNoneCommand => new(
        () => DispatchEdit(t => (t as FileEditTarget)?.SelectNone()),
        () => CanDispatchEdit(t => t.CanSelectNone));

    public RelayCommand InvertSelectCommand => new(
        () => DispatchEdit(t => (t as FileEditTarget)?.InvertSelect()),
        () => CanDispatchEdit(t => t.CanInvertSelect));

    /// <summary>
    /// Minimal edit-command surface every tab kind supports. Implemented as a private
    /// interface over the four tab VMs so the shell dispatches without a type-switch per
    /// verb; file-only verbs (new file/folder, rename, properties) stay on
    /// <see cref="ActiveFileTab"/> instead.
    /// </summary>
    private interface IEditTarget
    {
        void Cut();

        void Copy();

        void Paste();

        void Delete();

        void SelectAll();
    }

    private sealed class FileEditTarget(FileBrowserViewModel vm) : IEditTarget
    {
        public void Cut() => vm.CutCommand.Execute(null);

        public void Copy() => vm.CopyCommand.Execute(null);

        public void Paste() => vm.PasteCommand.Execute(null);

        public void Delete() => vm.DeleteCommand.Execute(null);

        public void SelectAll() => vm.SelectAllCommand.Execute(null);
        public void SelectNone() => vm.SelectNoneCommand.Execute(null);
        public void InvertSelect() => vm.InvertSelectCommand.Execute(null);
    }

    private sealed class BrowserEditTarget(BrowserTabViewModel vm) : IEditTarget
    {
        public void Cut() => vm.CutPageSelection();

        public void Copy() => vm.CopyPageSelection();

        public void Paste() => vm.PasteIntoPage();

        public void Delete() => vm.DeletePageSelection();

        public void SelectAll() => vm.SelectAllInPage();
    }

    private sealed class TerminalEditTarget(TerminalViewModel vm) : IEditTarget
    {
        public void Cut() => vm.CutInputCommand.Execute(null);

        public void Copy() => vm.CopyInputCommand.Execute(null);

        public void Paste() => vm.PasteInputCommand.Execute(null);

        public void Delete() => vm.DeleteInputCommand.Execute(null);

        public void SelectAll() => vm.SelectAllInputCommand.Execute(null);
    }

    private sealed class EditorEditTarget(CodeEditorViewModel vm) : IEditTarget
    {
        public void Cut() => vm.CutCommand.Execute(null);

        public void Copy() => vm.CopyCommand.Execute(null);

        public void Paste() => vm.PasteCommand.Execute(null);

        public void Delete() => vm.DeleteCommand.Execute(null);

        public void SelectAll() => vm.SelectAllCommand.Execute(null);
    }

    /// <summary>
    /// The file tab the creation verbs act on: a plain file tab directly, or a dual-pane
    /// tab's active pane. Null everywhere else, which disables the menu items.
    /// </summary>
    private FileBrowserViewModel? ActiveFileTab => SelectedTab switch
    {
        FileBrowserViewModel file => file,
        SplitPaneViewModel split => split.ActivePane,
        _ => null,
    };

    private IEditTarget? ActiveEditTarget => SelectedTab switch
    {
        FileBrowserViewModel file => new FileEditTarget(file),
        SplitPaneViewModel split when split.ActivePane is not null => new FileEditTarget(split.ActivePane),
        BrowserTabViewModel browser => new BrowserEditTarget(browser),
        TerminalViewModel terminal => new TerminalEditTarget(terminal),
        CodeEditorViewModel editor => new EditorEditTarget(editor),
        _ => null,
    };

    private void DispatchEdit(Action<IEditTarget> verb)
    {
        var target = ActiveEditTarget;
        if (target is not null)
        {
            verb(target);
        }
    }

    private bool CanDispatchEdit(Func<ITabViewModel, bool> capability) =>
        SelectedTab is not null && capability(SelectedTab) && ActiveEditTarget is not null;

    /// <summary>Text in the address bar. Kept in sync with the active tab's location.</summary>
    [ObservableProperty]
    private string _addressText = string.Empty;

    public MainWindowViewModel(IFileSystemService? fileSystem = null)
    {
        _fileSystem = fileSystem ?? new FileSystemService();

        // Always start with a folder tab, the way Explorer opens.
        AddFileTab();
    }

    /// <summary>
    /// Opens the command-line target in the best tab for it: a browser tab for anything
    /// with a URL scheme, otherwise a folder tab navigated to that path. Archive schemes
    /// (zip:, tar:) are treated as folder tabs even though they contain "://".
    /// </summary>
    public void OpenStartupTarget(string target)
    {
        var location = VfsLocation.Parse(target);

        if (location.IsLocal)
        {
            // No scheme, or "file:" — local path
        }
        else if (VfsLocation.IsSupportedScheme(location.Scheme))
        {
            // zip: or tar: — browse as folder
        }
        else if (target.Contains("://", StringComparison.Ordinal)
            || target.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
        {
            AddWebTab(target);
            return;
        }
        else
        {
            // Unknown scheme — try folder tab anyway (the service will report unsupported)
        }

        if (Tabs.OfType<FileBrowserViewModel>().FirstOrDefault() is { } fileTab)
        {
            fileTab.NavigateToCommand.Execute(target);
        }
        else
        {
            AddFileTab();
        }
    }

    public ObservableCollection<ITabViewModel> Tabs { get; } = [];

    public string WindowTitle =>
        SelectedTab is null ? "Conqueror.Net" : $"{SelectedTab.Title} - Conqueror.Net";

    // ---- Tab management ------------------------------------------------------

    [RelayCommand]
    private void AddFileTab()
    {
        var vm = new FileBrowserViewModel(_fileSystem);
        vm.OpenFileRequest += OnFileBrowserOpenFileRequest;
        vm.EditFileRequest += OnFileBrowserEditFileRequest;
        AttachTab(vm);
    }

    [RelayCommand]
    private void AddWebTab(string? address = null)
    {
        // CEF is Windows-only. Without this guard the WebView control throws during its own
        // constructor and takes the window down, so the browser tab is simply not offered
        // where the engine cannot run.
        if (!App.ChromiumAvailable)
        {
            App.Log?.Invoke("Browser tab is unavailable: the CEF engine could not be started.");
            return;
        }

        var vm = new BrowserTabViewModel(address ?? DefaultStartPage);
        void OnNewTab(string url) => AddWebTab(url);
        vm.NewTabRequested += OnNewTab;
        // Unsubscribe the popup handler when the tab is closed, otherwise the dead VM
        // (and its WebView) stays rooted by the event and leaks.
        vm.Disposed += () => vm.NewTabRequested -= OnNewTab;
        AttachTab(vm);
    }

    /// <summary>
    /// Opens a dual-pane "orthodox" tab. The right pane starts on the parent of the left, which
    /// is the arrangement a dual-pane manager is for: source one side, destination the other.
    /// </summary>
    [RelayCommand]
    private void AddSplitPaneTab(string? initialPath = null)
    {
        var vm = new SplitPaneViewModel(_fileSystem, initialPath);
        vm.OpenFileRequest += OnFileBrowserOpenFileRequest;
        vm.EditFileRequest += OnFileBrowserEditFileRequest;
        AttachTab(vm);
    }

    /// <summary>Opens a terminal tab, started in the active folder tab's folder.</summary>
    [RelayCommand]
    private void AddTerminalTab()
    {
        var start = Tabs.OfType<FileBrowserViewModel>().FirstOrDefault()?.CurrentPath;

        var vm = new TerminalViewModel(start);
        AttachTab(vm);
    }

    /// <summary>Opens a terminal tab with the specified shell.</summary>
    [RelayCommand]
    private void AddTerminalTabWithShell(string shellId)
    {
        var start = Tabs.OfType<FileBrowserViewModel>().FirstOrDefault()?.CurrentPath;
        ITerminalBackend backend;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            backend = new PtyProcessBackend(start, shellId);
        }
        else
        {
            backend = new PipedProcessBackend(start, shellId);
        }
        var vm = new TerminalViewModel(start, backend);
        AttachTab(vm);
    }

    /// <summary>Opens a terminal tab with bash.</summary>
    [RelayCommand]
    private void AddTerminalTabWithBash()
    {
        var start = Tabs.OfType<FileBrowserViewModel>().FirstOrDefault()?.CurrentPath;
        // Only add if bash is available
        if (TerminalShellRegistry.AvailableShells.Any(s => s.Id == "bash"))
        {
            ITerminalBackend backend;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                backend = new PtyProcessBackend(start, "bash");
            }
            else
            {
                backend = new PipedProcessBackend(start, "bash");
            }
            var vm = new TerminalViewModel(start, backend);
            AttachTab(vm);
        }
        else
        {
            // Fallback to regular terminal
            var vm = new TerminalViewModel(start);
            AttachTab(vm);
        }
    }

    /// <summary>Opens a terminal tab with zsh.</summary>
    [RelayCommand]
    private void AddTerminalTabWithZsh()
    {
        var start = Tabs.OfType<FileBrowserViewModel>().FirstOrDefault()?.CurrentPath;
        // Only add if zsh is available
        if (TerminalShellRegistry.AvailableShells.Any(s => s.Id == "zsh"))
        {
            ITerminalBackend backend;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                backend = new PtyProcessBackend(start, "zsh");
            }
            else
            {
                backend = new PipedProcessBackend(start, "zsh");
            }
            var vm = new TerminalViewModel(start, backend);
            AttachTab(vm);
        }
        else
        {
            // Fallback to regular terminal
            var vm = new TerminalViewModel(start);
            AttachTab(vm);
        }
    }

    /// <summary>Opens a terminal tab with fish.</summary>
    [RelayCommand]
    private void AddTerminalTabWithFish()
    {
        var start = Tabs.OfType<FileBrowserViewModel>().FirstOrDefault()?.CurrentPath;
        // Only add if fish is available
        if (TerminalShellRegistry.AvailableShells.Any(s => s.Id == "fish"))
        {
            ITerminalBackend backend;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                backend = new PtyProcessBackend(start, "fish");
            }
            else
            {
                backend = new PipedProcessBackend(start, "fish");
            }
            var vm = new TerminalViewModel(start, backend);
            AttachTab(vm);
        }
        else
        {
            // Fallback to regular terminal
            var vm = new TerminalViewModel(start);
            AttachTab(vm);
        }
    }

    /// <summary>Opens a terminal tab with a choice of shell.</summary>
    [RelayCommand]
    private async Task AddTerminalTabWithShellChoice()
    {
        var start = Tabs.OfType<FileBrowserViewModel>().FirstOrDefault()?.CurrentPath;
        // Get available shells for the current platform
        var availableShells = TerminalShellRegistry.AvailableShells;
        
        // For now, we'll just use the first available shell as an example
        // In a real implementation, this would show a dialog for the user to choose
        if (availableShells.Count > 0)
        {
            var selectedShell = availableShells[0]; // Just use first one for demo
            ITerminalBackend backend;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                backend = new PtyProcessBackend(start, selectedShell.Id);
            }
            else
            {
                backend = new PipedProcessBackend(start, selectedShell.Id);
            }
            var vm = new TerminalViewModel(start, backend);
            AttachTab(vm);
        }
        else
        {
            // Fallback to default
            var vm = new TerminalViewModel(start);
            AttachTab(vm);
        }
    }

    /// <summary>Opens a code/config editor tab, optionally on an existing file.</summary>
    [RelayCommand]
    private void AddEditorTab(string? path = null)
    {
        var fullPath = NormalizePath(path);

        // Reuse an editor that already has the file open rather than stacking a second
        // buffer over it: two dirty tabs on one path would let the last save win silently.
        if (fullPath is not null)
        {
            var existing = Tabs.OfType<CodeEditorViewModel>().FirstOrDefault(t =>
                string.Equals(NormalizePath(t.FilePath), fullPath, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                SelectedTab = existing;
                return;
            }
        }

        AttachTab(new CodeEditorViewModel(fullPath));
    }

    /// <summary>Absolute form of a path for comparisons, or null when it cannot be parsed.</summary>
    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private void AttachTab(ITabViewModel tab)
    {
        Tabs.Add(tab);
        SelectedTab = tab;
        SyncAddressFromTab();
    }

    [RelayCommand]
    private async Task CloseTabAsync(ITabViewModel? tab)
    {
        var target = tab ?? SelectedTab;
        if (target is null)
        {
            return;
        }

        // The editor prompts on dirty buffers; cancelling leaves the tab in place.
        if (target is CodeEditorViewModel editor && !await ConfirmEditorCloseAsync(editor))
        {
            return;
        }

        var index = Tabs.IndexOf(target);
        Tabs.Remove(target);

        // A terminal owns a child process, so closing the tab has to take the shell with it.
        // Without this the shell would outlive its tab and keep running invisibly.
        if (target is IDisposable disposable)
        {
            disposable.Dispose();
        }

        if (ReferenceEquals(SelectedTab, target))
        {
            SelectedTab = Tabs.Count == 0 ? null : Tabs[Math.Clamp(index, 0, Tabs.Count - 1)];
        }

        if (Tabs.Count == 0)
        {
            // Explorer and Chrome both guarantee at least one window's worth of tabs.
            AddFileTab();
        }
    }

    [RelayCommand]
    private async Task CloseOtherTabsAsync()
    {
        if (SelectedTab is null)
        {
            return;
        }

        foreach (var tab in Tabs.Where(t => !ReferenceEquals(t, SelectedTab)).ToList())
        {
            // Same guard as CloseTab: a dirty editor buffer prompts, and declining keeps
            // that tab open rather than throwing the edits away.
            if (tab is CodeEditorViewModel editor && !await ConfirmEditorCloseAsync(editor))
            {
                continue;
            }

            Tabs.Remove(tab);

            // Same as CloseTab: a WebView owns a CEF browser + HWND and must be torn
            // down, otherwise every "close other tabs" leaks a renderer.
            if (tab is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    [RelayCommand]
    private void NextTab()
    {
        if (Tabs.Count == 0)
        {
            return;
        }

        var index = SelectedTab is null ? 0 : Tabs.IndexOf(SelectedTab);
        SelectedTab = Tabs[(index + 1) % Tabs.Count];
    }

    [RelayCommand]
    private void PreviousTab()
    {
        if (Tabs.Count == 0)
        {
            return;
        }

        var index = SelectedTab is null ? 0 : Tabs.IndexOf(SelectedTab);
        SelectedTab = Tabs[(index - 1 + Tabs.Count) % Tabs.Count];
    }

    // ---- Shared navigation ---------------------------------------------------

    [RelayCommand]
    private void Navigate()
    {
        if (SelectedTab is not null && !string.IsNullOrWhiteSpace(AddressText))
        {
            SelectedTab.Navigate(AddressText);
        }
    }

    [RelayCommand]
    private void GoBack() => SelectedTab?.GoBack();

    [RelayCommand]
    private void GoForward() => SelectedTab?.GoForward();

    [RelayCommand]
    private void Refresh() => SelectedTab?.Reload();

    [RelayCommand]
    private void Stop() => SelectedTab?.Stop();

    // ---- Zoom commands (browser-tab only; delegate to the tab so the menu and the
    // in-view +/- buttons share one step/clamp path) ----

    [RelayCommand]
    private void ZoomIn()
    {
        if (SelectedTab is BrowserTabViewModel browser)
        {
            browser.ZoomInCommand.Execute(null);
        }
    }

    [RelayCommand]
    private void ZoomOut()
    {
        if (SelectedTab is BrowserTabViewModel browser)
        {
            browser.ZoomOutCommand.Execute(null);
        }
    }

    [RelayCommand]
    private void ZoomReset()
    {
        if (SelectedTab is BrowserTabViewModel browser)
        {
            browser.ZoomResetCommand.Execute(null);
        }
    }

    // ---- Developer tools (browser-tab only)

    [RelayCommand]
    private void ShowDeveloperTools()
    {
        if (SelectedTab is BrowserTabViewModel browser)
        {
            browser.ShowDeveloperTools();
        }
    }

    // ---- Extensions (browser-tab only)

    [RelayCommand]
    private void Extensions()
    {
        if (SelectedTab is BrowserTabViewModel browser)
        {
            browser.ExtensionsCommand.Execute(null);
        }
    }

    /// <summary>Explorer-only: goes to the parent folder of a file tab.</summary>
    [RelayCommand]
    private void GoUp()
    {
        if (SelectedTab is FileBrowserViewModel { CanGoUp: true } file)
        {
            file.GoUpCommand.Execute(null);
        }
    }

    // ---- Address bar mode ----------------------------------------------------

    // The backing field is _addressDisplay rather than _addressBarMode because the MVVM source
    // generator would derive a property called AddressBarMode from it, colliding with the enum
    // of that name.
    [ObservableProperty]
    private AddressBarMode _addressDisplay = AddressBarMode.Path;

    /// <summary>
    /// True when the "Address:" label should behave as a toggle. Only folder tabs have both a
    /// path and a breadcrumb form to switch between, so on a web tab the label stays a plain
    /// caption and the box stays editable.
    /// </summary>
    public bool IsAddressLabelToggleable => SelectedTab?.IsFileBrowser ?? false;

    /// <summary>
    /// Breadcrumb segments of the active folder tab, or an empty list otherwise. Exposed from
    /// here rather than bound through the tab because the address bar belongs to the shell.
    /// </summary>
    public ObservableCollection<BreadcrumbSegment> AddressBreadcrumbs
    {
        get
        {
            var folder = SelectedTab as FileBrowserViewModel;

            // A dual-pane tab shows the breadcrumbs of whichever pane is in front.
            if (folder is null && SelectedTab is SplitPaneViewModel split)
            {
                folder = split.ActivePane;
            }

            return folder?.Breadcrumbs ?? _noBreadcrumbs;
        }
    }

    /// <summary>Shared empty collection, so the getter never returns null for the binding.</summary>
    private static readonly ObservableCollection<BreadcrumbSegment> _noBreadcrumbs = [];

    /// <summary>
    /// Toggles between the editable path and the breadcrumb row. Called by the address label,
    /// which is why the label is the control the user clicks.
    /// </summary>
    [RelayCommand]
    private void ToggleAddressBarMode()
    {
        if (!IsAddressLabelToggleable)
        {
            return;
        }

        AddressDisplay =
            AddressDisplay == AddressBarMode.Path
                ? AddressBarMode.Breadcrumbs
                : AddressBarMode.Path;
    }

    /// <summary>True when the address bar should show the editable path box.</summary>
    public bool IsPathModeVisible =>
        AddressDisplay == AddressBarMode.Path || !IsAddressLabelToggleable;

    /// <summary>True when the address bar should show the breadcrumb row.</summary>
    public bool IsBreadcrumbModeVisible =>
        AddressDisplay == AddressBarMode.Breadcrumbs && IsAddressLabelToggleable;

    /// <summary>
    /// Both visibility properties are computed from <see cref="AddressDisplay"/>, so changing it
    /// raises change notifications for them as well.
    /// </summary>
    /// <remarks>
    /// Without this the toggle button flips <see cref="AddressDisplay"/> but the two bound
    /// <c>IsVisible</c> properties are never re-evaluated, so the address bar appears to do
    /// nothing when clicked. The generator only notifies for the property it generated.
    /// </remarks>
    partial void OnAddressDisplayChanged(AddressBarMode value)
    {
        OnPropertyChanged(nameof(IsPathModeVisible));
        OnPropertyChanged(nameof(IsBreadcrumbModeVisible));
    }

    /// <summary>
    /// Navigates to a clicked breadcrumb segment. Routed through the active tab so a dual-pane
    /// tab moves the pane that is in front.
    /// </summary>
    [RelayCommand]
    private void NavigateToAddressBreadcrumb(BreadcrumbSegment? segment)
    {
        if (segment is null)
        {
            return;
        }

        if (SelectedTab is SplitPaneViewModel split)
        {
            split.ActivePane?.NavigateToBreadcrumbCommand.Execute(segment);
            return;
        }

        if (SelectedTab is FileBrowserViewModel file)
        {
            file.NavigateToBreadcrumbCommand.Execute(segment);
        }
    }

    private void SyncAddressFromTab() => AddressText = SelectedTab?.Location ?? string.Empty;

    partial void OnSelectedTabChanged(ITabViewModel? value)
    {
        // The active tab owns the window title, and a browser tab renames itself as pages
        // load, so keep listening while it stays selected.
        if (_subscribedTab is not null)
        {
            _subscribedTab.PropertyChanged -= OnSelectedTabPropertyChanged;
        }

        _subscribedTab = value;

        if (_subscribedTab is not null)
        {
            _subscribedTab.PropertyChanged += OnSelectedTabPropertyChanged;
        }

        SyncAddressFromTab();
        OnPropertyChanged(nameof(WindowTitle));
        RefreshChrome();

        // Contextual menus re-evaluate against the newly selected tab.
        OnPropertyChanged(nameof(IsSplitPaneActive));
        OnPropertyChanged(nameof(IsWebBrowserActive));
        OnPropertyChanged(nameof(IsTerminalActive));
        OnPropertyChanged(nameof(IsEditorActive));
        OnPropertyChanged(nameof(ActiveSplitPane));

        // The address label is only a toggle for a folder tab, and the breadcrumb row is
        // rebuilt from whichever folder tab just became active.
        OnPropertyChanged(nameof(IsAddressLabelToggleable));
        OnPropertyChanged(nameof(IsPathModeVisible));
        OnPropertyChanged(nameof(IsBreadcrumbModeVisible));
        OnPropertyChanged(nameof(AddressBreadcrumbs));
    }

    private void OnSelectedTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ITabViewModel.Title))
        {
            OnPropertyChanged(nameof(WindowTitle));
        }
        else if (e.PropertyName is nameof(ITabViewModel.Location))
        {
            // The tab navigated on its own (a folder click, a redirect, a link); pull the
            // address bar back into step unless the user is mid-edit in it.
            SyncAddressFromTab();
        }
        else if (e.PropertyName is nameof(ITabViewModel.StatusText))
        {
            OnPropertyChanged(nameof(ActiveStatusText));
        }

        // The menu bar is contextual, so switching tabs has to re-evaluate which menus apply.
        OnPropertyChanged(nameof(IsSplitPaneActive));
        OnPropertyChanged(nameof(IsTerminalActive));
        OnPropertyChanged(nameof(ActiveSplitPane));
    }

    /// <summary>Status-bar text of the active tab, surfaced for the shell's status bar.</summary>
    public string ActiveStatusText => SelectedTab?.StatusText ?? string.Empty;

    private void RefreshChrome()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(CanGoUp));
        OnPropertyChanged(nameof(CanNewFile));
        OnPropertyChanged(nameof(CanNewFolder));
        OnPropertyChanged(nameof(CanCut));
        OnPropertyChanged(nameof(CanCopy));
        OnPropertyChanged(nameof(CanPaste));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanRename));
        OnPropertyChanged(nameof(CanProperties));
        OnPropertyChanged(nameof(CanSelectAll));
    }

    public bool CanGoBack => SelectedTab?.CanGoBack ?? false;

    public bool CanGoForward => SelectedTab?.CanGoForward ?? false;

    //New: capabilities for new file, new folder, cut, copy, paste, delete, rename, properties, and select all.
    //For now hard-coding them to the file browser tab, but will add these as contextual capabilities for other tabs in the future.
    public bool CanNewFile => SelectedTab?.CanNewFile ?? false;
    public bool CanNewFolder => SelectedTab?.CanNewFolder ?? false;
    public bool CanCut => SelectedTab?.CanCut ?? false;
    public bool CanCopy => SelectedTab?.CanCopy ?? false;
    public bool CanPaste => SelectedTab?.CanPaste ?? false;
    public bool CanDelete => SelectedTab?.CanDelete ?? false;
    public bool CanRename => SelectedTab?.CanRename ?? false;
    public bool CanProperties => SelectedTab?.CanProperties ?? false;
    public bool CanSelectAll => SelectedTab?.CanSelectAll ?? false;
    public bool CanSelectNone => SelectedTab?.CanSelectNone ?? false;
    public bool CanInvertSelect => SelectedTab?.CanInvertSelect ?? false;

    public bool CanGoUp => SelectedTab is FileBrowserViewModel { CanGoUp: true }
        || SelectedTab is SplitPaneViewModel { CanGoUp: true };

    // ---- Contextual menu state -----------------------------------------------

    /// <summary>
    /// True when the active tab is an orthodox dual-pane tab. The View menu uses this to swap
    /// in the pane-specific items rather than showing them where they would do nothing.
    /// </summary>
    public bool IsSplitPaneActive => SelectedTab is SplitPaneViewModel;
    public bool IsWebBrowserActive => SelectedTab is BrowserTabViewModel;

    /// <summary>The dual-pane tab currently in front, or null.</summary>
    public SplitPaneViewModel? ActiveSplitPane => SelectedTab as SplitPaneViewModel;

    /// <summary>True when the active tab is a terminal, which hides the file-only menus.</summary>
    public bool IsTerminalActive => SelectedTab is TerminalViewModel;

    /// <summary>True when the active tab is the in-process code/config editor.</summary>
    public bool IsEditorActive => SelectedTab is CodeEditorViewModel;

    /// <summary>Saves the active editor tab; the File menu's Save and Ctrl+S route here.</summary>
    [RelayCommand]
    private void SaveActiveEditor()
    {
        if (SelectedTab is CodeEditorViewModel editor)
        {
            editor.SaveCommand.Execute(null);
        }
    }

    /// <summary>
    /// Window-supplied "Save / Don't save / Cancel" prompt for closing a dirty editor tab.
    /// Set by the shell window on load; null headless, where closes proceed (tests).
    /// </summary>
    public Func<CodeEditorViewModel, Task<bool>>? ConfirmEditorClose { get; set; }

    private async Task<bool> ConfirmEditorCloseAsync(CodeEditorViewModel editor)
    {
        if (!editor.IsDirty)
        {
            return true;
        }

        if (ConfirmEditorClose is { } prompt)
        {
            return await prompt(editor);
        }

        // No view hook (headless): fall back to the editor's own guard, which proceeds
        // when it has nowhere to show a dialog.
        return await editor.ConfirmCloseAsync();
    }

    /// <summary>Swaps the two panes of the active dual-pane tab.</summary>
    [RelayCommand]
    private void SwapPanes()
    {
        if (SelectedTab is SplitPaneViewModel split)
        {
            split.SwapPanesCommand.Execute(null);
        }
    }

    /// <summary>Gives the split ratio back to an even 50/50.</summary>
    [RelayCommand]
    private void ResetSplit()
    {
        if (SelectedTab is SplitPaneViewModel split)
        {
            split.SplitRatio = 0.5;
        }
    }

    // ---- Opening files -------------------------------------------------------

    private void OnFileBrowserOpenFileRequest(FileSystemEntry entry)
    {
        // An archive (.zip, .tar, .tar.gz, .tgz) is browsed by navigating into it,
        // not by handing it to the OS. That is the KIO behaviour: an archive is a folder.
        if (!entry.IsDirectory && IsArchiveEntry(entry))
        {
            SelectedTab?.Navigate(entry.FullPath);
            return;
        }

        // A web shortcut or a local .html file belongs in a browser tab; text and config
        // files go to the in-process editor, and everything else is handed to whatever
        // the user has associated with it.
        if (TryGetWebAddress(entry, out var url))
        {
            AddWebTab(url);
            return;
        }

        // VFS paths (zip:/..., tar:/...) are not real files. Materialise them to a temp
        // location first so the editor and the OS "open" routing see a genuine file.
        var pathToOpen = entry.FullPath;
        var isVirtual = VfsLocation.Parse(entry.FullPath).IsLocal is false;

        if (isVirtual)
        {
            var materialized = App.FileSystem.TryMaterialize(entry.FullPath);
            if (!materialized.Success)
            {
                App.Log?.Invoke($"Could not open '{entry.Name}': {materialized.Error}");
                return;
            }

            pathToOpen = materialized.TempPath!;
        }

        if (!entry.IsDirectory && EditorFileTypes.Contains(entry.Extension))
        {
            AddEditorTab(pathToOpen);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(pathToOpen) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // Nothing is registered for this type, or the OS refused to launch it.
            App.Log?.Invoke($"Could not open '{entry.Name}': {ex.Message}");
        }
        finally
        {
            // Temp files extracted from archives are cleaned up when the app exits;
            // we don't delete them immediately because the launched process may still
            // need them. A proper temp-file manager could track this, but for now the
            // OS temp directory is cleaned on reboot.
        }
    }

    /// <summary>
    /// True when <paramref name="entry"/> is a supported archive format that we can browse
    /// as a virtual folder. Mirrors the schemes in <see cref="VfsLocation.IsSupportedScheme"/>.
    /// </summary>
    private static bool IsArchiveEntry(FileSystemEntry entry) =>
        !entry.IsDirectory
        && (entry.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
            || entry.Extension.Equals(".tar", StringComparison.OrdinalIgnoreCase)
            || entry.Extension.Equals(".tar.gz", StringComparison.OrdinalIgnoreCase)
            || entry.Extension.Equals(".tgz", StringComparison.OrdinalIgnoreCase));

    private void OnFileBrowserEditFileRequest(FileSystemEntry entry)
    {
        // The Edit verb answers with the in-process editor directly: the pane has already
        // checked the extension, and the user asked for the editor explicitly, so the
        // web-tab and shell routing above does not apply.
        if (!entry.IsDirectory && EditorFileTypes.Contains(entry.Extension))
        {
            AddEditorTab(entry.FullPath);
        }
    }

    /// <summary>
    /// After Tools ▸ Editor File Types changes the shared extension set, ask every live
    /// file pane to re-announce its Edit verb's grey state — the selection that drives it
    /// has not changed, so the binding would otherwise keep the old answer until the next
    /// click. Reading the set itself (double-click routing, EditSelected) needs no nudge:
    /// those consult <see cref="EditorFileTypes.Contains"/> at fire time.
    /// </summary>
    public void NotifyEditorFileTypesChanged()
    {
        foreach (var tab in Tabs)
        {
            switch (tab)
            {
                case FileBrowserViewModel pane:
                    pane.NotifyEditorFileTypesChanged();
                    break;
                case SplitPaneViewModel split:
                    split.NotifyEditorFileTypesChanged();
                    break;
            }
        }
    }

    private static bool TryGetWebAddress(FileSystemEntry entry, out string address)
    {
        address = string.Empty;

        if (entry.IsDirectory)
        {
            return false;
        }

        var extension = entry.Extension;

        if (extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
        {
            var url = ReadInternetShortcut(entry.FullPath);
            if (url is not null)
            {
                address = url;
                return true;
            }
        }

        if (
            extension.Equals(".htm", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
        )
        {
            address = new Uri(entry.FullPath).AbsoluteUri;
            return true;
        }

        return false;
    }

    /// <summary>Pulls the URL out of a Windows .url internet shortcut file.</summary>
    private static string? ReadInternetShortcut(string path)
    {
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                {
                    var url = line[4..].Trim();
                    return string.IsNullOrEmpty(url) ? null : url;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }
    /// <summary>
    /// How the address bar presents the current location. Only meaningful for a folder tab; a web
    /// tab has a URL rather than a path, so it always uses the editable box.
    /// </summary>
    public enum AddressBarMode
    {
        /// <summary>An editable text box holding the full path. Explorer's default.</summary>
        Path,

        /// <summary>
        /// A row of clickable path segments. Explorer reaches this by clicking the "Address:" label,
        /// which is why the label itself acts as the toggle.
        /// </summary>
        Breadcrumbs,
    }
}
