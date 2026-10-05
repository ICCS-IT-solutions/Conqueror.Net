namespace Conqueror.Net.FileBrowserUi.Models;

/// <summary>A task-pane entry: a special folder (Desktop, Documents) or a drive.</summary>
public sealed record ShellFolder(string Name, string Path, ShellFolderKind Kind);

public enum ShellFolderKind
{
    Folder,
    Drive,
    Network,
}
