using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Threading;
using WebViewControl;
using Xilium.CefGlue;
using Xilium.CefGlue.Common;
using Xilium.CefGlue.Common.Shared;

namespace Conqueror.Net.WebBrowserUi.Services;

/// <summary>
/// Hosts enabled extensions inside CEF itself by feeding each enabled root directory to
/// <c>CefRequestContext.LoadExtension</c> on the global request context — the context every
/// WebViewControl tab runs on (ChromiumBrowser is constructed with a null request-context
/// factory). CEF then is the real extension host: background pages run, extension APIs
/// exist, and manifest <c>content_scripts</c> are injected by Chromium at the times the
/// manifest declares.
///
/// Manual ExecuteScript injection survives only as the fallback for roots CEF refused to
/// load (see <see cref="NeedsFallback"/>); BrowserTabViewModel consults it before touching
/// a page so natively hosted extensions never run their scripts twice.
///
/// Threading: every state transition runs on the Avalonia UI thread — which is CEF's
/// browser UI thread in this hosting — so LoadExtension and the handler callbacks need no
/// marshalling. The lock still guards the tables because service events may originate
/// from any thread.
/// </summary>
public static class CefExtensionHost
{
    private enum RootState
    {
        Loading,
        Loaded,
        Failed,
    }

    /// <summary>One handler per LoadExtension call; CEF keeps it alive while the extension loads.</summary>
    private sealed class Handler : CefExtensionHandler
    {
        private readonly string _rootDirectory;

        public Handler(string rootDirectory) => _rootDirectory = rootDirectory;

        protected override void OnExtensionLoaded(CefExtension extension) =>
            HostLoaded(_rootDirectory, extension);

        protected override void OnExtensionLoadFailed(CefErrorCode result) =>
            HostLoadFailed(_rootDirectory, result);

        protected override void OnExtensionUnloaded(CefExtension extension) =>
            HostUnloaded(_rootDirectory, extension);

        protected override bool OnBeforeBackgroundBrowser(
            CefExtension extension,
            string url,
            ref CefClient client,
            CefBrowserSettings settings)
        {
            // Returning true suppresses the hidden background browser. CEF 120's
            // background-page path spawns a CefGlue-managed renderer that faults
            // with an AV at coreclr+0x1d45dc on this runtime, killing every page.
            // Content scripts (the ad-blocking half) are injected by the browser
            // process and unaffected; the background page is sacrificed to keep
            // the tab alive.
            Log($"background page for '{_rootDirectory}' suppressed ({url}) — content scripts only");
            return true;
        }
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, RootState> States = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Handler> Handlers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> ExtensionIds = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> UnloadWhenLoaded = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<TaskCompletionSource<bool>> SettledWaiters = new();

    /// <summary>Alive for the process lifetime; stands in for CEF's "no client yet" pointer.</summary>
    private static readonly SentinelClient Sentinel = new();

    private static bool _sentinelInstalled;

    // Scheme names WebViewLoader would normally register on the CefSettings — reproduced here
    // so that local://, embedded:// and custom:// intercepts keep working when we pre-initialise.
    private static readonly string[] CustomSchemes = new[]
    {
        "local",
        "embedded",
        "custom",
        Uri.UriSchemeHttp,
        Uri.UriSchemeHttps,
    };

    /// <summary>
    /// WebViewControl's <see cref="SchemeHandlerFactory"/> is internal, so we provide an
    /// identical null-returning factory: it satisfies <c>RegisterSchemeHandlerFactory</c> without
    /// hijacking built-in http/https handlers (CEF falls back to them when Create returns null).
    /// </summary>
    private sealed class NullSchemeHandlerFactory : CefSchemeHandlerFactory
    {
        protected override CefResourceHandler? Create(CefBrowser browser, CefFrame frame, string schemeName, CefRequest request)
        {
            return null;
        }
    }

