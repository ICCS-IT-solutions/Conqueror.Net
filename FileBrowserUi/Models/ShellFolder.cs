namespace Conqueror.Net.FileBrowserUi.Models;

/// <summary>A task-pane entry: a special folder (Desktop, Documents) or a drive.</summary>
public sealed record ShellFolder(string Name, string Path, ShellFolderKind Kind)
{
    /// <summary>Key into the SVG icon theme; see <see cref="Icons.FileIconResolver"/>.</summary>
    public string IconKey => Icons.FileIconResolver.ForShellFolder(this);
}

public enum ShellFolderKind
{
    Folder,
    Drive,
    Network,
}
