using System.IO;
using System.Text.Json;
using Conqueror.Net.WebBrowserUi.ViewModels;

namespace Conqueror.Net.WebBrowserUi.Services;

/// <summary>
/// File-backed extension registry for the embedded Chromium tab.
/// WebViewControl-Avalonia (CEF 120) exposes no extension-host API comparable to
/// CefSharp's RequestContext.LoadExtension — the control offers Address / Title /
/// ZoomPercentage / LoadUrl / EvaluateScript but no extension members (verified via
/// tools/dump-webview-api.ps1). So this service owns discovery + enable/disable
/// state on disk, while BrowserTabViewModel injects each manifest's content scripts
/// after main-frame navigation.
/// </summary>
public interface IExtensionService
{
    Task<IEnumerable<ExtensionInfo>> GetInstalledExtensionsAsync();
    Task<ExtensionInfo> LoadUnpackedExtensionAsync(string manifestPath);
    Task EnableExtensionAsync(string extensionId);
    Task DisableExtensionAsync(string extensionId);
    Task UnloadExtensionAsync(string extensionId);

    /// <summary>
    /// Returns content script file paths from enabled extensions.
    /// If <paramref name="targetUrl"/> is specified, scripts are filtered against 
    /// the match patterns (e.g. *://*.google.com/*) declared in manifest.json.
    /// </summary>
    IReadOnlyList<string> GetEnabledContentScripts(string? targetUrl = null);
}

public record ExtensionInfo(
    string Id,
    string Name,
    string Version,
    string Description,
    bool IsEnabled,
    string? IconPath);