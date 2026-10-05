using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Conqueror.Net.FileBrowserUi.Models;

namespace Conqueror.Net.FileBrowserUi.ViewModels;

public sealed partial class FileBrowserViewModel
{
    /// <summary>
    /// Re-projects <see cref="_allEntries"/> into <see cref="Items"/> applying the hidden-file
    /// filter, the search box and the current sort. Called whenever any of those change, and
    /// re-preserving the selected item so sorting does not lose the user's place.
    /// </summary>
    private void ApplyFilterAndSort()
    {
        var previouslySelected = SelectedItem?.FullPath;

        Items.Clear();

        IEnumerable<FileSystemEntry> source = _allEntries;

        if (!ShowHiddenFiles)
        {
            source = source.Where(e => !e.IsHidden);
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var term = SearchText.Trim();
            source = source.Where(e =>
                e.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                || e.TypeDescription.Contains(term, StringComparison.CurrentCultureIgnoreCase)
            );
        }

        // Explorer always floats folders above files regardless of the sort key.
        var comparer = SortAscending
            ? Comparer<object?>.Create(CompareAscending)
            : Comparer<object?>.Create((a, b) => CompareAscending(b, a));

        var ordered = source
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(SortKey, comparer)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase);

        foreach (var entry in ordered)
        {
            Items.Add(entry);
        }

        UpdateStatus(null);

        if (previouslySelected is not null)
        {
            SelectedItem = Items.FirstOrDefault(e =>
                string.Equals(e.FullPath, previouslySelected, StringComparison.OrdinalIgnoreCase)
            );
        }
    }

    private object? SortKey(FileSystemEntry entry) =>
        SortColumn switch
        {
            FileSortColumn.Size => entry.Length,
            FileSortColumn.Type => entry.TypeDescription,
            FileSortColumn.DateModified => entry.Modified,
            _ => entry.Name,
        };

    private static int CompareAscending(object? a, object? b) =>
        (a, b) switch
        {
            (string sa, string sb)
                => string.Compare(sa, sb, StringComparison.CurrentCultureIgnoreCase),
            (long la, long lb) => la.CompareTo(lb),
            (DateTime da, DateTime db) => da.CompareTo(db),
            _ => Comparer<object?>.Default.Compare(a, b),
        };

    partial void OnShowHiddenFilesChanged(bool value) => ApplyFilterAndSort();

    partial void OnSearchTextChanged(string value) => ApplyFilterAndSort();

    partial void OnSortColumnChanged(FileSortColumn value) => ApplyFilterAndSort();

    partial void OnSortAscendingChanged(bool value) => ApplyFilterAndSort();

    [RelayCommand]
    private void SetViewMode(FileViewMode mode) => ViewMode = mode;

    /// <summary>
    /// Sorts by a column. Takes the name as text because that is what the XAML column
    /// header buttons can pass, and so the header markup stays free of enum literals.
    /// </summary>
    [RelayCommand]
    private void SortBy(string? column)
    {
        if (!Enum.TryParse<FileSortColumn>(column, ignoreCase: true, out var parsed))
        {
            return;
        }

        // Clicking the active column flips direction, like Explorer's header buttons.
        if (SortColumn == parsed)
        {
            SortAscending = !SortAscending;
        }
        else
        {
            SortColumn = parsed;
            SortAscending = true;
        }
    }

    [RelayCommand]
    private void ToggleFolderPane() => ShowFolderPane = !ShowFolderPane;

    [RelayCommand]
    private void CreateNewFolder()
    {
        const string name = "New Folder";
        var candidate = name;
        var suffix = 2;

        while (Items.Any(e => string.Equals(e.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{name} ({suffix++})";
        }

        var (success, error) = _fileSystem.CreateDirectory(CurrentPath, candidate);
        if (!success)
        {
            StatusText = error ?? "Could not create the folder.";
            return;
        }

        Refresh();
        SelectEntryByPath(Path.Combine(CurrentPath, candidate));
    }
}
