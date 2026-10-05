namespace Conqueror.Net.FileBrowserUi.Models;

/// <summary>A task-pane entry: a special folder (Desktop, Documents) or a drive.</summary>
public sealed record ShellFolder(string Name, string Path, ShellFolderKind Kind)
{
    /// <summary>
    /// Key into the SVG icon theme; see <see cref="Icons.FileIconResolver"/>.
    /// </summary>
    /// <remarks>
    /// Falls back to the plain folder icon when the themed artwork was not imported, because a
    /// missing asset renders as an empty gap rather than an error - see
    /// <c>docs/ICON-THEME.md</c>, where <c>network</c> is deliberately excluded.
    /// </remarks>
    public string IconKey
    {
        get
        {
            var preferred = Kind switch
            {
                ShellFolderKind.Drive => Icons.FileIconResolver.DriveHardDisk,
                ShellFolderKind.Network => Icons.FileIconResolver.Network,
                _ => Icons.FileIconResolver.ForFolder(Path),
            };

            return Icons.FileIconResolver.IsBundled(preferred)
                ? preferred
                : Icons.FileIconResolver.Folder;
        }
    }
}

public enum ShellFolderKind
{
    Folder,
    Drive,
    Network,
}