    /// <summary>
    /// Pre-initialises the CEF engine with <c>CefSettings.NoSandbox = true</c> before any
    /// <see cref="WebView"/> is constructed.
    ///
    /// WebViewControl's own <c>WebViewLoader.Initialize</c> creates the <see cref="CefSettings"/>
    /// that <c>CefRuntimeLoader.InternalInitialize</c> feeds to <c>CefRuntime.Initialize</c>.
    /// On Windows that method never flips <c>NoSandbox</c>, so the only way to reach the
    /// CefGlue BrowserProcess subprocess is to let CEF propagate <c>--no-sandbox</c> to every
    /// subprocess command line itself — which only happens when the <see cref="CefSettings"/>
    /// flag is set.
    ///
    /// This method replicates exactly what <see cref="WebViewLoader.Initialize"/> would do
    /// (same CachePath, LogFile, colours, switches, schemes) but with the extra <c>NoSandbox</c>
    /// bit. When the first <see cref="WebView"/> is later built it observes
    /// <c>CefRuntimeLoader.IsLoaded == true</c> and skips its own initialisation, so there is
    /// no double-init.
    /// </summary>
    public static void PreInitialize()
    {
        if (CefRuntimeLoader.IsLoaded)
        {
            return;
        }

        try
        {
            var gs = WebView.Settings;

            var logSeverity = string.IsNullOrWhiteSpace(gs.LogFile)
                              ? CefLogSeverity.Disable
                              : gs.EnableErrorLogOnly
                                  ? CefLogSeverity.Error
                                  : CefLogSeverity.Verbose;

            var cefSettings = new CefSettings
            {
                CachePath              = gs.CachePath,
                LogFile                = gs.LogFile,
                LogSeverity            = logSeverity,
                BackgroundColor        = new CefColor((uint)gs.BackgroundColor.ToArgb()),
                NoSandbox              = true,          // the whole point — makes CEF add --no-sandbox to every subprocess
                WindowlessRenderingEnabled = gs.OsrEnabled,
                UserAgent              = gs.UserAgent,
                RemoteDebuggingPort    = 0,
            };

            // Flags that WebViewLoader would have appended via AddCommandLineSwitch plus the
            // sandbox flag (harmless on the browser process; propagated to children via NoSandbox).
            var flags = new List<KeyValuePair<string, string>>(gs.CommandLineSwitches)
            {
                new("no-sandbox", null!),
                new("disable-background-timer-throttling", null!),
                new("disable-gpu", null!),
            };

            var customSchemes = CustomSchemes.Select(s => new CustomScheme
            {
                SchemeName           = s,
                SchemeHandlerFactory = new NullSchemeHandlerFactory(),
            }).ToArray();

            CefRuntimeLoader.Initialize(cefSettings, flags.ToArray(), customSchemes);

            // CefRuntimeLoader.Load is internal — invoke it via reflection to trigger
            // InternalInitialize, which calls CefRuntime.Load + CefRuntime.Initialize.
            var load = typeof(CefRuntimeLoader).GetMethod("Load",
                BindingFlags.NonPublic | BindingFlags.Static);
            load?.Invoke(null, new object?[] { null });

            Log("PreInitialize: CEF initialised with NoSandbox=true (subprocesses will inherit --no-sandbox)");
        }
        catch (Exception ex)
        {
            Log($"PreInitialize failed: {ex.Message}");
        }
    }

    /// <summary>No-op client: every handler getter keeps its default (null) behaviour.</summary>
    private sealed class SentinelClient : CefClient
    {
    }

    /// <summary>
    /// CefGlue 120.6099.215 defect-workaround. CEF hands out a NULL
    /// <c>cef_client_t*</c> to the extension-handler callbacks
    /// (<c>on_before_background_browser</c>, <c>on_before_browser</c>) to mean "the
    /// handler has not provided a client yet" — exactly what CEF's own default handler
    /// relies on. CefGlue's trampoline resolves that pointer through
    /// <c>CefClient.FromNative</c>, whose lookup helper calls a method on the result
    /// even when the lookup failed, so the first MV2 background page (uBO) used to crash
    /// the whole process with NullReferenceException before our override ever ran.
    ///
    /// Registering a sentinel managed client under key 0 makes the lookup succeed. The
    /// trampoline then behaves as designed: unless the handler swaps the client, nothing
    /// is written back, CEF sees NULL and follows its supported "no custom client"
    /// default. The sentinel's refcount is decremented on each such lookup but never
    /// reaches zero from its starting value, so it is never released.
    /// </summary>
    private static void InstallNullClientSentinel()
    {
        lock (Gate)
        {
            if (_sentinelInstalled)
            {
                return;
            }

            _sentinelInstalled = true;
        }

        try
        {
            var field = typeof(CefClient).GetField("_roots", BindingFlags.NonPublic | BindingFlags.Static);
            if (field?.GetValue(null) is not IDictionary roots)
            {
                Log("CefClient null-lookup sentinel NOT installed: _roots field not found");
                return;
            }

            // Same monitor CefGlue locks around this dictionary.
            lock (roots)
            {
                if (!roots.Contains((IntPtr)0))
                {
                    roots[(IntPtr)0] = Sentinel;
                }
            }

            Log("CefClient null-lookup sentinel installed");
        }
        catch (Exception ex)
        {
            Log($"CefClient null-lookup sentinel failed: {ex.Message}");
        }
    }


