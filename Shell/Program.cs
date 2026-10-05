using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace Conqueror.Net.Shell;

/// <summary>
/// Entry point for the XP-style shell.
/// </summary>
/// <remarks>
/// Deliberately its own process. The shell owns the bottom edge of the primary screen and the
/// Start menu, so it has to outlive the file manager and the browser: if either crashed, taking
/// the taskbar with it would be its own outage.
/// </remarks>
internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder
            .Configure<ShellApp>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
