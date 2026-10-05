using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Conqueror.Net.Core.Tabs;
using Conqueror.Net.FileBrowserUi.Models;
using Conqueror.Net.FileBrowserUi.Services;
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
    /// with a URL scheme, otherwise a folder tab navigated to that path.
    /// </summary>
    public void OpenStartupTarget(string target)
    {
        if (
            target.Contains("://", StringComparison.Ordinal)
            || target.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
        )
        {
            AddWebTab(target);
            return;
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
        vm.NewTabRequested += url => AddWebTab(url);
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

    private void AttachTab(ITabViewModel tab)
    {
        Tabs.Add(tab);
        SelectedTab = tab;
        SyncAddressFromTab();
    }

    [RelayCommand]
    private void CloseTab(ITabViewModel? tab)
    {
        var target = tab ?? SelectedTab;
        if (target is null)
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
    private void CloseOtherTabs()
    {
        if (SelectedTab is null)
        {
            return;
        }

        foreach (var tab in Tabs.Where(t => !ReferenceEquals(t, SelectedTab)).ToList())
        {
            Tabs.Remove(tab);
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
        OnPropertyChanged(nameof(IsTerminalActive));
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
    }

    public bool CanGoBack => SelectedTab?.CanGoBack ?? false;

    public bool CanGoForward => SelectedTab?.CanGoForward ?? false;

    public bool CanGoUp => SelectedTab is FileBrowserViewModel { CanGoUp: true }
        || SelectedTab is SplitPaneViewModel { CanGoUp: true };

    // ---- Contextual menu state -----------------------------------------------

    /// <summary>
    /// True when the active tab is an orthodox dual-pane tab. The View menu uses this to swap
    /// in the pane-specific items rather than showing them where they would do nothing.
    /// </summary>
    public bool IsSplitPaneActive => SelectedTab is SplitPaneViewModel;

    /// <summary>The dual-pane tab currently in front, or null.</summary>
    public SplitPaneViewModel? ActiveSplitPane => SelectedTab as SplitPaneViewModel;

    /// <summary>True when the active tab is a terminal, which hides the file-only menus.</summary>
    public bool IsTerminalActive => SelectedTab is TerminalViewModel;

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
        // A web shortcut or a local .html file belongs in a browser tab; everything else is
        // handed to whatever the user has associated with it.
        if (TryGetWebAddress(entry, out var url))
        {
            AddWebTab(url);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(entry.FullPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // Nothing is registered for this type, or the OS refused to launch it.
            App.Log?.Invoke($"Could not open '{entry.Name}': {ex.Message}");
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
