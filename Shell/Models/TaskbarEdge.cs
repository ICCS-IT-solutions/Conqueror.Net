namespace Conqueror.Net.Shell.Models;

/// <summary>
/// Which screen edge the taskbar is anchored to.
/// </summary>
/// <remarks>
/// XP allowed the taskbar to be dragged to any of the four edges and remembered the choice, so
/// the default here is the traditional bottom rather than a fixed position. The values are
/// ordered so that <see cref="Bottom"/> is the zero value, which makes it the default for a
/// settings file whose field is missing or unrecognised - a corrupt or older config therefore
/// degrades to XP's familiar position rather than failing.
/// </remarks>
public enum TaskbarEdge
{
    Bottom = 0,
    Top = 1,
    Left = 2,
    Right = 3,

    /// <summary>Follow whatever edge the Windows taskbar itself is docked to.</summary>
    MatchWindows = 4,
}

public static class TaskbarEdgeExtensions
{
    /// <summary>Whether the edge runs horizontally, as a bar along the top or bottom.</summary>
    public static bool IsHorizontal(this TaskbarEdge edge) =>
        edge is TaskbarEdge.Bottom or TaskbarEdge.Top or TaskbarEdge.MatchWindows;

    /// <summary>Resolves <see cref="TaskbarEdge.MatchWindows"/> against the live taskbar.</summary>
    /// <remarks>
    /// The Windows taskbar is inferred from the difference between the screen's full bounds
    /// and its working area, which is what the shell reports while that taskbar is docked.
    /// Comparing the two rectangles is more robust than asking the shell directly: it also
    /// accounts for a third-party bar and for auto-hide, which shrinks the working area only
    /// while the bar is showing.
    /// </remarks>
    public static TaskbarEdge Resolve(this TaskbarEdge edge, Avalonia.Platform.Screen screen)
    {
        if (edge is not TaskbarEdge.MatchWindows || screen is null)
        {
            return edge;
        }

        var full = screen.Bounds;
        var work = screen.WorkingArea;

        if (work.Y > full.Y)
        {
            return TaskbarEdge.Top;
        }

        if (work.X > full.X)
        {
            return TaskbarEdge.Left;
        }

        if (work.Right < full.Right)
        {
            return TaskbarEdge.Right;
        }

        return TaskbarEdge.Bottom;
    }
}