    /// <summary>
    /// Reconciles the native host with the service's enabled set: loads every enabled root
    /// CEF has not accepted yet and unloads roots that were disabled or uninstalled.
    /// Safe to call repeatedly; the first call must come after a WebView exists (the engine
    /// has to be running), which BrowserTabViewModel does right after constructing one.
    /// </summary>
    public static void Sync(IExtensionService service)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            RunSync(service);
        }
        else
        {
            Dispatcher.UIThread.Post(() => RunSync(service));
        }
    }

    /// <summary>
    /// Completes once no root is mid-load (all loaded, failed or gone). Returns a
    /// completed task when the host is already settled, so callers can await it
    /// unconditionally. Race it against a timeout when a wedge must not block anything.
    /// </summary>
    public static Task WaitForSettledAsync()
    {
        lock (Gate)
        {
            if (!States.Values.Any(state => state == RootState.Loading))
            {
                return Task.CompletedTask;
            }

            var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            SettledWaiters.Add(waiter);
            return waiter.Task;
        }
    }

    /// <summary>
    /// True only for roots CEF actively refused to load — the sole case where manual
    /// content-script injection may proceed. Loading/Loaded/unknown roots are CEF's own
    /// business and must not be injected from the outside.
    /// </summary>
    public static bool NeedsFallback(string rootDirectory)
    {
        lock (Gate)
        {
            return States.TryGetValue(rootDirectory, out var state) && state == RootState.Failed;
        }
    }

    private static void RunSync(IExtensionService service)
    {
        // Must be in place before any LoadExtension can trigger a background-page
        // callback (uBO's MV2 background page fires one during load).
        InstallNullClientSentinel();

        IReadOnlyList<string> enabled;
        try
        {
            enabled = service.GetEnabledExtensionRoots();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"could not enumerate enabled roots: {ex.Message}");
            return;
        }

        var context = GetGlobalContext();
        if (context is null)
        {
            return;
        }

        List<string> toLoad;
        List<string> toUnload;
        lock (Gate)
        {
            var enabledSet = new HashSet<string>(enabled, StringComparer.OrdinalIgnoreCase);
            toLoad = enabled.Where(root => !States.ContainsKey(root)).ToList();
            toUnload = States.Keys.Where(root => !enabledSet.Contains(root)).ToList();
        }

        foreach (var root in toUnload)
        {
            UnloadRoot(root);
        }

        foreach (var root in toLoad)
        {
            LoadRoot(context, root);
        }
    }

    private static void LoadRoot(CefRequestContext context, string root)
    {
        if (!Directory.Exists(root))
        {
            lock (Gate)
            {
                States[root] = RootState.Failed;
            }

            Log($"root directory missing: {root}");
            NotifySettled();
            return;
        }

        var handler = new Handler(root);
        lock (Gate)
        {
            States[root] = RootState.Loading;
            Handlers[root] = handler;
        }

        Log($"loading '{root}' into the global request context...");
        try
        {
            context.LoadExtension(root, null, handler);
        }
        catch (Exception ex)
        {
            lock (Gate)
            {
                States[root] = RootState.Failed;
            }

            Log($"LoadExtension threw for '{root}': {ex.Message} (manual fallback will serve it)");
            NotifySettled();
        }
    }

    private static void UnloadRoot(string root)
    {
        string? unloadId = null;

        lock (Gate)
        {
            if (!States.TryGetValue(root, out var state))
            {
                return;
            }

            if (state == RootState.Failed)
            {
                // Nothing ever loaded — just forget it so a re-enable retries.
                States.Remove(root);
                Handlers.Remove(root);
                return;
            }

            if (state == RootState.Loading)
            {
                // The load completes first; HostLoaded unloads it immediately after.
                UnloadWhenLoaded.Add(root);
                return;
            }

            if (ExtensionIds.TryGetValue(root, out var id))
            {
                unloadId = id;
            }
        }

        if (unloadId is not null)
        {
            UnloadById(unloadId);
        }
    }

    private static void UnloadById(string id)
    {
        try
        {
            var context = GetGlobalContext();
            if (context is null)
            {
                return;
            }

            CefExtension? extension = null;
            try
            {
                extension = context.GetExtension(id);
            }
            catch (Exception)
            {
                // Already gone — treated as unloaded below.
            }

            if (extension is null)
            {
                ForgetId(id);
                NotifySettled();
                return;
            }

            extension.Unload();
            // HostUnloaded (CEF callback) clears the tables from here.
        }
        catch (Exception ex)
        {
            Log($"unload of {id} failed: {ex.Message}");
            ForgetId(id);
            NotifySettled();
        }
    }

    /// <summary>Drops every table entry that refers to a specific extension id.</summary>
    private static void ForgetId(string id)
    {
        lock (Gate)
        {
            var roots = ExtensionIds
                .Where(pair => string.Equals(pair.Value, id, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key)
                .ToList();

            foreach (var root in roots)
            {
                States.Remove(root);
                Handlers.Remove(root);
                ExtensionIds.Remove(root);
                UnloadWhenLoaded.Remove(root);
            }
        }
    }

    // ---- CEF callback entry points (browser UI thread) --------------------------

    private static void HostLoaded(string root, CefExtension extension)
    {
        var id = extension.Identifier;
        var unloadImmediately = false;

        lock (Gate)
        {
            if (!States.ContainsKey(root) || UnloadWhenLoaded.Remove(root))
            {
                // Disabled while the load was in flight — ship it straight back out.
                unloadImmediately = true;
            }
            else
            {
                States[root] = RootState.Loaded;
                ExtensionIds[root] = id;
            }
        }

        if (unloadImmediately)
        {
            Log($"'{root}' ({id}) finished loading after being disabled — unloading again");
            UnloadById(id);
            return;
        }

        Log($"'{root}' loaded natively as {id}");
        NotifySettled();
    }

    private static void HostLoadFailed(string root, CefErrorCode result)
    {
        lock (Gate)
        {
            States[root] = RootState.Failed;
            UnloadWhenLoaded.Remove(root);
        }

        Log($"'{root}' refused by CEF ({result}) — manual content-script fallback will serve it");
        NotifySettled();
    }

    private static void HostUnloaded(string root, CefExtension extension)
    {
        lock (Gate)
        {
            States.Remove(root);
            Handlers.Remove(root);
            ExtensionIds.Remove(root);
            UnloadWhenLoaded.Remove(root);
        }

        Log($"'{root}' unloaded");
        NotifySettled();
    }

    // ---- helpers -----------------------------------------------------------------

    private static void NotifySettled()
    {
        TaskCompletionSource<bool>[]? waiters = null;

        lock (Gate)
        {
            if (States.Values.Any(state => state == RootState.Loading) || SettledWaiters.Count == 0)
            {
                return;
            }

            waiters = SettledWaiters.ToArray();
            SettledWaiters.Clear();
        }

        foreach (var waiter in waiters)
        {
            waiter.TrySetResult(true);
        }
    }

    private static CefRequestContext? GetGlobalContext()
    {
        try
        {
            var context = CefRequestContext.GetGlobalContext();
            if (context is null)
            {
                Log("global request context is not available (engine not running yet)");
            }

            return context;
        }
        catch (Exception ex)
        {
            Log($"global request context unavailable: {ex.Message}");
            return null;
        }
    }

    private static void Log(string message)
    {
        var line = $"[CefExtensionHost] {message}";

        if (App.Log is not null)
        {
            App.Log(line);
        }
        else
        {
            Trace.WriteLine(line);
        }
    }
}



