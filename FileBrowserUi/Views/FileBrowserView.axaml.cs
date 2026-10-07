using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Conqueror.Net.FileBrowserUi.Models;
using Conqueror.Net.FileBrowserUi.ViewModels;

namespace Conqueror.Net.FileBrowserUi.Views;

/// <summary>
/// The folder pane. Handles the interactions that Avalonia has no binding for:
/// double-click to open, Enter to open, Backspace to go up.
/// </summary>
public partial class FileBrowserView : UserControl
{
    public FileBrowserView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private FileBrowserViewModel? Vm => DataContext as FileBrowserViewModel;

    /// <summary>The VM currently subscribed to; DataContext has already changed by the time
    /// <see cref="OnDataContextChanged"/> runs, so the previous one must be tracked here.</summary>
    private FileBrowserViewModel? _subscribedVm;

    /// <summary>
    /// Runs a Common Tasks row. The task carries its own <c>Action</c> rather than an
    /// <c>ICommand</c>, because the commands it invokes already live on the view-model and
    /// wrapping each one again would add a layer with no behaviour in it.
    /// </summary>
    private void OnTaskLinkClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TaskPaneTask task })
        {
            task.Invoke();
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        // Unsubscribe the previous tab's VM first: DataContext has already flipped to the
        // new value, so a plain "-=" against Vm would miss the old subscription entirely.
        if (_subscribedVm is not null)
        {
            _subscribedVm.PropertyChanged -= OnVmPropertyChanged;
            _subscribedVm.SelectAllRequest -= OnSelectAllRequest;
            _subscribedVm.OpenFileRequest -= OnOpenFileRequest;
            _subscribedVm = null;
        }

        if (Vm is null)
        {
            return;
        }

        _subscribedVm = Vm;
        Vm.PropertyChanged += OnVmPropertyChanged;
        Vm.SelectAllRequest += OnSelectAllRequest;
        Vm.OpenFileRequest += OnOpenFileRequest;

        // Destructive verbs (delete) ask through this hook; the view answers with an
        // XP-style Yes/No dialog. Headless contexts never assign, keeping the default.
        Vm.ConfirmAsync = ConfirmAsync;

        // Selection sync, guarded with -= first: DataContextChanged can fire more than
        // once for the same view and the ListBox instance outlives individual events.
        if (this.FindControl<ListBox>("EntryList") is { } list)
        {
            list.SelectionChanged -= OnEntrySelectionChanged;
            list.SelectionChanged += OnEntrySelectionChanged;
        }

        ApplyViewMode(Vm.ViewMode);
        WireTreeSelection();
    }

    private void OnVmPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e
    )
    {
        if (Vm is null)
        {
            return;
        }

        if (e.PropertyName == nameof(FileBrowserViewModel.ViewMode))
        {
            ApplyViewMode(Vm.ViewMode);
            return;
        }

        if (e.PropertyName == nameof(FileBrowserViewModel.RenamingEntry))
        {
            if (Vm.RenamingEntry is null)
            {
                return;
            }

            // The IsVisible binding needs a turn of the dispatcher before the box can take
            // focus, so post instead of focusing from inside the change notification.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (Vm?.RenamingEntry is not null
                    && this.FindControl<TextBox>("RenameBox") is { } box)
                {
                    box.Focus();
                    box.SelectAll();
                }
            });
        }
    }

    /// <summary>Keeps the folder tree in step when the user picks a folder or drive.</summary>
    private void WireTreeSelection()
    {
        // A single click on a tree row navigates, matching Explorer; the view-model owns the
        // command so the same rule applies whether the click came from here or a keyboard
        // selection change.
        if (this.FindControl<TreeView>("FolderTree") is { } tree)
        {
            tree.SelectionChanged -= OnTreeSelectionChanged;
            tree.SelectionChanged += OnTreeSelectionChanged;
        }
    }

    private void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is FolderTreeNode node && Vm is not null)
        {
            Vm.NavigateToTreeNodeCommand.Execute(node);
        }
    }

    private void OnOpenFileRequest(FileSystemEntry entry)
    {
        // Nothing extra to do here; the shell handles the actual opening. The subscription
        // exists so a new view instance stops reacting after it is detached.
    }

    private void OnEntryDoubleTapped(object? sender, TappedEventArgs e)
    {
        Vm?.OpenSelectedCommand.Execute(null);
    }

    /// <summary>
    /// Right-clicking a row that is not part of the current selection collapses the
    /// selection to that row, the way Explorer does, so the context menu acts on the item
    /// under the pointer. A row already inside a multi-selection leaves it alone, keeping
    /// Cut/Copy/Delete working on every highlighted item; right-clicking empty space
    /// changes nothing.
    /// </summary>
    private void OnEntryPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ListBox list
            || !e.GetCurrentPoint(list).Properties.IsRightButtonPressed)
        {
            return;
        }

        // Walk the logical tree up from the hit element to the row's container; the
        // ancestor check keeps an empty-area click (whose source chain reaches the list
        // itself) from matching a container belonging to some other control.
        ListBoxItem? container = null;
        for (var current = e.Source as StyledElement;
             current is not null;
             current = current.Parent)
        {
            if (current is ListBoxItem item)
            {
                container = item;
                break;
            }

            if (ReferenceEquals(current, list))
            {
                break;
            }
        }

        if (container is null || container.IsSelected)
        {
            return;
        }

        // Cleared first: with SelectionMode.Multiple a bare set is not guaranteed to
        // replace the existing selection rather than add to it.
        list.SelectedItem = null;
        list.SelectedItem = container.DataContext;
    }

    private void OnEntryKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                Vm.OpenSelectedCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Back when Vm.CanGoUp:
                Vm.GoUpCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Applies the pane the user picked in the Views menu by switching the list's item
    /// container, which is cheaper and more faithful than four separate lists.
    /// </summary>
    /// <summary>
    /// Applies the layout chosen in the Views menu. Each of Explorer's four modes is a
    /// different item template plus a different items panel, so both are swapped together.
    /// </summary>
    public void ApplyViewMode(FileViewMode mode)
    {
        // Resolve by name rather than through the generated Name= fields: those are not
        // populated early enough in a template's lifetime to be usable from code-behind.
        var list = this.FindControl<ListBox>("EntryList");
        var header = this.FindControl<Border>("DetailsHeader");

        if (list is null)
        {
            return;
        }

        list.ItemTemplate = mode switch
        {
            FileViewMode.List => FindTemplate("EntryListTemplate"),
            FileViewMode.Details => FindTemplate("EntryDetailsTemplate"),
            FileViewMode.Tiles => FindTemplate("EntryTilesTemplate"),
            _ => FindTemplate("EntryIconsTemplate"),
        };

        // Details and Tiles are single-column; Icons and List flow left to right.
        var orientation = mode is FileViewMode.Details or FileViewMode.Tiles
            ? Avalonia.Layout.Orientation.Vertical
            : Avalonia.Layout.Orientation.Horizontal;

        list.ItemsPanel = new FuncTemplate<Panel?>(
            () => new WrapPanel { Orientation = orientation }
        );

        if (header is not null)
        {
            header.IsVisible = mode == FileViewMode.Details;
        }
    }

    private IDataTemplate? FindTemplate(string key) =>
        Resources.TryGetResource(key, this.ActualThemeVariant, out var value)
            ? value as IDataTemplate
            : null;

    /// <summary>The Edit menu's Select All asks through the view; the ListBox owns the selection.</summary>
    private void OnSelectAllRequest()
    {
        if (this.FindControl<ListBox>("EntryList") is { } list)
        {
            list.SelectAll();
        }
    }

    /// <summary>
    /// Mirrors the ListBox selection into <see cref="FileBrowserViewModel.ExtraSelection"/>,
    /// so cut/copy/delete act on every highlighted row and not just the active one.
    /// </summary>
    private void OnEntrySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Vm is null || sender is not ListBox list)
        {
            return;
        }

        var active = list.SelectedItem as FileSystemEntry;
        Vm.ExtraSelection.Clear();

        if (list.SelectedItems is not { } items)
        {
            return;
        }

        foreach (var entry in items.OfType<FileSystemEntry>())
        {
            // The active entry already flows through the two-way SelectedItem binding;
            // SelectedPaths() unions the two, so it must not appear in both.
            if (!ReferenceEquals(entry, active))
            {
                Vm.ExtraSelection.Add(entry);
            }
        }
    }

    private void OnRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                Vm.ConfirmRenameCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                Vm.CancelRenameCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    /// <summary>XP commits an in-place rename when the editor loses focus.</summary>
    private void OnRenameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (Vm?.RenamingEntry is not null)
        {
            Vm.ConfirmRenameCommand.Execute(null);
        }
    }

    /// <summary>
    /// Yes/No prompt used by the destructive verbs. Built in code rather than XAML because
    /// the app ships no dialog assets; styled with the shared xp* classes so it matches the
    /// rest of the shell. Returns true (proceed) when no owner window exists, e.g. in tests.
    /// </summary>
    private async Task<bool> ConfirmAsync(string message, string title)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return true;
        }

        var yesButton = new Button
        {
            Content = "Yes",
            MinWidth = 75,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsDefault = true,
        };
        yesButton.Classes.Add("xpButton");

        var noButton = new Button
        {
            Content = "No",
            MinWidth = 75,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsCancel = true,
        };
        noButton.Classes.Add("xpButton");

        var dialog = new Window
        {
            Title = title,
            CanResize = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            MaxWidth = 480,
        };

        yesButton.Click += (_, _) => dialog.Close(true);
        noButton.Click += (_, _) => dialog.Close(false);

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(14),
            Spacing = 14,
            MinWidth = 320,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 6,
                    Children = { yesButton, noButton },
                },
            },
        };

        // Closing the dialog with the X returns default(bool) — no, do not proceed.
        return await dialog.ShowDialog<bool>(owner);
    }
}
