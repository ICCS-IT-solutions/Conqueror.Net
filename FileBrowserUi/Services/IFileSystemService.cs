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

    /// <summary>Creates an empty file; returns false and an error instead of throwing.</summary>
    (bool Success, string? Error) CreateFile(string parent, string name);

    /// <summary>
    /// Renames a file or folder in place. Returns the new full path on success, or false
    /// and an error instead of throwing.
    /// </summary>
    (bool Success, string? NewPath, string? Error) Rename(string path, string newName);

    /// <summary>
    /// Sends files/folders to the recycle bin (Explorer semantics). Falls back to permanent
    /// deletion where no recycle bin exists; returns false and an error instead of throwing.
    /// </summary>
    (bool Success, string? Error) Delete(IEnumerable<string> paths);

    /// <summary>
    /// Copies files/folders into <paramref name="destinationDirectory"/>. Returns false and
    /// an error instead of throwing.
    /// </summary>
    (bool Success, string? Error) Copy(IEnumerable<string> paths, string destinationDirectory);

    /// <summary>
    /// Moves files/folders into <paramref name="destinationDirectory"/>. Returns false and
    /// an error instead of throwing.
    /// </summary>
    (bool Success, string? Error) Move(IEnumerable<string> paths, string destinationDirectory);

    /// <summary>Empty path (deleted items), so the status bar can report freed space.</summary>
    (long Total, long Free) GetDriveInfo(string pathOrDrive);

    /// <summary>
    /// Opens the bytes of a file at <paramref name="path"/> for reading, or null when the path
    /// is a directory, is missing, or belongs to a backend that cannot stream it. Used to open
    /// a leaf that lives inside an archive or on a remote without a real on-disk file.
    /// </summary>
    System.IO.Stream? OpenFile(string path);

    /// <summary>
    /// Materialises <paramref name="path"/> as a real file on disk and returns its temp path,
    /// for handing to code that can only cope with a genuine file (the OS "open" routing, an
    /// editor). Returns false for a directory or a backend that cannot materialise.
    /// </summary>
    /// <remarks>
    /// The caller owns the returned temp file and should delete it when done. Local paths are
    /// returned as-is (already real files); only virtual paths are copied to a temp location.
    /// </remarks>
    (bool Success, string? TempPath, string? Error) TryMaterialize(string path);

    /// <summary>
    /// Volume label for the drive or mount point containing <paramref name="pathOrDrive"/>, or
    /// an empty string when the volume has none. Used for the breadcrumb root, which Explorer
    /// shows as "Win10 (C:)" when a label exists and as a bare "C:\" when it does not.
    /// </summary>
    string GetVolumeLabel(string pathOrDrive);
}
