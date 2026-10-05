using Avalonia;
using Avalonia.Controls;
using Conqueror.Net.Shell.Models;
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
    /// <summary>XP's taskbar is 30 device-independent units thick.</summary>
    private const double TaskbarThickness = 30;

    /// <summary>Start menu width, as in XP's default 380 px menu.</summary>
    private const double StartMenuWidth = 380;

    /// <summary>Start menu height.</summary>
    private const double StartMenuHeight = 520;

    private readonly ShellSettings _settings;
    private TaskbarWindow? _taskbar;
    private StartMenuWindow? _startMenu;

    public ShellWindowManager(ShellSettings settings) => _settings = settings;

    /// <summary>
    /// Raised after the bar has been repositioned, with the edge actually in effect.
    /// </summary>
    /// <remarks>
    /// Carries the resolved edge rather than the configured one, so the view reflects reality
    /// when the setting is <see cref="TaskbarEdge.MatchWindows"/>.
    /// </remarks>
    public event Action<TaskbarEdge>? EdgeChanged;

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

    /// <summary>
    /// Pins the taskbar to its configured edge of the primary screen, and the Start menu
    /// alongside it.
    /// </summary>
    /// <remarks>
    /// <see cref="TaskbarEdge.MatchWindows"/> is resolved here against the live working area, so
    /// the bar tracks the Windows taskbar if it is moved while the shell is running.
    /// </remarks>
    public void Reposition()
    {
        var screen = _taskbar?.Screens.Primary;

        if (screen is null || _taskbar is null)
        {
            return;
        }

        var bounds = screen.Bounds;
        var scale = screen.Scaling;
        var edge = _settings.Edge.Resolve(screen);

        // Screen bounds are in physical pixels; Avalonia's Position is too, but the taskbar's
        // own thickness is in DIPs, so convert once here rather than in each window.
        var thickness = (int)(TaskbarThickness * scale);

        var horizontal = edge.IsHorizontal();

        var width = horizontal ? bounds.Width : thickness;
        var height = horizontal ? thickness : bounds.Height;

        // No frame compensation. It was tried and reverted: Avalonia's Position already points at
        // the content origin, so subtracting Window.FrameSize pulls the bar *away* from the
        // edge and leaves a 30 px gap. Measured on a bottom-docked bar, compensation moved the
        // content edge from 1080 to 1050.
        var x = edge switch
        {
            TaskbarEdge.Left => bounds.X,
            TaskbarEdge.Right => bounds.Right - thickness,
            _ => bounds.X,
        };

        var y = edge switch
        {
            TaskbarEdge.Top => bounds.Y,
            TaskbarEdge.Bottom => bounds.Bottom - thickness,
            _ => bounds.Y,
        };

        _taskbar.WindowStartupLocation = WindowStartupLocation.Manual;
        _taskbar.Position = new PixelPoint(x, y);
        _taskbar.Width = width / scale;
        _taskbar.Height = height / scale;

        // The bar lays out horizontally or vertically depending on the edge, so the view needs
        // to be told; a vertical bar with a horizontal DockPanel would look broken.
        EdgeChanged?.Invoke(edge);

        if (_startMenu is not null)
        {
            PositionStartMenu(edge, bounds, thickness, scale);
        }
    }

    /// <summary>
    /// Places the Start menu against the taskbar's inner edge, the way XP did.
    /// </summary>
    /// <remarks>
    /// With a bottom taskbar the menu opens up and to the right, which is XP's default. With a
    /// left taskbar it opens rightwards, with a right taskbar leftwards, and with a top taskbar
    /// downwards - always into the screen rather than off it.
    /// </remarks>
    private void PositionStartMenu(TaskbarEdge edge, PixelRect bounds, int thickness, double scale)
    {
        // Re-checked rather than assumed: Reposition guards this, but the field is mutable and
        // the menu is created lazily, so a narrow guard here is cheaper than a null reference.
        if (_startMenu is null)
        {
            return;
        }

        var menuWidth = (int)(StartMenuWidth * scale);
        var menuHeight = (int)(StartMenuHeight * scale);

        var (x, y) = edge switch
        {
            TaskbarEdge.Left => (bounds.X + thickness, bounds.Y),
            TaskbarEdge.Right => (bounds.Right - thickness - menuWidth, bounds.Y),
            TaskbarEdge.Top => (bounds.X, bounds.Y + thickness),
            _ => (bounds.X, bounds.Bottom - thickness - menuHeight),
        };

        _startMenu.WindowStartupLocation = WindowStartupLocation.Manual;
        _startMenu.Position = new PixelPoint(x, y);
        _startMenu.Width = StartMenuWidth;
        _startMenu.Height = StartMenuHeight;
    }
}
