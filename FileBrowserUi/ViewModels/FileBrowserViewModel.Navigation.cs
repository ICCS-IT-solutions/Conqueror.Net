using CommunityToolkit.Mvvm.Input;
using Conqueror.Net.FileBrowserUi.Models;

namespace Conqueror.Net.FileBrowserUi.ViewModels;

public sealed partial class FileBrowserViewModel
{
    /// <summary>Loads a folder without touching history. Also used by the initial construction.</summary>
    private void LoadFolder(string path)
    {
        // Moving to another folder abandons any results, so the list never shows hits from a
        // place the user is no longer in.
        CancelSearch();
        IsSearchActive = false;
        _searchResults.Clear();

        var listing = _fileSystem.ListDirectory(path);

        _allEntries.Clear();
        foreach (var entry in listing.Entries)
        {
            _allEntries.Add(entry);
        }

        CurrentPath = path;
        BuildBreadcrumbs(path);
        ApplyFilterAndSort();
        UpdateStatus(listing.Error);
        SelectedItem = null;
        RevealInTree(path);
    }

    private void PushHistory(string path)
    {
        // Navigating to where we already are, or back and forward by clicking, should not
        // grow the history list; re-use the existing entry instead.
        if (
            _historyIndex >= 0
            && _historyIndex < _history.Count
            && string.Equals(_history[_historyIndex], path, StringComparison.OrdinalIgnoreCase)
        )
        {
            LoadFolder(path);
            return;
        }

        _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
        _history.Add(path);
        _historyIndex = _history.Count - 1;
        LoadFolder(path);
    }

    private void BuildBreadcrumbs(string path)
    {
        Breadcrumbs.Clear();

        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? string.Empty;

        if (!string.IsNullOrEmpty(root))
        {
            // Explorer shows the drive as "Win10 (C:\)" when the volume carries a label, and
            // as a bare "C:\" when it does not. The trailing backslash is kept so a labelled
            // root still reads as a drive rather than as a folder named "C:".
            var label = _fileSystem.GetVolumeLabel(full);
            var rootName = string.IsNullOrWhiteSpace(label) ? root : $"{label} ({root})";

            Breadcrumbs.Add(
                new BreadcrumbSegment(rootName, root, full.Length <= root.Length, IsRoot: true)
            );
        }

        // Everything after the root, e.g. "C:\a\b" -> "a\b".
        var rest = full.Length > root.Length ? full[root.Length..] : string.Empty;
        var accumulated = root;

        var segments = rest.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries
        );

        for (var i = 0; i < segments.Length; i++)
        {
            accumulated = Path.Combine(accumulated, segments[i]);
            Breadcrumbs.Add(
                new BreadcrumbSegment(segments[i], accumulated, i == segments.Length - 1)
            );
        }
    }

    private void UpdateStatus(string? error)
    {
        if (error is not null)
        {
            StatusText = error;
            return;
        }

        var count = Items.Count;
        var objectWord = count == 1 ? "object" : "objects";

        var (total, free) = _fileSystem.GetDriveInfo(CurrentPath);
        var freeText = total > 0 ? $"   {ByteSizeFormatter.Format(free)} free" : string.Empty;

        StatusText = $"{count:N0} {objectWord}{freeText}";
    }

    private void RaiseHistoryChanged()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    [RelayCommand]
    private void NavigateTo(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var resolved = _fileSystem.ResolvePath(path);

        if (resolved is null)
        {
            // Explorer appends "not found" rather than silently ignoring the entry.
            StatusText = $"{path} not found. Check the spelling and try again.";
            return;
        }

        if (_fileSystem.IsDirectory(resolved))
        {
            PushHistory(resolved);
            return;
        }

        // A file was named: open its containing folder and select it there.
        var parent = _fileSystem.GetParent(resolved);
        if (parent is not null)
        {
            PushHistory(parent);
            SelectEntryByPath(resolved);
        }
    }

    [RelayCommand]
    private void NavigateToBreadcrumb(BreadcrumbSegment? segment)
    {
        if (segment is not null)
        {
            PushHistory(segment.Path);
        }
    }

    [RelayCommand]
    private void NavigateToShellFolder(ShellFolder? folder)
    {
        if (folder is not null)
        {
            PushHistory(folder.Path);
        }
    }

    [RelayCommand]
    private void GoUp()
    {
        var parent = ParentPath;
        if (parent is not null)
        {
            PushHistory(parent);
        }
    }

    [RelayCommand]
    private void Refresh()
    {
        var selectedPath = SelectedItem?.FullPath;
        LoadFolder(CurrentPath);

        if (selectedPath is not null)
        {
            SelectEntryByPath(selectedPath);
        }
    }

    /// <summary>Opens the selected folder, or asks the shell to handle a selected file.</summary>
    [RelayCommand]
    private void OpenSelected()
    {
        var entry = SelectedItem;
        if (entry is null)
        {
            return;
        }

        if (entry.IsDirectory)
        {
            PushHistory(entry.FullPath);
            return;
        }

        OpenFileRequest?.Invoke(entry);
    }

    private void SelectEntryByPath(string path)
    {
        var match = Items.FirstOrDefault(e =>
            string.Equals(e.FullPath, path, StringComparison.OrdinalIgnoreCase)
        );

        if (match is not null)
        {
            SelectedItem = match;
        }
    }
}
