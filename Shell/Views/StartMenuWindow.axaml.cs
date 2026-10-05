using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Conqueror.Net.Shell.Views;

/// <summary>
/// The Start menu. A second top-level window rather than a popup, so it can sit above the
/// taskbar and above other applications without being clipped to the taskbar's bounds.
/// </summary>
public partial class StartMenuWindow : Window
{
    public StartMenuWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
