using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Conqueror.Net.FileBrowserUi.ViewModels;

namespace Conqueror.Net.FileBrowserUi.Views;

/// <summary>
/// The terminal tab. Owns no process logic - that lives in the view-model behind
/// <c>ITerminalBackend</c> - and only adds the Enter-to-send and auto-scroll behaviour.
/// </summary>
public partial class TerminalView : UserControl
{
    public TerminalView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private TerminalViewModel? Vm => DataContext as TerminalViewModel;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        Vm.PropertyChanged -= OnVmPropertyChanged;
        Vm.PropertyChanged += OnVmPropertyChanged;

        // The shell is started once the view exists, because a tab is constructed before its
        // view and a process must not be started for a tab that is never shown.
        _ = Vm.StartAsync();
    }

    private void OnVmPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e
    )
    {
        // Scroll to the newest line as output arrives.
        if (e.PropertyName == nameof(TerminalViewModel.Lines))
        {
            ScrollToEnd();
        }
    }

    /// <summary>Sends the buffered line on Enter, and cancels with Escape.</summary>
    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Vm is not null)
        {
            Vm.SendInputCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            // Escape clears the buffer rather than sending a half-typed command.
            if (Vm is not null)
            {
                Vm.Input = string.Empty;
            }

            e.Handled = true;
        }
    }

    private void ScrollToEnd()
    {
        if (this.FindControl<ScrollViewer>("OutputScroll") is { } scroller)
        {
            scroller.ScrollToEnd();
        }
    }
}