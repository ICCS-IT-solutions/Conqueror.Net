using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Conqueror.Net.Core.Tabs;
using Conqueror.Net.WebBrowserUi.Services;
using Conqueror.Net.WebBrowserUi.Views;
using WebViewControl;

namespace Conqueror.Net.WebBrowserUi.ViewModels;

/// <summary>
/// A web-browser tab. The shell drives this through <see cref="ITabViewModel"/>, exactly as it
/// drives a file tab, so the shared address bar and navigation buttons work on both.
/// </summary>
/// <remarks>
/// The <see cref="WebView"/> control is attached after the view is realised (Chromium can only
/// be created once there is a live visual root), so navigation requests made before that are
/// queued into <see cref="_pendingAddress"/> and replayed on attach.
/// </remarks>
public sealed partial class BrowserTabViewModel : ObservableObject, ITabViewModel, IDisposable
{
    //Capabilities: cut, copy, paste, select all: true.
    //Delete is false: there is no page-level delete semantic, so the shell menu stays off.
    public bool CanNewFile => false;
    public bool CanNewFolder => false;
    public bool CanCut => true;
    public bool CanCopy => true;
    public bool CanPaste => true;
    public bool CanDelete => false;
    public bool CanRename => false;
    public bool CanProperties => false;
    public bool CanSelectAll => true;

    private readonly IExtensionService _extensionService;
    private IRelayCommand? _extensionsCommand;
    public IRelayCommand ExtensionsCommand
    {
        get
        {
            if (_extensionsCommand is null)
            {
                _extensionsCommand = new RelayCommand(OnExtensionsClicked);
            }
            return _extensionsCommand;
        }
    }

    private async void OnExtensionsClicked()
    {
        var dialogVm = new ExtensionManagementDialogViewModel(_extensionService);
        await dialogVm.InitializeCommand.ExecuteAsync(null);
        var dialog = new ExtensionManagementDialog { Title = "Extensions" };
        dialog.DataContext = dialogVm;
        dialog.Show();
    }
    private const string HomeAddress = "about:blank";

    private WebView? _browser;
    private Border? _host;
    private string? _pendingAddress;
    private bool _handlersWired;
    private double _zoomPercentage = 100;

    [ObservableProperty]
    private string _title = "New Tab";

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _canGoBack;

    [ObservableProperty]
    private bool _canGoForward;

    [ObservableProperty]
    private bool _isBusy;

    public BrowserTabViewModel(string? initialAddress = null)
        : this(initialAddress, App.Extensions)
    {
    }

    public BrowserTabViewModel(string? initialAddress, IExtensionService extensionService)
    {
        _extensionService = extensionService ?? throw new ArgumentNullException(nameof(extensionService));
        Address = NormalizeAddress(initialAddress);
    }

    /// <summary>The address-bar text. Binding to this does not navigate; call <see cref="NavigateTo"/>.</summary>
    public string Address { get; set; } = HomeAddress;

    public string Location => Address;

    /// <summary>
    /// Connection Status artwork: a magnifier over a stylised globe, which is XP's stand-in for
    /// the old Internet Explorer "e". Closer to the original than a plain globe.
    /// </summary>
    public string IconKey => "Icon.Xp.Connection";

    public bool IsFileBrowser => false;

    /// <summary>
    /// The shell's search box. A web page has no folder to filter, so the text is kept but
    /// does nothing until a find-in-page is wired up.
    /// </summary>
    public string SearchText { get; set; } = string.Empty;

    /// <summary>Same as <see cref="StatusMessage"/>; the interface name the shell binds to.</summary>
    public string StatusText
    {
        get => StatusMessage;
        set => StatusMessage = value;
    }

    // ---- Page editing (driven by the shell's Edit menu through MainWindowViewModel) ----

    /// <summary>Cuts the page selection via the CEF edit commands; no-op before attach.</summary>
    public void CutPageSelection()
    {
        try
        {
            _browser?.EditCommands.Cut();
        }
        catch (InvalidOperationException)
        {
            // Engine busy or torn down during tab close.
        }
    }

