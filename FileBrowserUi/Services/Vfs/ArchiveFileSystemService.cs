using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Conqueror.Net.FileBrowserUi.Models;

namespace Conqueror.Net.FileBrowserUi.Services.Vfs;

/// <summary>
/// An <see cref="IFileSystemService"/> over a single archive (zip or tar). The archive is
/// presented as a read-only folder tree: <c>zip:/home/me/a.zip/docs</c> lists the entries under
/// "docs/" inside "a.zip" exactly as a real folder would list its children.
/// </summary>
/// <remarks>
/// <para>
/// The whole archive is read once per listing into a flat list of <see cref="ArchiveEntry"/>,
/// and the tree is derived from that by string-prefix matching on the normalised path. That is
/// cheap for the sizes an interactive file manager opens and keeps the zip and tar back ends
/// identical: the only thing a subclass supplies is <see cref="ReadEntries"/>.
/// </para>
/// <para>
/// Paths inside an archive always use '/' as the separator regardless of host OS, matching the
/// zip/tar on-disk convention. The composite service hands these methods the path portion of a
/// <see cref="VfsLocation"/>, which is already the in-archive path.
/// </para>
/// <para>
/// The service is read-only: mutating verbs return a clear "read-only" message rather than
/// pretending to write into a compressed stream, which the base formats cannot do in place.
/// </para>
/// </remarks>
internal abstract class ArchiveFileSystemService : IFileSystemService
{
    /// <summary>Absolute path of the archive file on the real file system.</summary>
    protected readonly string ArchivePath;

    /// <summary>Display name shown for the archive root, e.g. "holidays.zip".</summary>
    private readonly string _displayName;

    protected ArchiveFileSystemService(string archivePath)
    {
        ArchivePath = archivePath;
        _displayName = Path.GetFileName(archivePath.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(_displayName))
        {
            _displayName = archivePath;
        }
    }

    /// <summary>Reads every node in the archive as a flat, normalised list.</summary>
    protected abstract IReadOnlyList<ArchiveEntry> ReadEntries();

    /// <summary>Opens a single entry's bytes for extraction/open.</summary>
    protected abstract Stream? OpenEntry(string innerPath);

    /// <summary>
    /// The scheme this service answers for ("zip" or "tar"), used to build the canonical
    /// location string for each entry.
    /// </summary>
    protected abstract string Scheme { get; }

    // ---- path helpers ------------------------------------------------------

    /// <summary>Normalises an in-archive path to forward slashes, no leading/trailing slash.</summary>
    private static string Normalize(string inner)
    {
        if (string.IsNullOrWhiteSpace(inner))
        {
            return string.Empty;
        }

        var text = inner.Replace('\\', '/').Trim();
        while (text.StartsWith("./", StringComparison.Ordinal))
        {
            text = text[2..];
        }

        return text.Trim('/');
    }

