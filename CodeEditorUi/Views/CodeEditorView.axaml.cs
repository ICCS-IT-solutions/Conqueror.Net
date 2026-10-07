using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Conqueror.Net.CodeEditorUi.ViewModels;

namespace Conqueror.Net.CodeEditorUi.Views;

/// <summary>
/// The editor tab. Owns no file logic - that lives in the view-model - and only adds the
/// TextBox registration, Ctrl+S keybinding and the XP-style dialog hooks.
/// </summary>
public partial class CodeEditorView : UserControl
{
    private CodeEditorViewModel? _subscribedVm;

    public CodeEditorView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private CodeEditorViewModel? Vm => DataContext as CodeEditorViewModel;

    /// <summary>
    /// Three-way Save / Don't save / Cancel prompt for tab close. The first button is
    /// default, the last is cancel; closing with the X cancels.
    /// </summary>
    private async Task<int> PromptThreeWayAsync(
        string message,
        string title,
        string first,
        string second,
        string third)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return 1;
        }

        var dialog = BuildDialog(owner, title, message);
        var firstButton = NewDialogButton(first, isDefault: true, isCancel: false);
        var secondButton = NewDialogButton(second, isDefault: false, isCancel: false);
        var thirdButton = NewDialogButton(third, isDefault: false, isCancel: true);
        firstButton.Click += (_, _) => dialog.Close(0);
        secondButton.Click += (_, _) => dialog.Close(1);
        thirdButton.Click += (_, _) => dialog.Close(2);
        if (dialog.Content is StackPanel panel)
        {
            dialog.Content = WithButtons(panel, firstButton, secondButton, thirdButton);
        }

        // ShowDialog<int?> yields null when the window is closed with the X; map that
        // to Cancel (2) so an accidental close never discards edits.
        var result = await dialog.ShowDialog<int?>(owner);
        return result ?? 2;
    }

    /// <summary>Save-As picker: suggests the current name in the file's own folder.</summary>
    private async Task<string?> RequestSaveAsPathAsync(string? suggestedName)
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            return null;
        }

        IStorageFolder? folder = null;
        var current = Vm?.FilePath;
        if (!string.IsNullOrWhiteSpace(current))
        {
            try
            {
                var directory = System.IO.Path.GetDirectoryName(current);
                folder = directory is null
                    ? null
                    : await top.StorageProvider.TryGetFolderFromPathAsync(directory);
            }
            catch (Exception)
            {
                folder = null;
            }
        }

        var options = new FilePickerSaveOptions
        {
            Title = "Save As",
            SuggestedFileName = string.IsNullOrWhiteSpace(suggestedName) ? "Untitled.txt" : suggestedName,
            SuggestedStartLocation = folder,
            FileTypeChoices =
            [
                new FilePickerFileType("Text files") { Patterns = ["*.txt"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        };

        var file = await top.StorageProvider.SaveFilePickerAsync(options);
        return file?.TryGetLocalPath();
    }

    /// <summary>Saves on Ctrl+S without moving focus out of the editor.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.S
            && e.KeyModifiers.HasFlag(KeyModifiers.Control)
            && Vm is not null)
        {
            Vm.SaveCommand.Execute(null);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedVm is not null)
        {
            _subscribedVm.EditorBoxProvider = null;
            _subscribedVm.ConfirmAsync = null;
            _subscribedVm.RequestSaveAsPath = null;
            _subscribedVm.ConfirmDiscardAsync = null;
            _subscribedVm = null;
        }

        if (Vm is null)
        {
            return;
        }

        _subscribedVm = Vm;

        // Registers the live editor TextBox so the VM's cut/copy/paste/delete/select-all
        // commands act on the selection rather than the whole buffer.
        Vm.EditorBoxProvider = () => this.FindControl<TextBox>("EditorBox");
        Vm.ConfirmAsync = ConfirmAsync;
        Vm.RequestSaveAsPath = RequestSaveAsPathAsync;
        Vm.ConfirmDiscardAsync = ConfirmDiscardAsync;
    }

    private async Task<bool> ConfirmDiscardAsync()
    {
        var name = Vm?.Location ?? "the file";
        var choice = await PromptThreeWayAsync(
            $"Save changes to '{name}' before closing?",
            "Unsaved changes",
            "Save",
            "Don't save",
            "Cancel");

        return choice switch
        {
            0 => Vm is not null && await SaveThenConfirmAsync(),
            1 => true,
            _ => false,
        };
    }

    private async Task<bool> SaveThenConfirmAsync()
    {
        if (Vm is null)
        {
            return false;
        }

        await Vm.SaveCommand.ExecuteAsync(null);
        return !Vm.IsDirty;
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

        var dialog = BuildDialog(owner, title, message);
        var yesButton = NewDialogButton("Yes", isDefault: true, isCancel: false);
        var noButton = NewDialogButton("No", isDefault: false, isCancel: true);
        yesButton.Click += (_, _) => dialog.Close(true);
        noButton.Click += (_, _) => dialog.Close(false);
        dialog.Content = dialog.Content is StackPanel panel
            ? WithButtons(panel, yesButton, noButton)
            : dialog.Content;

        return await dialog.ShowDialog<bool>(owner);
    }

    internal static Button NewDialogButton(string content, bool isDefault, bool isCancel)
    {
        var button = new Button
        {
            Content = content,
            MinWidth = 75,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsDefault = isDefault,
            IsCancel = isCancel,
        };
        button.Classes.Add("xpButton");
        return button;
    }

    internal static Window BuildDialog(Window owner, string title, string message)
    {
        return new Window
        {
            Title = title,
            CanResize = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            MaxWidth = 480,
            Content = new StackPanel
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
                },
            },
        };
    }

    internal static StackPanel WithButtons(StackPanel panel, params Button[] buttons)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 6,
        };

        foreach (var button in buttons)
        {
            row.Children.Add(button);
        }

        panel.Children.Add(row);
        return panel;
    }
}
