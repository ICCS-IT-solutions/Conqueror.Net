namespace Conqueror.Net.FileBrowserUi.Services.Vfs;

/// <summary>
/// One node inside an archive, flattened into a single record so the zip and tar readers can
/// share the folder-tree logic in <see cref="ArchiveFileSystemService"/>.
/// </summary>
/// <param name="Path">
/// Path relative to the archive root, always using '/' as the separator, with no leading
/// "./" and no trailing '/'. A directory is a path with no file body, e.g. "docs/images".
/// </param>
/// <param name="IsDirectory">True for a folder node.</param>
/// <param name="Length">Uncompressed size in bytes; 0 for directories.</param>
/// <param name="Modified">Last-modified timestamp, in local time.</param>
internal readonly record struct ArchiveEntry(
    string Path,
    bool IsDirectory,
    long Length,
    DateTime Modified
);
