using Avalonia;
using Avalonia.Input;
using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.Input;
using Conqueror.Net.FileBrowserUi.Models;
using Conqueror.Net.FileBrowserUi.Services;

namespace Conqueror.Net.FileBrowserUi.ViewModels;

public sealed partial class FileBrowserViewModel
{
    private FileClipboardPayload _clipboard = FileClipboardPayload.Empty;

    [RelayCommand]
    private void NewFile()
    {
        var name = UniqueName("New file", string.Empty);
        var (success, error) = _fileSystem.CreateFile(CurrentPath, name);
        if (!success)
        {
            StatusText = error ?? $"Could not create '{name}'.";
            return;
        }

        Refresh();
        BeginRename(FindEntry(name));
    }

    [RelayCommand]
    private void NewFolder()
    {
        var name = UniqueName("New folder", string.Empty);
        var (success, error) = _fileSystem.CreateDirectory(CurrentPath, name);
        if (!success)
        {
            StatusText = error ?? $"Could not create '{name}'.";
            return;
        }

        Refresh();
        BeginRename(FindEntry(name));
    }

    [RelayCommand]
    private async Task CutAsync()
    {
        var paths = SelectedPaths();
        if (paths.Count == 0)
        {
            return;
        }

        _clipboard = FileClipboardPayload.FromPaths(paths, isCut: true);
        await WriteClipboardAsync(_clipboard);
        StatusText = $"{paths.Count:N0} item(s) cut — paste to move.";
    }

    [RelayCommand]
    private async Task CopyAsync()
    {
        var paths = SelectedPaths();
        if (paths.Count == 0)
        {
            return;
        }

        _clipboard = FileClipboardPayload.FromPaths(paths, isCut: false);
        await WriteClipboardAsync(_clipboard);
        StatusText = $"{paths.Count:N0} item(s) copied.";
    }

    [RelayCommand]
    private async Task PasteAsync()
    {
        if (_clipboard.IsEmpty)
        {
            var outside = await ReadClipboardAsync();
            if (outside.Count == 0)
            {
                StatusText = "Nothing to paste.";
                return;
            }

            var (ok, error) = _fileSystem.Copy(outside, CurrentPath);
            StatusText = ok ? $"Pasted {outside.Count:N0} item(s)." : error ?? "Paste failed.";
            if (ok)
            {
                Refresh();
            }

            return;
        }

        var (success, copyError) = _clipboard.IsCut
            ? _fileSystem.Move(_clipboard.Paths, CurrentPath)
            : _fileSystem.Copy(_clipboard.Paths, CurrentPath);

        if (!success)
        {
            StatusText = copyError ?? "Paste failed.";
            return;
        }

        if (_clipboard.IsCut)
        {
            _clipboard = FileClipboardPayload.Empty;
            await ClearClipboardAsync();
        }

        Refresh();
        StatusText = "Pasted.";
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        var paths = SelectedPaths();
        if (paths.Count == 0)
        {
            return;
        }

        var confirmed = await (ConfirmAsync?.Invoke(
            $"Are you sure you want to delete {paths.Count:N0} item(s)?",
            "Confirm delete") ?? Task.FromResult(true));
        if (!confirmed)
        {
            return;
        }

        var (success, error) = _fileSystem.Delete(paths);
        StatusText = success ? $"Deleted {paths.Count:N0} item(s)." : error ?? "Delete failed.";
        if (success)
        {
            Refresh();
        }
    }

    [RelayCommand]
    private void Rename()
    {
        BeginRename(SelectedItem);
    }

    [RelayCommand]
    private void ConfirmRename()
    {
        var entry = RenamingEntry;
        if (entry is null)
        {
            return;
        }

        var (success, newPath, error) = _fileSystem.Rename(entry.FullPath, RenameText);
        RenamingEntry = null;
        if (!success)
        {
            StatusText = error ?? "Rename failed.";
            return;
        }

        Refresh();
        if (newPath is not null)
        {
            SelectEntryByPath(newPath);
        }
    }

    [RelayCommand]
    private void CancelRename()
    {
        RenamingEntry = null;
    }

    [RelayCommand]
    private void ShowProperties()
    {
        var entry = SelectedItem;
        if (entry is null)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(entry.FullPath)
            {
                Verb = "properties",
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            StatusText = $"Could not show properties: {ex.Message}";
        }
    }

    [RelayCommand]
    private void SelectAll()
    {
        SelectAllRequest?.Invoke();
    }

    public event Action? SelectAllRequest;

    private void BeginRename(FileSystemEntry? entry)
    {
        if (entry is null)
        {
            StatusText = "Select an item to rename.";
            return;
        }

        RenamingEntry = entry;
        RenameText = entry.Name;
    }

    private FileSystemEntry? FindEntry(string name) =>
        Items.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    private List<string> SelectedPaths()
    {
        var paths = new List<string>();
        if (SelectedItem is not null)
        {
            paths.Add(SelectedItem.FullPath);
        }

        foreach (var extra in ExtraSelection)
        {
            if (!paths.Contains(extra.FullPath, StringComparer.OrdinalIgnoreCase))
            {
                paths.Add(extra.FullPath);
            }
        }

        return paths;
    }

    private string UniqueName(string stem, string extension)
    {
        var existing = new HashSet<string>(
            Items.Select(e => e.Name),
            StringComparer.OrdinalIgnoreCase);

        var candidate = stem + extension;
        var n = 2;
        while (existing.Contains(candidate))
        {
            candidate = $"{stem} ({n}){extension}";
            n++;
        }

        return candidate;
    }

    private static async Task WriteClipboardAsync(FileClipboardPayload payload)
    {
        var clipboard = ClipboardProvider.Current;
        if (clipboard is null)
        {
            return;
        }

        await clipboard.SetTextAsync(string.Join(Environment.NewLine, payload.Paths));
    }

    private static async Task<IReadOnlyList<string>> ReadClipboardAsync()
    {
        var clipboard = ClipboardProvider.Current;
        if (clipboard is null)
        {
            return [];
        }

        try
        {
            var text = await clipboard.TryGetTextAsync().ConfigureAwait(false);
            return FileClipboardPayload.TryParseDropFiles(text, out var paths) ? paths : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static Task ClearClipboardAsync()
    {
        var clipboard = ClipboardProvider.Current;
        return clipboard?.ClearAsync() ?? Task.CompletedTask;
    }
}
