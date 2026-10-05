using System;
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
    private readonly IFileSystemService _fileSystem;
    private readonly ObservableCollection<FileSystemEntry> _allEntries = [];

    /// <summary>Back/forward history, held as an index into <see cref="_history"/>.</summary>
    private readonly System.Collections.Generic.List<string> _history = [];

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

    public string IconKey => "Icon.Folder";

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
        // Folder enumeration is synchronous, so there is never anything in flight to abort.
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

/// <summary>One clickable segment of the breadcrumb bar.</summary>
public sealed record BreadcrumbSegment(string Name, string Path, bool IsLast);
