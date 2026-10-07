using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Conqueror.Net.Core.Tabs;
using Conqueror.Net.FileBrowserUi.Services;

namespace Conqueror.Net.FileBrowserUi.ViewModels;

/// <summary>
/// An "orthodox" tab: two independent folder panes side by side, separated by a splitter.
/// </summary>
/// <remarks>
/// This is the traditional dual-pane file manager. Each side owns its own
/// <see cref="FileBrowserViewModel"/>, so each has its own history and its own folder tree;
/// the tab delegates its chrome to whichever pane has focus, which is what keeps the shared
/// address bar and Back button meaningful.
/// </remarks>
public sealed partial class SplitPaneViewModel : ObservableObject, ITabViewModel
{
    /// <summary>Pane widths as a fraction of the tab, so the ratio survives a window resize.</summary>
    private const double DefaultSplitRatio = 0.5;

    private readonly FileBrowserViewModel _left;
    private readonly FileBrowserViewModel _right;
    private readonly IFileSystemService _fileSystem;

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
    /// True when the active pane has a selection to clear. The split pane itself does not
    /// own a list; this delegates to whichever pane is active so the shell's Edit menu
    /// greys correctly when the user has nothing selected.
    /// </summary>
    public bool CanSelectNone => ActivePane?.CanSelectNone ?? false;

    /// <summary>
    /// True when the active pane can invert its selection. Same delegation rule as
    /// <see cref="CanSelectNone"/>: the split pane is a container, not a list owner.
    /// </summary>
    public bool CanInvertSelect => ActivePane?.CanInvertSelect ?? false;

    [ObservableProperty]
    private FileBrowserViewModel _activePane;

    /// <summary>
    /// Share of the tab given to the left pane, 0..1. Stored as a fraction rather than a pixel
    /// width so the panes keep their relative sizes across a window resize.
    /// </summary>
    [ObservableProperty]
    private double _splitRatio = DefaultSplitRatio;

    public SplitPaneViewModel(IFileSystemService fileSystem, string? initialPath = null)
    {
        _fileSystem = fileSystem;

        // The right pane opens on the parent of the left pane's start, which is the arrangement
        // a dual-pane manager wants: source on one side, destination on the other.
        _left = new FileBrowserViewModel(fileSystem, initialPath);
        _right = new FileBrowserViewModel(fileSystem, _left.ParentPath ?? initialPath);

        _left.OpenFileRequest += entry => OpenFileRequest?.Invoke(entry);
        _right.OpenFileRequest += entry => OpenFileRequest?.Invoke(entry);

        _left.EditFileRequest += entry => EditFileRequest?.Invoke(entry);
        _right.EditFileRequest += entry => EditFileRequest?.Invoke(entry);

        _left.PropertyChanged += OnPaneNavigated;
        _right.PropertyChanged += OnPaneNavigated;

        ActivePane = _left;
    }

    public FileBrowserViewModel LeftPane => _left;

    public FileBrowserViewModel RightPane => _right;

    /// <summary>Raised when either pane opens a non-folder; the window root handles it.</summary>
    public event Action<Models.FileSystemEntry>? OpenFileRequest;

    /// <summary>Raised when either pane's Edit verb fires; the window root opens the editor.</summary>
    public event Action<Models.FileSystemEntry>? EditFileRequest;

    public string Title =>
        ActivePane is null
            ? "Dual pane"
            : $"{ActivePane.Title} / {_right.Title}";

    /// <summary>Folder-and-magnifier artwork; Explorer uses it for a dual-pane folder window.</summary>
    public string IconKey => "Icon.Xp.Explorer";

    public string Location => ActivePane?.Location ?? string.Empty;

    public bool CanGoBack => ActivePane?.CanGoBack ?? false;

    public bool CanGoForward => ActivePane?.CanGoForward ?? false;

    public bool CanGoUp => ActivePane?.CanGoUp ?? false;

    public bool IsBusy => ActivePane?.IsBusy ?? false;

    public double? LoadProgress => ActivePane?.LoadProgress;

    public string StatusText =>
        $"Left: {_left.StatusText}    Right: {_right.StatusText}";

    public bool IsFileBrowser => true;

