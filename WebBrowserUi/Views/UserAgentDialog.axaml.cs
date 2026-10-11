using Avalonia.Controls;
using Avalonia.Interactivity;
using Conqueror.Net.WebBrowserUi.ViewModels;

namespace Conqueror.Net.WebBrowserUi.Views;

public partial class UserAgentDialog : Window
{
    public UserAgentDialog()
    {
        InitializeComponent();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is UserAgentDialogViewModel vm)
        {
            Close(vm.UserAgent);
        }
        else
        {
            Close(null);
        }
    }
}