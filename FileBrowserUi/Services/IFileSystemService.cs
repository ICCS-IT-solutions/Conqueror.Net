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

    /// <summary>Creates a directory; returns false and an error instead of throwing.</summary>
    (bool Success, string? Error) CreateDirectory(string parent, string name);

    /// <summary>Empty path (deleted items), so the status bar can report freed space.</summary>
    (long Total, long Free) GetDriveInfo(string pathOrDrive);
}
