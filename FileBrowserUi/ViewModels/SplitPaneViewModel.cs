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
        // The right pane opens on the parent of the left pane's start, which is the arrangement
        // a dual-pane manager wants: source on one side, destination on the other.
        _left = new FileBrowserViewModel(fileSystem, initialPath);
        _right = new FileBrowserViewModel(fileSystem, _left.ParentPath ?? initialPath);

        _left.OpenFileRequest += entry => OpenFileRequest?.Invoke(entry);
        _right.OpenFileRequest += entry => OpenFileRequest?.Invoke(entry);

        _left.PropertyChanged += OnPaneNavigated;
        _right.PropertyChanged += OnPaneNavigated;

        ActivePane = _left;
    }

    public FileBrowserViewModel LeftPane => _left;

    public FileBrowserViewModel RightPane => _right;

    /// <summary>Raised when either pane opens a non-folder; the window root handles it.</summary>
    public event Action<Models.FileSystemEntry>? OpenFileRequest;

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