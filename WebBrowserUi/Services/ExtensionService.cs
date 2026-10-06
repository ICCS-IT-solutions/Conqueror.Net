using System.IO;
using System.Text.Json;

namespace Conqueror.Net.WebBrowserUi.Services;

/// <summary>
/// File-backed extension registry for the embedded Chromium tab.
/// The service owns discovery + enable/disable state on disk; loading into the engine is
/// CefExtensionHost's job — it feeds each enabled root directory to
/// CefRequestContext.LoadExtension on the global request context (every WebViewControl tab
/// is created with a null request-context factory, i.e. on that global context), which
/// makes CEF itself the extension host: background pages run, extension APIs exist,
/// declarative/webRequest permissions apply, and manifest content_scripts are injected at
/// the times the manifest asks for. Manual content-script execution survives only as the
/// fallback for roots CEF refused to load (see GetEnabledContentScriptGroups).
/// </summary>
public interface IExtensionService
{
    Task<IEnumerable<ExtensionInfo>> GetInstalledExtensionsAsync();
    Task<ExtensionInfo> LoadUnpackedExtensionAsync(string manifestPath);
    Task EnableExtensionAsync(string extensionId);
    Task DisableExtensionAsync(string extensionId);
    Task UnloadExtensionAsync(string extensionId);

    /// <summary>
    /// Absolute root directories (each containing a manifest.json) of every enabled
    /// extension — exactly the inputs CefRequestContext.LoadExtension wants.
    /// </summary>
    IReadOnlyList<string> GetEnabledExtensionRoots();

    /// <summary>
    /// Content-script groups (one per extension root) from enabled extensions, filtered
    /// against <paramref name="targetUrl"/> when given. Only extensions CEF could not
    /// load natively should be injected from here; a natively hosted extension runs its
    /// own scripts at the manifest-declared times and must not run them twice.
    /// </summary>
    IReadOnlyList<ContentScriptGroup> GetEnabledContentScriptGroups(string? targetUrl = null);

    /// <summary>Raised after any change to the enabled set (toggle, install, uninstall).</summary>
    event Action? ExtensionsChanged;
}

/// <summary>Content scripts of a single extension root, in manifest execution order.</summary>
public record ContentScriptGroup(string RootDirectory, IReadOnlyList<string> ScriptFiles);

public record ExtensionInfo(
    string Id,
    string Name,
    string Version,
    string Description,
    bool IsEnabled,
    string? IconPath);