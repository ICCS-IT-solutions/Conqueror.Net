using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Conqueror.Net.Shell.ViewModels;

namespace Conqueror.Net.Shell.Views;

/// <summary>
/// The taskbar: a decorationless, always-on-top strip pinned to the bottom of the screen.
/// </summary>
public partial class TaskbarWindow : Window
{
    public TaskbarWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
