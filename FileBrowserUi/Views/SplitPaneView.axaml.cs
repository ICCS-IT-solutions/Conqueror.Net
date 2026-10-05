using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Conqueror.Net.FileBrowserUi.ViewModels;

namespace Conqueror.Net.FileBrowserUi.Views;

/// <summary>
/// The orthodox (dual-pane) tab. Composes two existing <see cref="FileBrowserView"/>s, so it
/// adds no browser logic of its own - only the focus tracking that decides which pane the
/// window's shared chrome drives.
/// </summary>
public partial class SplitPaneView : UserControl
{
    public SplitPaneView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        AddHandler(PointerPressedEvent, OnAnyPointerPressed, handledEventsToo: true);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private SplitPaneViewModel? Vm => DataContext as SplitPaneViewModel;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        Vm.PropertyChanged -= OnVmPropertyChanged;
        Vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e
    )
    {
        if (e.PropertyName == nameof(SplitPaneViewModel.SplitRatio))
        {
            SyncColumns();
        }
    }

    /// <summary>
    /// Marks whichever pane was clicked as the active one, so the shared address bar and Back
    /// button operate on it. Pointer capture is checked so the click is ignored once the user
    /// starts dragging the splitter.
    /// </summary>
    private void OnAnyPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed != true)
        {
            return;
        }

        // A splitter drag is not a pane selection.
        if (e.Source is GridSplitter)
        {
            return;
        }

        var context = e.Source as Control;

        while (context is not null)
        {
            var dataContext = context.DataContext as FileBrowserViewModel;

            if (dataContext is not null && Vm is not null)
            {
                Vm.FocusPaneCommand.Execute(dataContext);
                return;
            }

            context = context.Parent as Control;
        }
    }

    /// <summary>Turns the splitter's pixel width back into a fraction for the next resize.</summary>
    private void OnSplitterDragCompleted(object? sender, VectorEventArgs e) => SyncRatio();

    private void SyncColumns()
    {
        var columns = this.FindControl<Grid>("PaneGrid")?.ColumnDefinitions;

        if (columns is not { Count: 3 } || Vm is null)
        {
            return;
        }

        // The middle column is the fixed-width grip; the two panes share what is left.
        var grip = columns[1].Width;
        var available = Math.Max(1, Bounds.Width - 7);
        var left = available * Vm.SplitRatio;

        columns[0].Width = new Avalonia.Controls.GridLength(left);
        columns[2].Width = new Avalonia.Controls.GridLength(Math.Max(1, available - left));
    }

    private void SyncRatio()
    {
        var columns = this.FindControl<Grid>("PaneGrid")?.ColumnDefinitions;

        if (columns is not { Count: 3 } || Vm is null)
        {
            return;
        }

        var total = columns[0].ActualWidth + columns[2].ActualWidth;

        if (total > 1)
        {
            Vm.SplitRatio = columns[0].ActualWidth / total;
        }
    }
}