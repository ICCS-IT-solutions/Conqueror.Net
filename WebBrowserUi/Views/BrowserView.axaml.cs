using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Conqueror.Net.WebBrowserUi.ViewModels;
using WebViewControl;

namespace Conqueror.Net.WebBrowserUi.Views;

/// <summary>
/// Hosts the embedded Chromium control. Chromium can only be created once the control has
/// been attached to a live visual root, so the view-model is wired up on Loaded and any
/// navigation requested beforehand is replayed there.
/// </summary>
/// <remarks>
/// Process-wide CEF settings are applied in <see cref="App.ConfigureChromium"/> rather than
/// here: this control starts the Chromium engine inside its own constructor, so by the time
/// a BrowserView body runs it is already too late to set them.
/// </remarks>
public partial class BrowserView : UserControl
{
    public BrowserView()
    {
        InitializeComponent();

        // A control realised from a DataTemplate can receive Loaded and DataContext in
        // either order, so hook both and let the view-model's own guard make a repeated
        // attempt harmless. Relying on Loaded alone silently skipped Attach() entirely.
        Loaded += (_, _) => TryAttach();
        DataContextChanged += (_, _) => TryAttach();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void TryAttach()
    {
        // The generated Name="Browser" field is not yet populated this early in the
        // template's lifetime, so resolve the control from the live name scope instead.
        var browser = this.FindControl<WebView>("Browser");

        if (DataContext is BrowserTabViewModel vm && browser is not null)
        {
            vm.Attach(browser);
        }
    }
}
