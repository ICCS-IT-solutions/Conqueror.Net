using System;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
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
        if (Vm is null)
        {
            return;
        }

        Vm.PropertyChanged -= OnVmPropertyChanged;
        Vm.PropertyChanged += OnVmPropertyChanged;

        ApplyViewMode(Vm.ViewMode);
        WireTreeSelection();
    }

    private void OnVmPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e
    )
    {
        if (e.PropertyName == nameof(FileBrowserViewModel.ViewMode) && Vm is not null)
        {
            ApplyViewMode(Vm.ViewMode);
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

        // Opening a web page must not desync the sidebar, so subscribe on attach.
        if (Vm is not null)
        {
            Vm.OpenFileRequest -= OnOpenFileRequest;
            Vm.OpenFileRequest += OnOpenFileRequest;
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
}
