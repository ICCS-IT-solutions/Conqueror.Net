using System;
using System.Threading.Tasks;

namespace Conqueror.Net.WebBrowserUi.Services;

/// <summary>
/// Service for handling OAuth 2.0 flows by redirecting to the system browser.
/// Embedded browsers (CEF, WebView2, WKWebView) are blocked by Google, Microsoft, and other
/// OAuth providers. This service opens the authorization URL in the user's default browser
/// and receives the callback via a custom URI scheme.
/// </summary>
public interface IOAuthRedirectService
{
    /// <summary>
    /// Gets the custom URI scheme used for OAuth callbacks (e.g., "conqueror").
    /// </summary>
    string Scheme { get; }

    /// <summary>
    /// Gets the callback path for OAuth redirects (e.g., "oauth-callback").
    /// </summary>
    string CallbackPath { get; }

    /// <summary>
    /// The full callback URI (e.g., "conqueror://oauth-callback").
    /// </summary>
    string CallbackUri { get; }

    /// <summary>
    /// Registers the custom URI scheme with the operating system.
    /// Must be called during application startup.
    /// </summary>
    Task<bool> RegisterSchemeAsync();

    /// <summary>
    /// Unregisters the custom URI scheme. Optional cleanup.
    /// </summary>
    Task UnregisterSchemeAsync();

    /// <summary>
    /// Initiates an OAuth flow by opening the authorization URL in the system browser.
    /// </summary>
    /// <param name="authorizationUrl">The full OAuth authorization URL with all query parameters.</param>
    /// <param name="state">Optional CSRF state parameter. If not provided, a random one is generated.</param>
    /// <returns>The authorization code from the callback, or null if cancelled/failed.</returns>
    Task<string?> StartOAuthFlowAsync(string authorizationUrl, string? state = null);

    /// <summary>
    /// Checks if a URI is an OAuth callback for this application.
    /// </summary>
    /// <param name="uri">The URI to check.</param>
    /// <returns>True if the URI matches the callback scheme and path.</returns>
    bool IsOAuthCallback(Uri uri);

    /// <summary>
    /// Parses the OAuth callback URI and extracts the authorization code.
    /// </summary>
    /// <param name="uri">The callback URI (e.g., "conqueror://oauth-callback?code=xxx&state=yyy").</param>
    /// <returns>The authorization code, or null if not found or state mismatch.</returns>
    string? ParseCallback(Uri uri);
}