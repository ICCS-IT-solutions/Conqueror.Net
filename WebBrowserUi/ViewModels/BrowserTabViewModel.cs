using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Conqueror.Net.Core.Tabs;
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
public sealed partial class BrowserTabViewModel : ObservableObject, ITabViewModel
{
    private const string HomeAddress = "about:blank";

    private WebView? _browser;
    private string? _pendingAddress;
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
    {
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
                _browser.ZoomPercentage = value;
            }
        }
    }

    /// <summary>Raised when a page asks for a new window (target=_blank, window.open).</summary>
    public event Action<string>? NewTabRequested;

    /// <summary>Wires the VM to the real Chromium control and replays any queued navigation.</summary>
    public void Attach(WebView browser)
    {
        if (_browser is not null)
        {
            return;
        }

        _browser = browser;

        browser.Navigated += OnNavigated;
        browser.TitleChanged += OnTitleChanged;
        browser.LoadFailed += OnLoadFailed;
        browser.PopupOpening += OnPopupOpening;

        browser.ZoomPercentage = ZoomPercentage;

        var toLoad = _pendingAddress ?? Address;
        _pendingAddress = null;
        LoadUrl(toLoad);
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
    private void ShowDeveloperTools() => _browser?.ShowDeveloperTools();

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