    /// <summary>Copies the page selection via the CEF edit commands; no-op before attach.</summary>
    public void CopyPageSelection()
    {
        try
        {
            _browser?.EditCommands.Copy();
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>Pastes the clipboard into the focused page field; no-op before attach.</summary>
    public void PasteIntoPage()
    {
        try
        {
            _browser?.EditCommands.Paste();
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>Deletes the page selection; no-op before attach.</summary>
    public void DeletePageSelection()
    {
        try
        {
            _browser?.EditCommands.Delete();
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>Selects the whole page; no-op before attach.</summary>
    public void SelectAllInPage()
    {
        try
        {
            _browser?.EditCommands.SelectAll();
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// CEF does not surface page-load progress through this control, so the shell shows an
    /// indeterminate busy state instead of a percentage.
    /// </summary>
    public double? LoadProgress => null;

    public double ZoomPercentage
    {
        get => _zoomPercentage;
        set
        {
            if (SetProperty(ref _zoomPercentage, value) && _browser is not null)
            {
                // WebViewControl's ZoomPercentage is a factor (1.0 = 100%): the setter
                // computes ZoomLevel = log(value, 1.2), so pushing the 100-based display
                // value straight through asks for log_1.2(100) ~= 25 zoom levels — a 100x
                // zoom — instead of the intended one. Divide to convert display -> factor.
                _browser.ZoomPercentage = value / 100.0;
            }
        }
    }

    /// <summary>Raised when a page asks for a new window (target=_blank, window.open).</summary>
    public event Action<string>? NewTabRequested;

    /// <summary>Raised after <see cref="Dispose"/> tears the engine down (shell unhooks popups).</summary>
    public event Action? Disposed;

    /// <summary>
    /// Hosts this tab's own Chromium control inside <paramref name="host"/>.
    /// Each tab owns a dedicated WebView (created lazily on the UI thread) that is
    /// re-parented here — a single ContentControl/DataTemplate reuses its visual, so
    /// sharing one WebView across tabs is what forced every tab onto the same page.
    /// </summary>
    public void Attach(Border host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));

        if (_browser is null)
        {
            _browser = new WebView { AllowDeveloperTools = true };
            WireBrowserEvents(_browser);
            // Library takes a factor (1.0 = 100%), view-model keeps 100-based display units.
            _browser.ZoomPercentage = ZoomPercentage / 100.0;
        }

        // Re-parent our own engine instance into the visible host.
        if (!ReferenceEquals(_browser.Parent, host))
        {
            if (_browser.Parent is Panel oldPanel)
            {
                oldPanel.Children.Remove(_browser);
            }

            if (_browser.Parent is Decorator oldDecorator)
            {
                oldDecorator.Child = null;
            }

            if (_browser.Parent is ContentControl oldContent)
            {
                oldContent.Content = null;
            }

            host.Child = _browser;
        }

        var toLoad = _pendingAddress ?? Address;
        _pendingAddress = null;

        // Only drive navigation when the target differs from what this engine shows,
        // otherwise switching back to a tab would reload it.
        if (!string.Equals(_browser.Address?.TrimEnd('/'), toLoad.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            LoadUrl(toLoad);
        }
        else
        {
            // Still refresh chrome state so the tab strip/address bar match this tab.
            UpdateNavigationState();
            OnPropertyChanged(nameof(Location));
        }
    }

    /// <summary>Removes our WebView from the shared host so the next tab can dock its own.</summary>
    public void Detach()
    {
        if (_host is not null && ReferenceEquals(_browser?.Parent, _host))
        {
            _host.Child = null;
        }

        _host = null;
    }

    private void WireBrowserEvents(WebView browser)
    {
        if (_handlersWired)
        {
            return;
        }

        _handlersWired = true;
        browser.Navigated += OnNavigated;
        browser.TitleChanged += OnTitleChanged;
        browser.LoadFailed += OnLoadFailed;
        browser.PopupOpening += OnPopupOpening;
    }

    public void Dispose()
    {
        if (_browser is not null)
        {
            _browser.Navigated -= OnNavigated;
            _browser.TitleChanged -= OnTitleChanged;
            _browser.LoadFailed -= OnLoadFailed;
            _browser.PopupOpening -= OnPopupOpening;
            if (_browser.Parent is Decorator oldHost)
            {
                oldHost.Child = null;
            }
            try
            {
                _browser.Dispose();
            }
            catch (InvalidOperationException)
            {
                // Engine already torn down during shutdown.
            }

            _browser = null;
        }

        _host = null;
        _handlersWired = false;
        Disposed?.Invoke();
    }

    // ---- ITabViewModel -------------------------------------------------------

    void ITabViewModel.Navigate(string location) => NavigateTo(location);

    void ITabViewModel.GoBack() => GoBack();

    void ITabViewModel.GoForward() => GoForward();

    void ITabViewModel.Reload() => Reload();

    void ITabViewModel.Stop()
    {
        IsBusy = false;
        StatusMessage = "Loading stopped.";
    }

    [RelayCommand]
    public void NavigateTo(string? address) => LoadUrl(NormalizeAddress(address));

    [RelayCommand]
    public void GoBack()
    {
        if (_browser?.CanGoBack == true)
        {
            _browser.GoBack();
        }
    }

    [RelayCommand]
    public void GoForward()
    {
        if (_browser?.CanGoForward == true)
        {
            _browser.GoForward();
        }
    }

    [RelayCommand]
    public void Reload()
    {
        if (_browser is not null)
        {
            IsBusy = true;
            _browser.Reload(false);
        }
    }

    [RelayCommand]
    public void GoHome() => NavigateTo(HomeAddress);

    [RelayCommand]
    private void Stop() => ((ITabViewModel)this).Stop();

    [RelayCommand]
    public void ShowDeveloperTools() => _browser?.ShowDeveloperTools();

    [RelayCommand]
    private void ZoomIn() => ZoomPercentage = Math.Clamp(ZoomPercentage + 10, 25, 500);

    [RelayCommand]
    private void ZoomOut() => ZoomPercentage = Math.Clamp(ZoomPercentage - 10, 25, 500);

    [RelayCommand]
    private void ZoomReset() => ZoomPercentage = 100;

    /// <summary>Runs a snippet of JavaScript in the page, returning its result as text.</summary>
    public async Task<string?> EvaluateAsync(string script)
    {
        if (_browser is null)
        {
            return null;
        }

        try
        {
            return await _browser.EvaluateScript<string>(script, null, TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
            when (ex is WebView.JavascriptException or TimeoutException or InvalidOperationException
            )
        {
            StatusMessage = ex.Message;
            return null;
        }
    }

    private void OnNavigated(string url, string frameName)
    {
        // Sub-frames report too; only the main frame owns the address bar.
        if (!string.IsNullOrEmpty(frameName))
        {
            return;
        }

        Address = url;
        OnPropertyChanged(nameof(Location));
        StatusMessage = string.Empty;
        IsBusy = false;
        UpdateNavigationState();

        RefreshTitleFromDocument();
        InjectExtensionContentScripts();
    }

    private void OnTitleChanged()
    {
        if (_browser is null)
        {
            return;
        }

        var title = _browser.Title;
        if (!string.IsNullOrWhiteSpace(title))
        {
            Title = title;
        }
    }

    /// <summary>
    /// CEF's TitleChanged does not always arrive for pages whose title is set by script or
    /// arrives after the navigation callback, which would leave the tab stuck on "New Tab".
    /// Asking the document directly is cheap and makes the title reliable.
    /// </summary>
    private async void RefreshTitleFromDocument()
    {
        if (_browser is null)
        {
            return;
        }

        try
        {
            // Give the parser a moment; document.title is empty until the head is parsed.
            await Task.Delay(250);

            if (_browser is null)
            {
                return;
            }

            var fromDocument = await _browser.EvaluateScript<string>(
                "document.title",
                null,
                TimeSpan.FromSeconds(5)
            );

            if (!string.IsNullOrWhiteSpace(fromDocument))
            {
                Title = fromDocument.Trim();
            }
        }
        catch (Exception ex)
            when (ex
                    is WebView.JavascriptException
                        or TimeoutException
                        or InvalidOperationException
                        or TaskCanceledException
            )
        {
            // Keep whatever TitleChanged gave us.
        }
    }

    private void OnLoadFailed(string url, int errorCode, string frameName)
    {
        if (!string.IsNullOrEmpty(frameName))
        {
            return;
        }

        IsBusy = false;
        StatusMessage = $"Cannot display '{url}' (error {errorCode}).";
        UpdateNavigationState();
    }

    /// <summary>
    /// Runs each enabled extension's content scripts in the page. This is the injection
    /// half of the extension handler: WebViewControl exposes no extension host, so the
    /// closest faithful behaviour is executing manifest content_scripts via EvaluateScript.
    /// Failures are swallowed per-file — one broken script must not break the page.
    /// </summary>
    private async void InjectExtensionContentScripts()
    {
        if (_browser is null)
        {
            return;
        }

        IReadOnlyList<string> scripts;
        try
        {
            scripts = _extensionService.GetEnabledContentScripts(Address);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var path in scripts)
        {
            string js;
            try
            {
                js = await File.ReadAllTextAsync(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(js))
            {
                continue;
            }

            try
            {
                _browser.ExecuteScript(js, null);
            }
            catch (Exception ex) when (ex is InvalidOperationException or WebView.JavascriptException)
            {
                // Engine busy or script error — keep the page alive.
            }
        }
    }

    private void OnPopupOpening(string url)
    {
        // Suppress CEF's own window and open it as one of our tabs instead.
        if (!string.IsNullOrWhiteSpace(url))
        {
            NewTabRequested?.Invoke(url);
        }
    }

    private void UpdateNavigationState()
    {
        CanGoBack = _browser?.CanGoBack ?? false;
        CanGoForward = _browser?.CanGoForward ?? false;
    }

    private void LoadUrl(string address)
    {
        if (_browser is null)
        {
            _pendingAddress = address;
            return;
        }

        IsBusy = true;

        // LoadUrl is the explicit navigation entry point. Assigning the Address property
        // only reflects the current location and does not reliably start a load.
        _browser.LoadUrl(address, null);
    }

    /// <summary>Turns typed text into something Chromium can open.</summary>
    public static string NormalizeAddress(string? input)
    {
        var text = (input ?? string.Empty).Trim();

        if (string.IsNullOrEmpty(text))
        {
            return HomeAddress;
        }

        // Bare words are a search, matching what a browser address bar expects.
        if (
            !text.Contains("://", StringComparison.Ordinal)
            && !text.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
        )
        {
            var looksLikeHostOrPath =
                text.Contains('.') || text.Contains('\\') || text.Contains(':');
            text = looksLikeHostOrPath
                ? "https://" + text
                : "https://duckduckgo.com/?q=" + Uri.EscapeDataString(text);
        }

        return text;
    }
}
