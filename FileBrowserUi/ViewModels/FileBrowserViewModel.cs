using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Conqueror.Net.Core.Tabs;
using Conqueror.Net.FileBrowserUi.Models;
using Conqueror.Net.FileBrowserUi.Services;

namespace Conqueror.Net.FileBrowserUi.ViewModels;

/// <summary>Drives a file-system tab: navigation history, the entry list and the status bar.</summary>
public sealed partial class FileBrowserViewModel : ObservableObject, ITabViewModel
{
    //Handlers for new file, cut, copy, paste, delete, rename, properties, and select all.
    //Here these pertain to the file browser tab.
    //
    // The shell drives these through MainWindowViewModel's Edit menu; the [RelayCommand]
    // methods below are the real implementations (hand-written methods, not stubs).
    //Capabilities:
    public bool CanNewFile => true;
    public bool CanNewFolder => true;
    public bool CanCut => true;
    public bool CanCopy => true;
    public bool CanPaste => true;
    public bool CanDelete => true;
    public bool CanRename => true;
    public bool CanProperties => true;
    public bool CanSelectAll => true;

    /// <summary>
    /// Pending rename target. Set by <see cref="Rename"/>; the view shows its rename box
    /// when this is non-null, then calls <see cref="ConfirmRenameCommand"/> or
    /// <see cref="CancelRenameCommand"/>.
    /// </summary>
    [ObservableProperty]
    private FileSystemEntry? _renamingEntry;

    [ObservableProperty]
    private string _renameText = string.Empty;

    /// <summary>
    /// Multi-selection beyond <see cref="SelectedItem"/>. The view keeps this in sync with
    /// its ListBox selection so cut/copy/delete act on every highlighted row.
    /// </summary>
    public ObservableCollection<FileSystemEntry> ExtraSelection { get; } = [];

    /// <summary>
    /// Confirmation hook for destructive actions. The view sets this to show an XP-style
    /// dialog; when unset (tests, headless) actions proceed without prompting.
    /// </summary>
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; } =
        (_, _) => Task.FromResult(true);
    private readonly IFileSystemService _fileSystem;
    private readonly ObservableCollection<FileSystemEntry> _allEntries = [];

    /// <summary>Back/forward history, held as an index into <see cref="_history"/>.</summary>
    private readonly List<string> _history = [];

    private int _historyIndex = -1;

    public FileBrowserViewModel(IFileSystemService fileSystem, string? initialPath = null)
    {
        _fileSystem = fileSystem;

        foreach (var folder in _fileSystem.GetShellFolders())
        {
            ShellFolders.Add(folder);
        }

        var start =
            _fileSystem.ResolvePath(initialPath ?? string.Empty)
            ?? _fileSystem.GetShellFolders().FirstOrDefault()?.Path
            ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        // Built after the shell folders are collected, and before the first LoadFolder so the
        // tree can reveal the starting folder without a second pass over the roots.
        BuildFolderPane();

        LoadFolder(start ?? string.Empty);
    }

    /// <summary>Entries after hidden-file filtering, searching and sorting.</summary>
    public ObservableCollection<FileSystemEntry> Items { get; } = [];

    /// <summary>Task-pane entries (special folders and drives).</summary>
    public ObservableCollection<ShellFolder> ShellFolders { get; } = [];

    /// <summary>Clickable path segments shown in the breadcrumb bar.</summary>
    public ObservableCollection<BreadcrumbSegment> Breadcrumbs { get; } = [];

    [ObservableProperty]
    private FileSystemEntry? _selectedItem;

    [ObservableProperty]
    private ShellFolder? _selectedShellFolder;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private FileViewMode _viewMode = FileViewMode.Icons;

    [ObservableProperty]
    private FileSortColumn _sortColumn = FileSortColumn.Name;

    [ObservableProperty]
    private bool _sortAscending = true;

    [ObservableProperty]
    private bool _showHiddenFiles;

    [ObservableProperty]
    private bool _showFolderPane = true;

    private string _currentPath = string.Empty;
    private bool _isBusy;
    private double? _loadProgress;

    public string CurrentPath
    {
        get => _currentPath;
        private set
        {
            if (SetProperty(ref _currentPath, value))
            {
                OnPropertyChanged(nameof(Location));
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(ParentPath));
                OnPropertyChanged(nameof(CanGoUp));
                RaiseHistoryChanged();
            }
        }
    }

    /// <summary>Display name for the tab: the current folder's name, or the whole path at a root.</summary>
    public string Title
    {
        get
        {
            if (string.IsNullOrEmpty(CurrentPath))
            {
                return "My Computer";
            }

            var trimmed = CurrentPath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar
            );
            var name = trimmed
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .LastOrDefault();
            return string.IsNullOrEmpty(name) ? trimmed : name;
        }
    }

    public string Location => CurrentPath;

    /// <summary>The real XP Explorer folder artwork, downscaled by tools/import-xp-chrome-icons.ps1.</summary>
    public string IconKey => "Icon.Xp.Explorer";

    public bool IsFileBrowser => true;

    public string? ParentPath => _fileSystem.GetParent(CurrentPath);

    public bool CanGoUp => ParentPath is not null;

    public bool CanGoBack => _historyIndex > 0;

    public bool CanGoForward => _historyIndex >= 0 && _historyIndex < _history.Count - 1;

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public double? LoadProgress
    {
        get => _loadProgress;
        private set => SetProperty(ref _loadProgress, value);
    }

    /// <summary>
    /// Raised when the user opens a non-folder item. The window root subscribes and either
    /// spawns a browser tab (for URLs) or hands the file to the operating system.
    /// </summary>
    public event Action<FileSystemEntry>? OpenFileRequest;

    // ---- ITabViewModel -------------------------------------------------------

    void ITabViewModel.Navigate(string location) => NavigateTo(location);

    void ITabViewModel.GoBack() => GoBack();

    void ITabViewModel.GoForward() => GoForward();

    void ITabViewModel.Reload() => Refresh();

    void ITabViewModel.Stop()
    {
        // A recursive search is the one thing that can be in flight here, so Stop cancels it
        // rather than simply clearing the busy flags as it did when everything was
        // synchronous.
        CancelSearch();
        IsBusy = false;
        LoadProgress = null;
    }

    [RelayCommand]
    public void GoBack()
    {
        if (!CanGoBack)
        {
            return;
        }

        _historyIndex--;
        LoadFolder(_history[_historyIndex]);
    }

    [RelayCommand]
    public void GoForward()
    {
        if (!CanGoForward)
        {
            return;
        }

        _historyIndex++;
        LoadFolder(_history[_historyIndex]);
    }
}

/// <summary>
/// One clickable segment of the breadcrumb bar.
/// </summary>
/// <param name="Name">Text shown in the bar.</param>
/// <param name="Path">Full path this segment navigates to.</param>
/// <param name="IsLast">True for the deepest segment, which is the current folder.</param>
/// <param name="IsRoot">True for the drive/mount root, which is rendered in bold.</param>
public sealed record BreadcrumbSegment(string Name, string Path, bool IsLast, bool IsRoot = false);
