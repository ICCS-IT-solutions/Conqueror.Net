using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Conqueror.Net.FileBrowserUi.Models;

namespace Conqueror.Net.FileBrowserUi.Services.Vfs;

/// <summary>
/// The KIO-style front door: one <see cref="IFileSystemService"/> that inspects the scheme of
/// each path and dispatches to the right back end. Local paths go to the ordinary
/// <see cref="FileSystemService"/>; <c>zip:</c> and <c>tar:</c> paths open the named archive as
/// a browsable folder; the network schemes are recognised but not yet backed, and report that.
/// </summary>
/// <remarks>
/// <para>
/// This is what gets injected wherever an <see cref="IFileSystemService"/> is expected, so the
/// rest of the app is unchanged: it keeps passing path strings, and the scheme is interpreted
/// here. A path with no scheme, or a <c>file:</c> prefix, or a Windows drive path is local.
/// </para>
/// <para>
/// Archive services are cached per archive path, because opening and flattening an archive on
/// every single call (each keystroke of filtering, every status-bar update) would be wasteful.
/// </para>
/// </remarks>
public sealed class CompositeFileSystemService : IFileSystemService
{
    private readonly IFileSystemService _local;
    private readonly Dictionary<string, ArchiveFileSystemService> _archives =
        new(StringComparer.OrdinalIgnoreCase);

    public CompositeFileSystemService()
        : this(new FileSystemService()) { }

    public CompositeFileSystemService(IFileSystemService local)
    {
        _local = local;
    }

