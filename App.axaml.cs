using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Conqueror.Net.FileBrowserUi.Services;
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
    /// Applies CEF's process-wide settings. This must run before the first <c>WebView</c>
    /// control is constructed: the control spins the Chromium engine up during its own
    /// constructor, and every setting then throws "after WebView engine has been loaded".
    /// </summary>
    public static void ConfigureChromium()
    {
        var cachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Conqueror.Net",
            "cef-cache"
        );

        Directory.CreateDirectory(cachePath);

        WebViewControl.WebView.Settings.CachePath = cachePath;

        // System.Drawing colour: the control predates Avalonia's colour type.
        WebViewControl.WebView.Settings.BackgroundColor = System.Drawing.Color.White;
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

    public override void Initialize()
    {
        // Before AvaloniaXamlLoader runs, so no DataTemplate can build a WebView first.
        ConfigureChromium();

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