    public string SearchText
    {
        get => ActivePane?.SearchText ?? string.Empty;
        set
        {
            // Two-way against whichever pane has focus, so the shared search box drives it.
            if (ActivePane is not null)
            {
                ActivePane.SearchText = value;
            }
        }
    }

    /// <summary>Swaps the two panes, the common way to reverse a copy direction.</summary>
    [RelayCommand]
    private void SwapPanes()
    {
        (var leftPath, var rightPath) = (_left.CurrentPath, _right.CurrentPath);

        _left.NavigateToCommand.Execute(rightPath);
        _right.NavigateToCommand.Execute(leftPath);
    }

    /// <summary>Reloads the focused pane only; the other keeps its place.</summary>
    [RelayCommand]
    private void RefreshActive() => ActivePane?.RefreshCommand.Execute(null);

    /// <summary>Makes the given pane the one the window chrome drives.</summary>
    [RelayCommand]
    private void FocusPane(FileBrowserViewModel? pane)
    {
        if (pane is not null)
        {
            ActivePane = pane;
        }
    }

    /// <summary>
    /// Re-announces both panes' Edit verb grey state after Tools ▸ Editor File Types
    /// changes the shared extension set; the selections have not moved, so nothing else
    /// would refresh the bindings.
    /// </summary>
    public void NotifyEditorFileTypesChanged()
    {
        _left.NotifyEditorFileTypesChanged();
        _right.NotifyEditorFileTypesChanged();
    }

    // ---- Pane-to-pane transfers ---------------------------------------------
    // The four directional verbs are the dual-pane manager's reason for existing: take the
    // source pane's selection straight to the other side without a clipboard round-trip.
    // Wording is relative to the split, not to which folder sits where, because SwapPanes
    // exchanges the panes' content whenever the user reverses direction.

    /// <summary>Copies the left pane's selection into the right pane's folder.</summary>
    [RelayCommand]
    private void CopyToRight() => Transfer(_left, _right, move: false);

    /// <summary>Moves the left pane's selection into the right pane's folder.</summary>
    [RelayCommand]
    private void MoveToRight() => Transfer(_left, _right, move: true);

    /// <summary>Copies the right pane's selection into the left pane's folder.</summary>
    [RelayCommand]
    private void CopyToLeft() => Transfer(_right, _left, move: false);

    /// <summary>Moves the right pane's selection into the left pane's folder.</summary>
    [RelayCommand]
    private void MoveToLeft() => Transfer(_right, _left, move: true);

    private void Transfer(FileBrowserViewModel source, FileBrowserViewModel target, bool move)
    {
        var paths = source.SelectedPaths();
        if (paths.Count == 0)
        {
            return;
        }

        // Both panes on the same folder would copy a folder onto itself or silently succeed
        // at nothing; say so instead of reporting a strange failure from the file service.
        if (string.Equals(source.CurrentPath, target.CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            source.StatusText = "Both panes are showing the same folder.";
            return;
        }

        var (ok, error) = move
            ? _fileSystem.Move(paths, target.CurrentPath)
            : _fileSystem.Copy(paths, target.CurrentPath);

        if (!ok)
        {
            source.StatusText = error ?? (move ? "Move failed." : "Copy failed.");
            return;
        }

        // A move changes both listings; a copy only the target's.
        target.RefreshCommand.Execute(null);
        if (move)
        {
            source.RefreshCommand.Execute(null);
        }

        source.StatusText = $"{(move ? "Moved" : "Copied")} {paths.Count:N0} item(s) to the other pane.";
    }

    private void OnPaneNavigated(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FileBrowserViewModel.Location) or nameof(FileBrowserViewModel.Title))
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Location));
        }
    }

    // ---- ITabViewModel -------------------------------------------------------

    void ITabViewModel.Navigate(string location) => ActivePane?.NavigateToCommand.Execute(location);

    void ITabViewModel.GoBack() => ActivePane?.GoBackCommand.Execute(null);

    void ITabViewModel.GoForward() => ActivePane?.GoForwardCommand.Execute(null);

    void ITabViewModel.Reload() => ActivePane?.RefreshCommand.Execute(null);

    // Stop is an explicit interface member on the pane, so it is reached through the
    // interface rather than as a method on the concrete type.
    void ITabViewModel.Stop() => ((ITabViewModel?)ActivePane)?.Stop();
}