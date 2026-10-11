using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Conqueror.Net.WebBrowserUi.Services;

/// <summary>
/// Windows implementation of <see cref="IOAuthRedirectService"/> using a custom URI scheme
/// registered in the Windows Registry and <see cref="Process.Start"/> to launch the system browser.
/// </summary>
public sealed class WindowsOAuthRedirectService : IOAuthRedirectService, IDisposable
{
    private const string DefaultScheme = "conqueror";
    private const string DefaultCallbackPath = "oauth-callback";

    private readonly string _scheme;
    private readonly string _callbackPath;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pendingCallbacks = new();
    private bool _isRegistered;

    public string Scheme => _scheme;
    public string CallbackPath => _callbackPath;
    public string CallbackUri => $"{_scheme}://{_callbackPath}";

    public WindowsOAuthRedirectService(string scheme = DefaultScheme, string callbackPath = DefaultCallbackPath)
    {
        _scheme = scheme ?? DefaultScheme;
        _callbackPath = callbackPath ?? DefaultCallbackPath;
    }

    public async Task<bool> RegisterSchemeAsync()
    {
        if (_isRegistered) return true;

        try
        {
            using var schemeKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{_scheme}");
            schemeKey.SetValue("", $"URL:{_scheme} Protocol");
            schemeKey.SetValue("URL Protocol", "");

            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exePath))
            {
                using var defaultIconKey = schemeKey.CreateSubKey("DefaultIcon");
                defaultIconKey.SetValue("", $"\"{exePath}\",1");
            }

            using var commandKey = schemeKey.CreateSubKey(@"shell\open\command");
            commandKey.SetValue("", $"\"{exePath}\" \"%1\"");

            _isRegistered = true;
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[OAuthRedirect] Failed to register scheme: {ex.Message}");
            return false;
        }
    }

    public async Task UnregisterSchemeAsync()
    {
        if (!_isRegistered) return;

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{_scheme}", false);
            _isRegistered = false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[OAuthRedirect] Failed to unregister scheme: {ex.Message}");
        }
    }

    public async Task<string?> StartOAuthFlowAsync(string authorizationUrl, string? state = null)
    {
        if (string.IsNullOrWhiteSpace(authorizationUrl))
        {
            throw new ArgumentException("Authorization URL cannot be empty", nameof(authorizationUrl));
        }

        state ??= Guid.NewGuid().ToString("N");
        var callbackUri = $"{CallbackUri}?state={Uri.EscapeDataString(state)}";

        var separator = authorizationUrl.Contains('?') ? '&' : '?';
        var fullAuthUrl = $"{authorizationUrl}{separator}redirect_uri={Uri.EscapeDataString(callbackUri)}";

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCallbacks[state] = tcs;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fullAuthUrl,
                UseShellExecute = true,
            };

            if (Process.Start(startInfo) == null)
            {
                _pendingCallbacks.TryRemove(state, out _);
                System.Diagnostics.Trace.WriteLine("[OAuthRedirect] Failed to start system browser process");
                return null;
            }

            var timeoutTask = Task.Delay(TimeSpan.FromMinutes(5));
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

            if (completedTask == timeoutTask)
            {
                _pendingCallbacks.TryRemove(state, out _);
                System.Diagnostics.Trace.WriteLine("[OAuthRedirect] OAuth flow timed out");
                return null;
            }

            return await tcs.Task;
        }
        catch (Exception ex)
        {
            _pendingCallbacks.TryRemove(state, out _);
            System.Diagnostics.Trace.WriteLine($"[OAuthRedirect] OAuth flow error: {ex.Message}");
            return null;
        }
    }

    public bool IsOAuthCallback(Uri uri)
    {
        if (uri == null || string.IsNullOrEmpty(uri.Scheme)) return false;

        return uri.Scheme.Equals(_scheme, StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.TrimStart('/').Equals(_callbackPath, StringComparison.OrdinalIgnoreCase);
    }

    public string? ParseCallback(Uri uri)
    {
        if (!IsOAuthCallback(uri)) return null;

        var query = ParseQueryString(uri.Query);
        var code = query["code"];
        var state = query["state"];
        var error = query["error"];

        if (!string.IsNullOrEmpty(error))
        {
            var errorDesc = query["error_description"];
            System.Diagnostics.Trace.WriteLine($"[OAuthRedirect] OAuth error: {error} - {errorDesc}");
            CompleteCallback(state, null);
            return null;
        }

        if (string.IsNullOrEmpty(code))
        {
            System.Diagnostics.Trace.WriteLine("[OAuthRedirect] No authorization code in callback");
            CompleteCallback(state, null);
            return null;
        }

        CompleteCallback(state, code);
        return code;
    }

    private void CompleteCallback(string? state, string? result)
    {
        if (!string.IsNullOrEmpty(state) && _pendingCallbacks.TryRemove(state, out var tcs))
        {
            if (result != null) tcs.TrySetResult(result);
            else tcs.TrySetCanceled();
        }
    }

    public void Dispose()
    {
        foreach (var kvp in _pendingCallbacks) kvp.Value.TrySetCanceled();
        _pendingCallbacks.Clear();
    }

    private static System.Collections.Specialized.NameValueCollection ParseQueryString(string query)
    {
        var result = new System.Collections.Specialized.NameValueCollection();
        if (string.IsNullOrEmpty(query)) return result;

        if (query.StartsWith("?")) query = query[1..];

        foreach (var pair in query.Split('&'))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]);
            var value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
            result[key] = value;
        }

        return result;
    }
}