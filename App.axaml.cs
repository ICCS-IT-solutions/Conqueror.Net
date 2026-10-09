using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Conqueror.Net.FileBrowserUi.Services;
using Conqueror.Net.FileBrowserUi.Services.Vfs;
using Conqueror.Net.WebBrowserUi.Services;
using Conqueror.Net.WindowRoot;
using Conqueror.Net.WindowRoot.ViewModels;

namespace Conqueror.Net;

public partial class App : Application
{
    /// <summary>Diagnostic sink; wired to Trace by the program entry point.</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>
    /// Shared file-system access. One instance serves every tab. Routes local paths to the
    /// real file system and zip:/tar: paths into archives as browsable folders.
    /// </summary>
    public static IFileSystemService FileSystem { get; } = new CompositeFileSystemService();

    /// <summary>
    /// Shared extension registry. One instance serves every browser tab so enable/disable
    /// state stays consistent in memory as well as on disk.
    /// </summary>
    public static WebBrowserUi.Services.IExtensionService Extensions { get; } =
        new WebBrowserUi.Services.FileBasedExtensionService();

    /// <summary>
    /// Applies CEF's process-wide settings. This must run before the first <c>WebView</c>
    /// control is constructed: the control spins the Chromium engine up during its own
    /// constructor, and every setting then throws "after WebView engine has been loaded".
    /// </summary>
    /// <remarks>
    /// Guarded because CEF is the one genuinely Windows-bound dependency in the app. Skipping
    /// it off-Windows is what lets the file manager, dual-pane and terminal tabs run on Linux
    /// and macOS; only the browser tab is unavailable there.
    /// </remarks>
    public static bool TryConfigureChromium()
    {
        // CEF is supported on Windows and Linux. On macOS it's not available via WebViewControl-Avalonia.
        if (OperatingSystem.IsMacOS())
        {
            Log?.Invoke($"CEF is not available on macOS: the browser tab is unavailable, other tabs are unaffected.");
            return false;
        }

        try
        {
            var cachePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Conqueror.Net",
                "cef-cache"
            );

            Directory.CreateDirectory(cachePath);

            WebViewControl.WebView.Settings.CachePath = cachePath;

            var cefLogPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Conqueror.Net",
                "cef.log"
            );
            try
            {
                File.Delete(cefLogPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Locked by a previous run — that log keeps growing instead.
            }

            WebViewControl.WebView.Settings.LogFile = cefLogPath;

            // Keep CEF's own log to errors only. CefExtensionHost.PreInitialize reads this
            // flag to choose the log severity: with a LogFile set but EnableErrorLogOnly
            // false it picks CefLogSeverity.Verbose, which makes Chromium enable
            // --enable-logging --v=1 and flood the console with VERBOSE1 lines — most
            // visibly the D-Bus probes in dbus/bus.cc ("org.freedesktop.login1
            // GetNameOwner"), a code path that only exists on Linux. Setting it true selects
            // CefLogSeverity.Error so those VLOG(1) traces are never emitted.
            WebViewControl.WebView.Settings.EnableErrorLogOnly = true;

            WebViewControl.WebView.Settings.BackgroundColor = System.Drawing.Color.White;

            // Enable experimental web-platform features before the engine spins up.
            WebViewControl.WebView.Settings.AddCommandLineSwitch("enable-experimental-web-platform-features", null);

            // On Linux with software rendering (X11/Wayland), CEF needs --no-sandbox
            // and GPU-related flags disabled. These must be set before first WebView creation.
            if (OperatingSystem.IsLinux())
            {
                WebViewControl.WebView.Settings.AddCommandLineSwitch("no-sandbox", null);
                WebViewControl.WebView.Settings.AddCommandLineSwitch("disable-gpu", null);
                WebViewControl.WebView.Settings.AddCommandLineSwitch("disable-gpu-compositing", null);
                WebViewControl.WebView.Settings.AddCommandLineSwitch("disable-software-rasterizer", null);
            }

            // Pre-initialise CEF with NoSandbox=true so that the --no-sandbox flag is propagated
            // to ALL subprocesses (browser, renderer, GPU), not just the browser process.
            CefExtensionHost.PreInitialize();

            return true;
        }
        catch (Exception ex)
        {
            // A failed engine init must not take the whole window down with it; the browser
            // tab degrades to an error page and everything else keeps working.
            Log?.Invoke($"Could not configure CEF: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Reads a startup target out of the command line. A URL opens a browser tab, a path
    /// opens a folder tab, matching Explorer's behaviour for a "target" argument.
    /// </summary>
    public static string? GetStartupTarget(string[] args)
    {
        if (args.Length == 0)
        {
            return null;
        }

        // A single bare argument that is not a switch.
        var candidate = args[0].Trim('"');

        return string.IsNullOrWhiteSpace(candidate) || candidate.StartsWith('-') ? null : candidate;
    }

    public static bool ChromiumAvailable { get; private set; }

    public override void Initialize()
    {
        
        //Crash here needs to be fixed or I need a different web browser engine.
        //When chromium available returns false, the init method stops and nothing loads.
 
        // Before AvaloniaXamlLoader runs, so no DataTemplate can build a WebView first.
        ChromiumAvailable = TryConfigureChromium();

        // Extension toggles install/uninstall must re-sync the native CEF host. Before the
        // first WebView exists this is a no-op; that WebView's Attach performs the sync.
        Extensions.ExtensionsChanged += () => WebBrowserUi.Services.CefExtensionHost.Sync(Extensions);
        
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainWindowViewModel(FileSystem);

            var target = GetStartupTarget(desktop.Args ?? []);

            if (target is not null)
            {
                viewModel.OpenStartupTarget(target);
            }

            var mainWindow = new MainWindow { DataContext = viewModel };

            desktop.MainWindow = mainWindow;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
