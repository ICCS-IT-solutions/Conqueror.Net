using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Conqueror.Net.Core;

namespace Conqueror.Net.WindowRoot;

/// <summary>
/// Tools ▸ Editor File Types…: which extensions the built-in editor claims from the OS.
/// </summary>
/// <remarks>
/// Built in code the way the file browser builds its confirmations — a small fixed-size
/// dialog with no view-model of its own. The list is edited as a copy; only OK writes it
/// through <see cref="EditorFileTypes.Set"/> (which persists), so Cancel or the X leaves
/// both the running session and the settings file untouched. The window root refreshes the
/// open panes' Edit verb grey states after a successful OK.
/// </remarks>
public sealed class EditorFileTypesDialog : Window
{
    private readonly ObservableCollection<string> _types = [];
    private readonly ListBox _list;
    private readonly TextBox _newType;
    private readonly TextBlock _hint;
    private readonly Button _removeButton;

    public EditorFileTypesDialog()
    {
        Title = "Editor File Types";
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        foreach (var extension in EditorFileTypes.Sorted)
        {
            _types.Add(extension);
        }

        _list = new ListBox
        {
            ItemsSource = _types,
            MinWidth = 380,
            Height = 220,
        };
        _list.SelectionChanged += (_, _) => _removeButton.IsEnabled = _list.SelectedIndex >= 0;

        _newType = new TextBox
        {
            Watermark = "e.g. .ts",
            VerticalAlignment = VerticalAlignment.Center,
        };
        _newType.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter)
            {
                OnAddClick(_newType, args);
                args.Handled = true;
            }
        };

        _removeButton = MakeButton("Remove");
        _removeButton.IsEnabled = false;
        _removeButton.Click += OnRemoveClick;

        var addButton = MakeButton("Add");
        addButton.Click += OnAddClick;

        _hint = new TextBlock
        {
            Text = string.Empty,
            Foreground = new SolidColorBrush(Color.Parse("#A11B1B")),
        };

        var defaultsButton = MakeButton("Defaults");
        defaultsButton.HorizontalAlignment = HorizontalAlignment.Left;
        defaultsButton.Click += OnDefaultsClick;

        var okButton = MakeButton("OK", isDefault: true);
        okButton.Margin = new Thickness(0, 0, 6, 0);
        okButton.Click += OnOkClick;

        var cancelButton = MakeButton("Cancel", isCancel: true);
        cancelButton.Click += (_, _) => Close(false);

        var addButtonRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Children = { _newType, addButton, _removeButton },
        };
        Grid.SetColumn(addButton, 1);
        Grid.SetColumn(_removeButton, 2);

        var buttonRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Children = { defaultsButton, okButton, cancelButton },
        };
        Grid.SetColumn(okButton, 1);
        Grid.SetColumn(cancelButton, 2);

        Content = new StackPanel
        {
            Margin = new Thickness(14),
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text =
                        "Files with these extensions open in the built-in editor — on "
                        + "double-click and from Edit in the context menu. Every other "
                        + "type keeps its normal Windows association.",
                    TextWrapping = TextWrapping.Wrap,
                },
                _list,
                addButtonRow,
                _hint,
                buttonRow,
            },
        };
    }

    private static Button MakeButton(string content, bool isDefault = false, bool isCancel = false)
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

    private void OnAddClick(object? sender, RoutedEventArgs e)
    {
        var extension = EditorFileTypes.Normalize(_newType.Text);
        if (extension is null)
        {
            // An idle press on Add with an empty box says nothing worth showing; real input
            // that failed normalisation gets the hint.
            _hint.Text = "An extension looks like .txt or .cs.";
            _hint.IsVisible = !string.IsNullOrWhiteSpace(_newType.Text);
            return;
        }

        _hint.IsVisible = false;
        _newType.Text = string.Empty;

        var existing = _types.FirstOrDefault(t =>
            string.Equals(t, extension, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            _list.SelectedItem = existing;
            return;
        }

        _types.Add(extension);
        SortInPlace();
        _list.SelectedItem = extension;
    }

    private void OnRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (_list.SelectedItem is string extension)
        {
            _types.Remove(extension);
            _hint.IsVisible = false;
        }
    }

    private void OnDefaultsClick(object? sender, RoutedEventArgs e)
    {
        _types.Clear();
        foreach (var extension in EditorFileTypes.Shipped)
        {
            _types.Add(extension);
        }

        SortInPlace();
        _hint.IsVisible = false;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        EditorFileTypes.Set(_types);
        Close(true);
    }

    private void SortInPlace()
    {
        var sorted = _types.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToArray();
        _types.Clear();
        foreach (var extension in sorted)
        {
            _types.Add(extension);
        }
    }
}