    private static string LeafName(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    /// <summary>The canonical VFS path for an in-archive path, e.g. "zip:/home/me/a.zip/docs".</summary>
    private string LocationFor(string inner)
    {
        var normalized = Normalize(inner);
        return normalized.Length == 0
            ? string.Concat(Scheme, ":", ArchivePath)
            : string.Concat(Scheme, ":", ArchivePath, "/", normalized);
    }

    /// <summary>
    /// Immediate children under <paramref name="inner"/>: explicit file entries plus folders
    /// implied by any deeper path, so an archive that stores only files still shows its folders.
    /// </summary>
    private List<ArchiveEntry> Children(string inner)
    {
        var prefix = Normalize(inner);
        var prefixWithSlash = prefix.Length == 0 ? string.Empty : prefix + "/";

        var directories = new Dictionary<string, ArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, ArchiveEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in ReadEntries())
        {
            var path = Normalize(entry.Path);
            if (path.Length == 0
                || !path.StartsWith(prefixWithSlash, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var remainder = path[prefixWithSlash.Length..];
            var slash = remainder.IndexOf('/');

            if (slash < 0)
            {
                files[remainder] = entry;
            }
            else
            {
                var dirName = remainder[..slash];
                if (!directories.ContainsKey(dirName))
                {
                    directories[dirName] = new ArchiveEntry(
                        prefixWithSlash + dirName, true, 0, entry.Modified);
                }
            }
        }

        var result = new List<ArchiveEntry>(directories.Count + files.Count);
        result.AddRange(directories.Values);
        result.AddRange(files.Values);
        return result;
    }

    // ---- IFileSystemService (read side) ------------------------------------

    public DirectoryListing ListDirectory(string path)
    {
        var inner = Normalize(path);
        try
        {
            if (!File.Exists(ArchivePath))
            {
                return new DirectoryListing([], $"Cannot find '{ArchivePath}'.");
            }

            if (inner.Length != 0 && !IsDirectory(inner))
            {
                return new DirectoryListing([], $"Cannot find '{LocationFor(inner)}'.");
            }

            var prefix = inner.Length == 0 ? string.Empty : inner + "/";
            var entries = Children(inner)
                .Select(e =>
                {
                    var name = LeafName(e.Path);
                    return new FileSystemEntry(
                        fullPath: LocationFor(prefix + name),
                        name: name,
                        isDirectory: e.IsDirectory,
                        length: e.Length,
                        modified: e.Modified);
                })
                .ToList();

            return new DirectoryListing(entries, null);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            return new DirectoryListing([], $"Could not read '{_displayName}': {ex.Message}");
        }
    }

    public bool IsDirectory(string path)
    {
        var inner = Normalize(path);
        if (inner.Length == 0)
        {
            return File.Exists(ArchivePath);
        }

        // A directory is any prefix of some entry, or an explicit directory entry.
        foreach (var entry in ReadEntries())
        {
            var normalized = Normalize(entry.Path);
            if (normalized.Equals(inner, StringComparison.OrdinalIgnoreCase) && entry.IsDirectory)
            {
                return true;
            }

            if (normalized.StartsWith(inner + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public bool Exists(string path)
    {
        var inner = Normalize(path);
        if (inner.Length == 0)
        {
            return File.Exists(ArchivePath);
        }

        return IsDirectory(inner)
            || ReadEntries().Any(e =>
                Normalize(e.Path).Equals(inner, StringComparison.OrdinalIgnoreCase));
    }

    public string? GetParent(string path)
    {
        var inner = Normalize(path);
        if (inner.Length == 0)
        {
            // Up from the archive root returns to the folder that holds the archive, so the
            // breadcrumb and Up button walk out of the archive and back onto the real disk.
            return Path.GetDirectoryName(
                ArchivePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        var slash = inner.LastIndexOf('/');
        return slash < 0 ? LocationFor(string.Empty) : LocationFor(inner[..slash]);
    }

    public string? ResolvePath(string input) =>
        // The composite already parsed the scheme; here we only confirm the inner path exists.
        Exists(input) ? LocationFor(Normalize(input)) : null;

    public IReadOnlyList<ShellFolder> GetShellFolders() => [];

    public IReadOnlyList<string> ListChildDirectories(string path) =>
        Children(Normalize(path))
            .Where(e => e.IsDirectory)
            .Select(e => LocationFor(e.Path))
            .ToList();

    public void Search(
        string root,
        string term,
        IProgress<FileSystemEntry> progress,
        System.Threading.CancellationToken cancellationToken
    )
    {
        var inner = Normalize(root);
        foreach (var entry in ReadEntries())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var normalized = Normalize(entry.Path);
            if (!normalized.StartsWith(inner, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = LeafName(normalized);
            if (name.Contains(term, StringComparison.CurrentCultureIgnoreCase))
            {
                progress.Report(new FileSystemEntry(
                    LocationFor(normalized), name, entry.IsDirectory, entry.Length, entry.Modified));
            }
        }
    }

    // ---- IFileSystemService (write side: read-only) ------------------------

    private const string ReadOnlyMessage =
        "This is an archive. Open an item to extract it; the archive itself is read-only.";

    public (bool Success, string? Error) CreateDirectory(string parent, string name) =>
        (false, ReadOnlyMessage);

    public (bool Success, string? Error) CreateFile(string parent, string name) =>
        (false, ReadOnlyMessage);

    public (bool Success, string? NewPath, string? Error) Rename(string path, string newName) =>
        (false, null, ReadOnlyMessage);

    public (bool Success, string? Error) Delete(IEnumerable<string> paths) => (false, ReadOnlyMessage);

    public (bool Success, string? Error) Copy(IEnumerable<string> paths, string destinationDirectory) =>
        (false, ReadOnlyMessage);

    public (bool Success, string? Error) Move(IEnumerable<string> paths, string destinationDirectory) =>
        (false, ReadOnlyMessage);

    public (long Total, long Free) GetDriveInfo(string pathOrDrive) => (0, 0);

    public string GetVolumeLabel(string pathOrDrive) => _displayName;

    // ---- extraction / open -------------------------------------------------

    /// <summary>
    /// Opens an entry's bytes, or null when the entry is a directory or missing. Used by the
    /// composite to extract a leaf so it can be opened by the OS routing.
    /// </summary>
    public Stream? Open(string innerPath) => OpenEntry(Normalize(innerPath));

    public Stream? OpenFile(string path) => Open(path);

    public (bool Success, string? TempPath, string? Error) TryMaterialize(string path)
    {
        var inner = Normalize(path);
        if (IsDirectory(inner))
        {
            return (false, null, "'" + LocationFor(inner) + "' is a folder.");
        }

        Stream? source;
        try
        {
            source = Open(inner);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return (false, null, $"Could not read '{_displayName}': {ex.Message}");
        }

        if (source is null)
        {
            return (false, null, $"Cannot find '{LocationFor(inner)}'.");
        }

        // Extract to a uniquely-named temp file carrying the entry's real extension, so the
        // OS "open" routing and the editor both recognise it.
        var leaf = LeafName(inner);
        var tempPath = Path.Combine(
            Path.GetTempPath(),
            "Conqueror.Net-vfs",
            Guid.NewGuid().ToString("n") + "-" + Sanitize(leaf));

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
            using (source)
            {
                using var destination = File.Create(tempPath);
                source.CopyTo(destination);
            }

            return (true, tempPath, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, null, ex.Message);
        }
    }

    /// <summary>Strips characters that are illegal in a file name, keeping the extension.</summary>
    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return cleaned.Length == 0 ? "extracted" : cleaned;
    }
}


