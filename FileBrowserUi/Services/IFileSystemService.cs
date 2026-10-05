using Conqueror.Net.FileBrowserUi.Models;

namespace Conqueror.Net.FileBrowserUi.Services;

/// <summary>Result of enumerating a folder, carrying a non-fatal error for the status bar.</summary>
public sealed record DirectoryListing(IReadOnlyList<FileSystemEntry> Entries, string? Error)
{
    public bool HasError => Error is not null;
}

/// <summary>Reads the file system on behalf of the file-browser view-models.</summary>
public interface IFileSystemService
{
    DirectoryListing ListDirectory(string path);

    /// <summary>True when the path exists and is a directory we can list.</summary>
    bool IsDirectory(string path);

    bool Exists(string path);

    /// <summary>The parent directory, or null at a drive/UNC root.</summary>
    string? GetParent(string path);

    /// <summary>
    /// Resolves user input (an address-bar entry, a shell link, a bare folder name) to a full
    /// path. Returns null when the input names nothing that exists.
    /// </summary>
    string? ResolvePath(string input);

    /// <summary>Special folders and drives shown in the Explorer task pane.</summary>
    IReadOnlyList<ShellFolder> GetShellFolders();

    /// <summary>
    /// Immediate child directories of <paramref name="path"/>, used to expand a folder-tree
    /// node on demand. Returns an empty list rather than throwing on an unreadable folder,
    /// because a denied junction is normal and should leave the node simply childless.
    /// </summary>
    IReadOnlyList<string> ListChildDirectories(string path);

    /// <summary>
    /// Recursively searches <paramref name="root"/> for files and folders whose name contains
    /// <paramref name="term"/>, reporting matches as they are found.
    /// </summary>
    /// <remarks>
    /// This walks the tree, so it is genuinely slow and must not run on the UI thread; the
    /// implementation takes a <see cref="CancellationToken"/> for that reason. An empty or
    /// whitespace <paramref name="term"/> matches nothing, and an unreadable subdirectory is
    /// skipped rather than aborting the whole walk.
    /// </remarks>
    void Search(
        string root,
        string term,
        IProgress<FileSystemEntry> progress,
        CancellationToken cancellationToken
    );

    /// <summary>Creates a directory; returns false and an error instead of throwing.</summary>
    (bool Success, string? Error) CreateDirectory(string parent, string name);

    /// <summary>Empty path (deleted items), so the status bar can report freed space.</summary>
    (long Total, long Free) GetDriveInfo(string pathOrDrive);

    /// <summary>
    /// Volume label for the drive or mount point containing <paramref name="pathOrDrive"/>, or
    /// an empty string when the volume has none. Used for the breadcrumb root, which Explorer
    /// shows as "Win10 (C:)" when a label exists and as a bare "C:\" when it does not.
    /// </summary>
    string GetVolumeLabel(string pathOrDrive);
}
