using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using System.Diagnostics;
using Avalonia.Markup.Xaml;
using Conqueror.Net.Shell.Services;
using Conqueror.Net.Shell.ViewModels;
using Conqueror.Net.Shell.Views;

namespace Conqueror.Net.Shell;

/// <summary>
/// Shell application. Hosts the taskbar window and the Start menu.
/// </summary>
public class ShellApp : Application
{
    /// <summary>
    /// Diagnostic sink.
    /// </summary>
    /// <remarks>
    /// Writes to stdout rather than only to Trace. The shell is launched with WinExe, so
    /// stdout has nowhere to go unless redirected - which is exactly what the smoke tests do.
    /// </remarks>
    public static Action<string>? Log { get; } = message =>
    {
        Console.WriteLine($"[shell] {message}");
        Trace.WriteLine(message);
    };

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Reads <c>--edge &lt;bottom|top|left|right|windows&gt;</c> from the command line.
    /// </summary>
    /// <remarks>
    /// An override for one run, not saved: the persisted setting wins on the next launch. This
    /// exists so each position can be launched and screenshotted on demand, which is how the
    /// vertical layouts are verified without a GUI to click through.
    /// </remarks>
    private static Models.TaskbarEdge? GetEdgeOverride(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is "--edge" or "/edge")
            {
                return args[i + 1].ToLowerInvariant() switch
                {
                    "bottom" => Models.TaskbarEdge.Bottom,
                    "top" => Models.TaskbarEdge.Top,
                    "left" => Models.TaskbarEdge.Left,
                    "right" => Models.TaskbarEdge.Right,
                    "windows" or "match" => Models.TaskbarEdge.MatchWindows,
                    _ => null,
                };
            }
        }

        return null;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // OnLastWindowClose would tear the shell down the moment the Start menu closed, so
            // the lifetime is driven explicitly and the taskbar is the thing that stays up.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var settings = Services.ShellSettings.Load();

            // A --edge switch makes each position testable without a context menu, which also
            // gives the smoke tests something deterministic to assert on.
            var requested = GetEdgeOverride(desktop.Args ?? []);

            if (requested is not null)
            {
                settings.Edge = requested.Value;
            }

            var windowManager = new Services.ShellWindowManager(settings);

            var taskbar = new TaskbarWindow
            {
                DataContext = new TaskbarViewModel(windowManager, settings),
            };

            windowManager.Attach(taskbar);

            desktop.MainWindow = taskbar;
            taskbar.Show();

            Log?.Invoke($"started on {settings.Edge} edge");
        }

        base.OnFrameworkInitializationCompleted();
    }
}
