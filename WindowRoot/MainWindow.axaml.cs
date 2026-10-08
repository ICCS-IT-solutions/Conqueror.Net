using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.VisualTree;
using Conqueror.Net.CodeEditorUi.ViewModels;
using Conqueror.Net.CodeEditorUi.Views;
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

        // The dirty-editor close prompt lives here rather than in the editor view: the
        // tab being closed may be a background one, whose view has not been created yet.
        if (Vm is { } vm)
        {
            vm.ConfirmEditorClose = ConfirmEditorCloseAsync;
        }
    }

    /// <summary>
    /// Save / Don't save / Cancel for a dirty editor tab. "Save" runs the editor's save
    /// (which may still open Save As and be cancelled) and only allows the close when the
    /// buffer actually reached disk.
    /// </summary>
    private async Task<bool> ConfirmEditorCloseAsync(CodeEditorViewModel editor)
    {
        var dialog = CodeEditorView.BuildDialog(
            this,
            "Unsaved changes",
            $"Save changes to '{editor.Location}' before closing?");

        var saveButton = CodeEditorView.NewDialogButton("Save", isDefault: true, isCancel: false);
        var discardButton = CodeEditorView.NewDialogButton("Don't save", isDefault: false, isCancel: false);
        var cancelButton = CodeEditorView.NewDialogButton("Cancel", isDefault: false, isCancel: true);

        saveButton.Click += async (_, _) =>
        {
            await editor.SaveCommand.ExecuteAsync(null);

            // A cancelled Save As or a write error leaves the buffer dirty; refusing the
            // close is the only way to keep the edits from being lost.
            dialog.Close(!editor.IsDirty);
        };
        discardButton.Click += (_, _) => dialog.Close(true);
        cancelButton.Click += (_, _) => dialog.Close(false);

        if (dialog.Content is StackPanel panel)
        {
            dialog.Content = CodeEditorView.WithButtons(panel, saveButton, discardButton, cancelButton);
        }

        // Closing with the X returns default(bool) = false, i.e. cancel.
        return await dialog.ShowDialog<bool>(this);
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
            case Key.E when ctrl:
                Vm.AddEditorTabCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.S when ctrl:
                // The editor view consumes Ctrl+S itself when focus is inside it; this
                // covers the rest of the window (toolbar, address bar, tab strip).
                Vm.SaveActiveEditorCommand.Execute(null);
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

    private void OnNewCodeEditorTabClick(object? sender, RoutedEventArgs e) =>
        Vm?.AddEditorTabCommand.Execute(null);

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

    /// <summary>Tools ▸ Editor File Types… — the global picker for the editor's claims.</summary>
    private async void OnEditorFileTypesClick(object? sender, RoutedEventArgs e)
    {
        // OK inside the dialog commits (and persists) the list; only then do the open
        // panes need their Edit verb grey states refreshed, since the selection that
        // drives them never changed. Cancel and the X return false and do nothing.
        var applied = await new EditorFileTypesDialog().ShowDialog<bool>(this);
        if (applied)
        {
            Vm?.NotifyEditorFileTypesChanged();
        }
        }

    /// <summary>Code Editor ▸ Find — searches forward from the caret and selects the match.</summary>
            /// <summary>Code Editor ▸ Find — searches forward from the caret and selects the match.</summary>
    private void OnEditorFindClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.SelectedTab is not CodeEditorViewModel editor)
        {
            return;
        }

        ShowFindReplaceDialog(editor, isReplace: false);
    }

    /// <summary>Code Editor ▸ Replace — find-and-replace within the active editor buffer.</summary>
    private void OnEditorReplaceClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.SelectedTab is not CodeEditorViewModel editor)
        {
            return;
        }

        ShowFindReplaceDialog(editor, isReplace: true);
    }

    /// <summary>Code Editor ▸ Font Larger — grows the editor font by one point.</summary>
    private void OnEditorFontLargerClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.SelectedTab is CodeEditorViewModel editor)
        {
            editor.FontSize = Math.Min(editor.FontSize + 1, 40);
        }
    }

    /// <summary>Code Editor ▸ Font Smaller — shrinks the editor font by one point.</summary>
    private void OnEditorFontSmallerClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.SelectedTab is CodeEditorViewModel editor)
        {
            editor.FontSize = Math.Max(editor.FontSize - 1, 6);
        }
    }

    /// <summary>
    /// <summary>
    /// Lightweight Find (or Find+Replace) dialog. Reuses the XP-style dialog helpers
    /// from <see cref="CodeEditorView"/>. Search is done on the VM's Text buffer
    /// and the selection is pushed back through EditorBoxProvider so the caret
    /// lands on the match. Wraps to the top of the buffer on overflow.
    /// </summary>
    private void ShowFindReplaceDialog(CodeEditorViewModel editor, bool isReplace)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var title = isReplace ? "Find and Replace" : "Find";
        var dialog = CodeEditorView.BuildDialog(owner, title, string.Empty);

        var searchBox = new TextBox
        {
            Width = 320,
            MaxWidth = 480,
        };

        var matchCase = new CheckBox
        {
            Content = "Match case",
            Margin = new Thickness(0, 8, 0, 0),
        };

        TextBox? replaceBox = null;
        var prompt = dialog.Content as StackPanel;

        if (isReplace)
        {
            replaceBox = new TextBox
            {
                Width = 320,
                MaxWidth = 480,
                Margin = new Thickness(0, 8, 0, 0),
            };
        }

        var insertAt = prompt!.Children.Count - 1;

        if (replaceBox is not null)
        {
            prompt.Children.Insert(insertAt, new TextBlock { Text = "Replace with:" });
            prompt.Children.Insert(insertAt + 1, replaceBox);
            insertAt += 2;
        }

        prompt.Children.Insert(insertAt, matchCase);
        prompt.Children.Insert(insertAt + 1, new TextBlock { Text = "Find:" });
        prompt.Children.Insert(insertAt + 2, searchBox);

        var findButton = CodeEditorView.NewDialogButton("Find", isDefault: true, isCancel: false);
        var closeButton = CodeEditorView.NewDialogButton("Close", isDefault: false, isCancel: true);

        void DoFind()
        {
            var box = editor.EditorBoxProvider?.Invoke();
            if (box is null)
            {
                return;
            }

            var text = box.Text ?? string.Empty;
            var search = searchBox.Text;
            if (string.IsNullOrEmpty(search))
            {
                return;
            }

            var comparison = matchCase.IsChecked == true
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            var start = box.SelectionStart + (box.SelectedText?.Length ?? 0);
            var index = text.IndexOf(search, start, comparison);

            if (index < 0 && start > 0)
            {
                index = text.IndexOf(search, 0, comparison);
            }

            if (index >= 0)
            {
                box.SelectRange(index, search.Length);
            }
        }

        findButton.Click += (_, _) => DoFind();

        if (replaceBox is not null)
        {
            var replaceButton = CodeEditorView.NewDialogButton("Replace", isDefault: false, isCancel: false);

            replaceButton.Click += (_, _) =>
            {
                var box = editor.EditorBoxProvider?.Invoke();
                if (box is null)
                {
                    return;
                }

                var text = box.Text ?? string.Empty;
                var search = searchBox.Text;
                if (string.IsNullOrEmpty(search) || box.SelectedText != search)
                {
                    DoFind();
                    return;
                }

                var replacement = replaceBox.Text ?? string.Empty;
                var caret = box.SelectionStart;
                box.Text = text.Remove(caret, search.Length).Insert(caret, replacement);
                box.CaretIndex = caret + replacement.Length;
                box.SelectRange(caret, replacement.Length);
            };

            prompt.Children[^1] = CodeEditorView.WithButtons(prompt!, findButton, replaceButton, closeButton);
        }
        else
        {
            prompt.Children[^1] = CodeEditorView.WithButtons(prompt!, findButton, closeButton);
        }

        dialog.Show(owner);
        searchBox.Focus();
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

