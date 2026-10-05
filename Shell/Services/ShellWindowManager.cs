using Avalonia;
using Avalonia.Controls;
using Conqueror.Net.Shell.ViewModels;
using Conqueror.Net.Shell.Views;

namespace Conqueror.Net.Shell.Services;

/// <summary>
/// Owns the taskbar and Start menu windows and keeps them positioned on the primary screen.
/// </summary>
/// <remarks>
/// Windows cannot be resized across the screen edge, so the taskbar has to be told its size
/// every time the display layout changes - resolution, scale, a monitor being unplugged. That is
/// what <see cref="Reposition"/> is for; it is idempotent and safe to call from
/// <c>PositionChanged</c>.
/// </remarks>
public sealed class ShellWindowManager
{
    /// <summary>XP's taskbar is 30 device-independent units tall.</summary>
    private const double TaskbarThickness = 30;

    /// <summary>Start menu width, as in XP's default 380 px menu.</summary>
    private const double StartMenuWidth = 380;

    private TaskbarWindow? _taskbar;
    private StartMenuWindow? _startMenu;

    public void Attach(TaskbarWindow taskbar)
    {
        _taskbar = taskbar;

        // Position immediately rather than waiting for the first Start-menu click: without
        // this the window keeps its XAML-declared 800x30 size at (78,78) and floats in the
        // middle of the desktop instead of sitting on the screen edge.
        taskbar.Opened += (_, _) => Reposition();

        Reposition();
    }

    public bool IsStartMenuOpen => _startMenu?.IsVisible == true;

    /// <summary>Opens the Start menu, or closes it if already showing, as XP's button did.</summary>
    public void ToggleStartMenu()
    {
        if (_startMenu is null)
        {
            if (_taskbar is null)
            {
                return;
            }

            _startMenu = new StartMenuWindow
            {
                DataContext = new StartMenuViewModel(CloseStartMenu, LaunchFileManager),
            };

            Reposition();
        }

        if (_startMenu!.IsVisible)
        {
            CloseStartMenu();
        }
        else
        {
            _startMenu.Show();
            _startMenu.Activate();
        }
    }

    public void CloseStartMenu()
    {
        if (_startMenu is not null)
        {
            _startMenu.IsVisible = false;
        }
    }

    /// <summary>Launches the file manager against a folder, or the home folder when null.</summary>
    private void LaunchFileManager(string? path)
    {
        CloseStartMenu();

        var target = string.IsNullOrWhiteSpace(path) ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) : path;

        try
        {
            // Prefer an already-running instance so the shell hands over to it rather than
            // starting a second copy with a second CEF instance.
            var executable = ConqPath();

            if (executable is not null)
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(executable, $"\"{target}\"")
                    {
                        UseShellExecute = false,
                        WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
                    }
                );
            }
        }
        catch (Exception ex)
            when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            ShellApp.Log?.Invoke($"could not launch file manager: {ex.Message}");
        }
    }

    /// <summary>
    /// Locates Conqueror.Net.exe next to, or one level above, this shell executable.
    /// </summary>
    /// <remarks>
    /// Both projects publish into their own bin folder, so during development the file manager
    /// is a sibling rather than a neighbour. Returns null rather than guessing when not found.
    /// </remarks>
    private static string? ConqPath()
    {
        var here = AppContext.BaseDirectory;

        var candidates = new[]
        {
            Path.Combine(here, "Conqueror.Net.exe"),
            Path.GetFullPath(Path.Combine(here, "..", "..", "..", "..", "Conqueror.Net", "bin", "Debug", "net8.0", "Conqueror.Net.exe")),
            Path.GetFullPath(Path.Combine(here, "..", "..", "..", "..", "Conqueror.Net", "bin", "Release", "net8.0", "win-x64", "publish", "Conqueror.Net.exe")),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Pins the taskbar to the bottom of the primary screen, and the menu above it.</summary>
    public void Reposition()
    {
        var screen = _taskbar?.Screens.Primary;

        if (screen is null || _taskbar is null)
        {
            return;
        }

        var bounds = screen.Bounds;
        var scale = screen.Scaling;

        // Screen bounds are in physical pixels; Avalonia's Position is too, but the taskbar's
        // own height is set in DIPs, so convert once here rather than in each window.
        var height = (int)(TaskbarThickness * scale);

        _taskbar.WindowStartupLocation = WindowStartupLocation.Manual;
        _taskbar.Position = new PixelPoint(bounds.X, bounds.Y + bounds.Height - height);
        _taskbar.Width = bounds.Width / scale;
        _taskbar.Height = TaskbarThickness;

        if (_startMenu is not null)
        {
            var menuHeight = 520 * scale;

            _startMenu.WindowStartupLocation = WindowStartupLocation.Manual;
            _startMenu.Position = new PixelPoint(
                bounds.X,
                bounds.Y + bounds.Height - height - (int)menuHeight
            );
            _startMenu.Width = StartMenuWidth;
            _startMenu.Height = 520;
        }
    }
}
