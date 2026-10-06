using Avalonia;

namespace Conqueror.Net;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Diagnostic sink: App.Log consumers (CefExtensionHost, CEF wiring notes) land in
        // the debugger's output window and in ui.log next to cef.log, so extension-host
        // decisions are inspectable after any run. Lambda because Trace.WriteLine is
        // [Conditional("TRACE")] and cannot be converted to a delegate directly.
        var uiLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Conqueror.Net",
            "ui.log"
        );
        try
        {
            File.WriteAllText(uiLogPath, string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Previous run's log stays; appends simply mix runs.
        }

        App.Log = message =>
        {
            System.Diagnostics.Trace.WriteLine(message);
            try
            {
                File.AppendAllText(
                    uiLogPath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Never let diagnostics take the app down.
            }
        };

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
