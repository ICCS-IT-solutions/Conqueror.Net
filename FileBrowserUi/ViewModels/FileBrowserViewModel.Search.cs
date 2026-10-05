using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Conqueror.Net.FileBrowserUi.Models;

namespace Conqueror.Net.FileBrowserUi.ViewModels;

/// <summary>
/// The in-pane recursive search. Unlike the plain <see cref="FileBrowserViewModel.SearchText"/>
/// filter, which only narrows the folder already listed, this walks the subtree from the current
/// folder on a worker thread and streams matches into the content list as they are found.
/// </summary>
public sealed partial class FileBrowserViewModel
{
    /// <summary>Cancellation for the in-flight search; null when none is running.</summary>
    private CancellationTokenSource? _searchCts;

    /// <summary>Matches found so far. Replaces the plain listing while a search is displayed.</summary>
    private readonly ObservableCollection<FileSystemEntry> _searchResults = [];

    /// <summary>True while the content list is showing search results rather than a listing.</summary>
    public bool IsSearchActive { get; private set; }

    [ObservableProperty]
    private bool _isSearching;

    /// <summary>Matches found so far, used for the status text while the walk is running.</summary>
    private int _searchMatchCount;

    [RelayCommand]
    private void StartSearch()
    {
        var term = SearchText?.Trim() ?? string.Empty;
        if (term.Length == 0)
        {
            return;
        }

        CancelSearch();

        _searchResults.Clear();
        _searchMatchCount = 0;
        IsSearchActive = true;
        IsSearching = true;
        LoadProgress = null;

        var cts = new CancellationTokenSource();
        _searchCts = cts;

        // Progress<T> marshals back to the UI thread, so the observable collections may be
        // touched in the callback even though the walk itself runs on a worker thread.
        var progress = new Progress<FileSystemEntry>(entry =>
        {
            _searchResults.Add(entry);
            _searchMatchCount++;
            StatusText = $"Searching... {_searchMatchCount:N0} found";
        });

        var root = CurrentPath;

        var worker = Task.Run(
            () =>
            {
                try
                {
                    _fileSystem.Search(root, term, progress, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // The user pressed Stop; finishing quietly is the expected outcome.
                }
            },
            cts.Token
        );

        _ = worker.ContinueWith(
            _ =>
            {
                IsSearching = false;
                LoadProgress = null;

                if (ReferenceEquals(_searchCts, cts))
                {
                    _searchCts = null;
                }

                cts.Dispose();
                ApplySearchResults();
            },
            TaskScheduler.FromCurrentSynchronizationContext()
        );
    }

    [RelayCommand]
    private void CancelSearch()
    {
        if (_searchCts is null)
        {
            return;
        }

        try
        {
            _searchCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The walk finished and disposed the source between the check and the cancel.
        }
    }

    /// <summary>Abandons any search and returns the pane to the plain current-folder listing.</summary>
    public void ClearSearch()
    {
        CancelSearch();
        IsSearchActive = false;
        IsSearching = false;
        _searchResults.Clear();
        SearchText = string.Empty;
    }

    /// <summary>Sorts the collected matches and shows them, or reports that nothing matched.</summary>
    private void ApplySearchResults()
    {
        Items.Clear();

        foreach (
            var entry in _searchResults
                .OrderByDescending(e => e.IsDirectory)
                .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
        )
        {
            Items.Add(entry);
        }

        if (Items.Count == 0)
        {
            StatusText = $"No items found matching \"{SearchText?.Trim()}\".";
            return;
        }

        var noun = Items.Count == 1 ? "item" : "items";
        StatusText = $"{Items.Count:N0} {noun} found (searched {CurrentPath})";
    }
}