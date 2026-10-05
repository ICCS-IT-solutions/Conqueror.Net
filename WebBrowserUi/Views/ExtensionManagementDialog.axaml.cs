using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Conqueror.Net.WebBrowserUi.ViewModels;

namespace Conqueror.Net.WebBrowserUi.Views;

public partial class ExtensionManagementDialog : Window
{
    public ExtensionManagementDialog()
    {
        InitializeComponent();
    }

    // Handled via click event to close the window instance directly
    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>
    /// Opens a file picker for an unpacked manifest.json and forwards the path to the
    /// dialog view-model. Kept in code-behind because Avalonia's StorageProvider lives
    /// on the Window (TopLevel), not in the view-model, matching the Close_Click pattern.
    /// </summary>
    private async void Install_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ExtensionManagementDialogViewModel vm)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select extension manifest (manifest.json)",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Extension manifest") { Patterns = ["manifest.json"] }],
        });

        if (files.Count > 0)
        {
            await vm.InstallFromManifestCommand.ExecuteAsync(files[0].Path.LocalPath);
        }
    }
}