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

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // OnLastWindowClose would tear the shell down the moment the Start menu closed, so
            // the lifetime is driven explicitly and the taskbar is the thing that stays up.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var windowManager = new ShellWindowManager();

            var taskbar = new TaskbarWindow { DataContext = new TaskbarViewModel(windowManager) };

            windowManager.Attach(taskbar);

            desktop.MainWindow = taskbar;
            taskbar.Show();

            desktop.Startup += (_, _) => Log?.Invoke("shell started");
        }

        base.OnFrameworkInitializationCompleted();
    }
}
