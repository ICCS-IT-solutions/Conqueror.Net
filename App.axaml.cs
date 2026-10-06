using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Conqueror.Net.FileBrowserUi.Services;
using Conqueror.Net.WebBrowserUi.Services;
using Conqueror.Net.WindowRoot;
using Conqueror.Net.WindowRoot.ViewModels;

namespace Conqueror.Net;

public partial class App : Application
{
    /// <summary>Diagnostic sink; wired to Trace by the program entry point.</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>
    /// Shared file-system access. One instance serves every tab.</summary>
    public static IFileSystemService FileSystem { get; } = new FileSystemService();

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
        if (!OperatingSystem.IsWindows())
        {
            Log?.Invoke(
                $"CEF is not configured on {Environment.OSVersion.Platform}: the browser tab "
                    + "is unavailable, the other tab kinds are unaffected."
            );
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

            // Evidence channel: CEF's own log (extension loads, content-script injection,
            // errors). Truncate first so each run's log covers only that run; setting
            // LogFile also flips severity to Verbose (EnableErrorLogOnly defaults false).
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

            // System.Drawing colour: the control predates Avalonia's colour type.
            WebViewControl.WebView.Settings.BackgroundColor = System.Drawing.Color.White;

            // Enable experimental web-platform features before the engine spins up.
            WebViewControl.WebView.Settings.AddCommandLineSwitch("enable-experimental-web-platform-features", null);

            // Pre-initialise CEF with NoSandbox=true so that the --no-sandbox flag is propagated
            // to ALL subprocesses (browser, renderer, GPU), not just the browser process.
            // This must happen before any WebView is constructed — the first WebView's
            // constructor calls CefRuntimeLoader.Load() which would otherwise run the
            // default initialiser that leaves NoSandbox=false on Windows.
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
