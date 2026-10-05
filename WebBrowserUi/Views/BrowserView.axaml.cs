using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Conqueror.Net.WebBrowserUi.ViewModels;

namespace Conqueror.Net.WebBrowserUi.Views;

/// <summary>
/// Hosts the tab's dedicated Chromium control. Each <see cref="BrowserTabViewModel"/>
/// owns its own WebView (Chromium needs a live visual root, so it is created on the
/// UI thread during attach) which is re-parented into the host Border here.
/// </summary>
public partial class BrowserView : UserControl
{
    private BrowserTabViewModel? _attachedVm;

    public BrowserView()
    {
        InitializeComponent();

        // A control realised from a DataTemplate can receive Loaded and DataContext in
        // either order, so hook both. DataContextChanged also fires when the shared
        // ContentControl swaps tabs — detach the old VM so its engine leaves the host
        // before the new tab docks its own.
        Loaded += (_, _) => TryAttach();
        DataContextChanged += (_, _) => SwitchAttachment();
        DetachedFromVisualTree += (_, _) => DetachCurrent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void SwitchAttachment()
    {
        DetachCurrent();
        TryAttach();
    }

    private void DetachCurrent()
    {
        _attachedVm?.Detach();
        _attachedVm = null;
    }

    private void TryAttach()
    {
        var host = this.FindControl<Border>("BrowserHost");

        if (DataContext is BrowserTabViewModel vm && host is not null)
        {
            _attachedVm = vm;
            vm.Attach(host);
        }
    }
}
