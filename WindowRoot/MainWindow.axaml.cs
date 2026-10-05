using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.VisualTree;
using Conqueror.Net.Core.Tabs;
using Conqueror.Net.FileBrowserUi.Models;
using Conqueror.Net.FileBrowserUi.ViewModels;
using Conqueror.Net.WindowRoot.ViewModels;

namespace Conqueror.Net.WindowRoot;

/// <summary>
/// The shell window. Chrome that needs a view rather than a binding (menu items, the
/// address bar's Enter key) is handled here; everything else lives in the view-model.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        KeyDown += OnWindowKeyDown;
        Loaded += OnLoaded;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Registers the platform clipboard once the window has a TopLevel, so the tab
    /// view-models can cut/copy/paste without holding a visual reference.
    /// </summary>
    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Window inherits TopLevel, so Clipboard is directly available here.
        FileBrowserUi.Services.ClipboardProvider.Register(Clipboard);
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    /// <summary>Resolves a command against whichever tab kind is currently active.</summary>
    private ITabViewModel? CurrentTab => Vm?.SelectedTab;

    private FileBrowserViewModel? CurrentFileTab => CurrentTab as FileBrowserViewModel;

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        switch (e.Key)
        {
            case Key.T when ctrl:
                Vm.AddWebTabCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.N when ctrl:
                Vm.AddFileTabCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.P when ctrl:
                Vm.AddSplitPaneTabCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.OemTilde when ctrl:
                Vm.AddTerminalTabCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.U when ctrl && Vm.IsSplitPaneActive:
                Vm.SwapPanesCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.W when ctrl:
                Vm.CloseTabCommand.Execute(Vm.SelectedTab);
                e.Handled = true;
                break;
            case Key.Tab when ctrl:
                Vm.NextTabCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.F5:
                Vm.RefreshCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                // Escape stops the load, like IE's toolbar Stop button.
                Vm.StopCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private void OnAddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Vm is not null)
        {
            Vm.NavigateCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnNewWebTabClick(object? sender, RoutedEventArgs e) =>
        Vm?.AddWebTabCommand.Execute(null);

    /// <summary>
    /// Selects the clicked tab.
    /// </summary>
    /// <remarks>
    /// The tab's own close button sits inside this one, and <see cref="Button.Click"/> bubbles, so
    /// a click that just closed a tab arrives here too. Ignoring any tab that is no longer in
    /// <c>Tabs</c> stops that click from re-selecting the tab it just closed, which would leave
    /// the content area showing a closed tab.
    /// </remarks>
    private void OnTabClick(object? sender, RoutedEventArgs e)
    {
        if (
            Vm is { } vm
            && sender is Button { CommandParameter: ITabViewModel tab }
            && vm.Tabs.Contains(tab)
        )
        {
            vm.SelectedTab = tab;
        }
    }
    private void OnNewSplitPaneTabClick(object? sender, RoutedEventArgs e) =>
        Vm?.AddSplitPaneTabCommand.Execute(null);

    private void OnNewTerminalTabClick(object? sender, RoutedEventArgs e) =>
        Vm?.AddTerminalTabCommand.Execute(null);

    /// <summary>Reloads one pane of the dual-pane tab, named by the menu item's Tag.</summary>
    private void OnRefreshPaneClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.ActiveSplitPane is not { } split)
        {
            return;
        }

        var pane = (sender as MenuItem)?.Tag as string == "Left" ? split.LeftPane : split.RightPane;
        pane.RefreshCommand.Execute(null);
    }

    private void OnNewFolderClick(object? sender, RoutedEventArgs e) =>
        CurrentFileTab?.CreateNewFolderCommand.Execute(null);

    private void OnToggleFolderPaneClick(object? sender, RoutedEventArgs e)
    {
        if (CurrentFileTab is not null)
        {
            CurrentFileTab.ToggleFolderPaneCommand.Execute(null);
            SyncMenuChecks();
        }
    }

    private void OnToggleHiddenClick(object? sender, RoutedEventArgs e)
    {
        if (CurrentFileTab is not null)
        {
            CurrentFileTab.ShowHiddenFiles = !CurrentFileTab.ShowHiddenFiles;
            SyncMenuChecks();
        }
    }

    /// <summary>Switches the folder pane between Icons / List / Details / Tiles.</summary>
    private void OnViewModeClick(object? sender, RoutedEventArgs e)
    {
        if (
            CurrentFileTab is { } file
            && sender is MenuItem { Tag: string tag }
            && Enum.TryParse<FileViewMode>(tag, ignoreCase: true, out var mode)
        )
        {
            file.SetViewModeCommand.Execute(mode);
        }
    }

    private void OnSelectAllClick(object? sender, RoutedEventArgs e)
    {
        if (CurrentFileTab is null)
        {
            return;
        }

        foreach (var entry in CurrentFileTab.Items)
        {
            CurrentFileTab.SelectedItem = entry;
        }
    }

    private void OnOpenLocationClick(object? sender, RoutedEventArgs e)
    {
        // Focus the address bar so the user can type a path or URL straight into it.
        var addressBox = this.FindControl<TextBox>(AddressBoxName);
        addressBox?.Focus();
    }

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Shows the XP-style About box.</summary>
    private void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        var face = new SolidColorBrush(Color.Parse("#ECE9D8"));
        var edge = new SolidColorBrush(Color.Parse("#716F64"));

        var dialog = new Window
        {
            Title = "About Conqueror.Net",
            Width = 340,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = face,
            ShowInTaskbar = false,
        };

        var content = new StackPanel { Margin = new Thickness(14), Spacing = 8 };
        content.Children.Add(
            new TextBlock
            {
                Text = "Conqueror.Net",
                FontSize = 17,
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(Color.Parse("#0A246A")),
            }
        );
        content.Children.Add(new TextBlock { Text = "Version 0.1.0" });
        content.Children.Add(
            new TextBlock
            {
                Text =
                    "A Windows XP-style file manager with tabbed browsing,\n"
                    + "including an embedded Chromium (CEF 120) browser.",
                TextWrapping = TextWrapping.Wrap,
            }
        );

        var ok = new Button
        {
            Content = "OK",
            Classes = { "xpButton" },
            Width = 74
        };
        ok.Click += (_, _) => dialog.Close();
        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
        };
        buttons.Children.Add(ok);
        content.Children.Add(buttons);

        dialog.Content = new Border
        {
            BorderBrush = edge,
            BorderThickness = new Thickness(1),
            Child = content
        };

        dialog.Show(this);
    }

    /// <summary>Re-applies the checkbox state of the View menu after a command changed it.</summary>
    private void SyncMenuChecks()
    {
        foreach (var item in this.GetVisualDescendants().OfType<MenuItem>())
        {
            switch (item.Name)
            {
                case FolderPaneMenuItemName:
                    item.IsChecked = CurrentFileTab?.ShowFolderPane ?? false;
                    break;
                case HiddenFilesMenuItemName:
                    item.IsChecked = CurrentFileTab?.ShowHiddenFiles ?? false;
                    break;
            }
        }
    }

    private const string AddressBoxName = "AddressBox";
    private const string FolderPaneMenuItemName = "FolderPaneMenuItem";
    private const string HiddenFilesMenuItemName = "HiddenFilesMenuItem";
}