    /// <summary>Recognised but not yet implemented schemes, with the message shown for each.</summary>
    private static readonly Dictionary<string, string> UnsupportedSchemes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ftp"] = "FTP browsing is not available yet.",
            ["sftp"] = "SFTP browsing is not available yet.",
            ["smb"] = "SMB/CIFS browsing is not available yet.",
            ["fish"] = "fish:// browsing is not available yet.",
            ["webdav"] = "WebDAV browsing is not available yet.",
        };

    /// <summary>
    /// Resolves the back end for <paramref name="path"/>. Returns null and sets
    /// <paramref name="unsupportedMessage"/> when the scheme is recognised but has no service.
    /// </summary>
    private IFileSystemService? Backend(string path, out string? unsupportedMessage)
    {
        var location = VfsLocation.Parse(path);

        if (location.IsLocal)
        {
            unsupportedMessage = null;
            return _local;
        }

        if (VfsLocation.IsSupportedScheme(location.Scheme))
        {
            unsupportedMessage = null;
            return ArchiveFor(location.Scheme, location.Path);
        }

        unsupportedMessage = UnsupportedSchemes.TryGetValue(location.Scheme, out var message)
            ? message
            : $"The '{location.Scheme}:' scheme is not supported.";
        return null;
    }

    private ArchiveFileSystemService ArchiveFor(string scheme, string archivePath)
    {
        var key = scheme + "|" + archivePath;
        if (!_archives.TryGetValue(key, out var service))
        {
            service = scheme.Equals("zip", StringComparison.OrdinalIgnoreCase)
                ? new ZipFileSystemService(archivePath)
                : new TarFileSystemService(archivePath);
            _archives[key] = service;
        }

        return service;
    }

    /// <summary>
    /// The path as the chosen back end wants to see it: unchanged for local, and the inner path
    /// (with the scheme and archive stripped) for an archive.
    /// </summary>
    private static string InnerPath(string path) => VfsLocation.Parse(path).Path;

    public DirectoryListing ListDirectory(string path)
    {
        var backend = Backend(path, out var unsupported);
        return backend is null
            ? new DirectoryListing([], unsupported)
            : backend.ListDirectory(InnerPath(path));
    }

    public bool IsDirectory(string path)
    {
        var backend = Backend(path, out _);
        return backend?.IsDirectory(InnerPath(path)) ?? false;
    }

    public bool Exists(string path)
    {
        var backend = Backend(path, out _);
        return backend?.Exists(InnerPath(path)) ?? false;
    }

    public string? GetParent(string path)
    {
        var backend = Backend(path, out _);
        return backend?.GetParent(InnerPath(path));
    }

    public string? ResolvePath(string input)
    {
        var location = VfsLocation.Parse(input);
        var backend = Backend(input, out _);
        if (backend is null)
        {
            return null;
        }

        // Typing just an archive's own path ("a.zip") means "open this archive", but the archive
        // back end only understands in-archive paths, so map a bare local archive path to the
        // archive root location.
        if (!location.IsLocal
            && VfsLocation.IsSupportedScheme(location.Scheme)
            && File.Exists(location.Path))
        {
            return location.ToString();
        }

        return backend.ResolvePath(InnerPath(input));
    }

    public IReadOnlyList<ShellFolder> GetShellFolders() => _local.GetShellFolders();

    public IReadOnlyList<string> ListChildDirectories(string path)
    {
        var backend = Backend(path, out _);
        return backend?.ListChildDirectories(InnerPath(path)) ?? [];
    }

    public void Search(
        string root,
        string term,
        IProgress<FileSystemEntry> progress,
        CancellationToken cancellationToken
    )
    {
        var backend = Backend(root, out var unsupported);
        if (backend is null)
        {
            progress.Report(new FileSystemEntry(
                unsupported ?? "Search is not available here.",
                unsupported ?? "Search is not available here.",
                false,
                0,
                default));
            return;
        }

        backend.Search(InnerPath(root), term, progress, cancellationToken);
    }

    public (bool Success, string? Error) CreateDirectory(string parent, string name)
    {
        var backend = Backend(parent, out var unsupported);
        return backend is null
            ? (false, unsupported)
            : backend.CreateDirectory(InnerPath(parent), name);
    }

    public (bool Success, string? Error) CreateFile(string parent, string name)
    {
        var backend = Backend(parent, out var unsupported);
        return backend is null
            ? (false, unsupported)
            : backend.CreateFile(InnerPath(parent), name);
    }

    public (bool Success, string? NewPath, string? Error) Rename(string path, string newName)
    {
        var backend = Backend(path, out var unsupported);
        return backend is null
            ? (false, null, unsupported)
            : backend.Rename(InnerPath(path), newName);
    }

    public (bool Success, string? Error) Delete(IEnumerable<string> paths)
    {
        var backend = Backend(paths.FirstOrDefault() ?? string.Empty, out var unsupported);
        return backend is null
            ? (false, unsupported)
            : backend.Delete(paths.Select(InnerPath));
    }

    public (bool Success, string? Error) Copy(IEnumerable<string> paths, string destinationDirectory)
    {
        var backend = Backend(destinationDirectory, out var unsupported);
        return backend is null
            ? (false, unsupported)
            : backend.Copy(paths.Select(InnerPath), InnerPath(destinationDirectory));
    }

    public (bool Success, string? Error) Move(IEnumerable<string> paths, string destinationDirectory)
    {
        var backend = Backend(destinationDirectory, out var unsupported);
        return backend is null
            ? (false, unsupported)
            : backend.Move(paths.Select(InnerPath), InnerPath(destinationDirectory));
    }

    public (long Total, long Free) GetDriveInfo(string pathOrDrive)
    {
        var backend = Backend(pathOrDrive, out _);
        return backend?.GetDriveInfo(InnerPath(pathOrDrive)) ?? (0, 0);
    }

    public string GetVolumeLabel(string pathOrDrive)
    {
        var backend = Backend(pathOrDrive, out _);
        return backend?.GetVolumeLabel(InnerPath(pathOrDrive)) ?? string.Empty;
    }

    public Stream? OpenFile(string path)
    {
        var backend = Backend(path, out _);
        return backend?.OpenFile(InnerPath(path));
    }

    public (bool Success, string? TempPath, string? Error) TryMaterialize(string path)
    {
        var backend = Backend(path, out var unsupported);
        return backend is null
            ? (false, null, unsupported)
            : backend.TryMaterialize(InnerPath(path));
    }
}

