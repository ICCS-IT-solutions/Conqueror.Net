using System;
using System.IO;

namespace Conqueror.Net.FileBrowserUi.Models;

/// <summary>
/// A single file or directory shown in a folder pane. Wraps <see cref="FileSystemInfo"/> and
/// caches the metadata the details/list views need, because attribute and length lookups each
/// cost a filesystem round trip and the UI re-reads them while drawing rows.
/// </summary>
public sealed class FileSystemEntry
{
    private FileAttributes? _attributes;
    private long? _length;
    private string? _typeDescription;
    private string? _iconKey;

    public FileSystemEntry(FileSystemInfo info)
    {
        Info = info ?? throw new ArgumentNullException(nameof(info));
    }

    public FileSystemInfo Info { get; }

    public string FullPath => Info.FullName;

    public string Name => Info.Name;

    public FileAttributes Attributes => _attributes ??= ReadAttributes();

    public bool IsDirectory => Attributes.HasFlag(FileAttributes.Directory);

    public bool IsHidden =>
        Attributes.HasFlag(FileAttributes.Hidden) || Attributes.HasFlag(FileAttributes.System);

    /// <summary>Junctions/symlinks. Explorer shows these with a shortcut overlay.</summary>
    public bool IsLink => Attributes.HasFlag(FileAttributes.ReparsePoint);

    public bool IsReadOnly => Attributes.HasFlag(FileAttributes.ReadOnly);

    public long Length => IsDirectory ? 0 : _length ??= ReadLength();

    public DateTime Modified => Info.LastWriteTime;

    public string Extension => IsDirectory ? string.Empty : Info.Extension;

    /// <summary>Human readable kind shown in the "Type" column, e.g. "Text Document".</summary>
    public string TypeDescription => _typeDescription ??= ResolveTypeDescription();

    /// <summary>Key into the SVG icon theme; see <see cref="FileIconResolver"/>.</summary>
    public string IconKey =>
        _iconKey ??= IsDirectory
            ? Icons.FileIconResolver.ForFolder(FullPath)
            : Icons.FileIconResolver.ForFile(this);

    public string SizeDisplay => IsDirectory ? string.Empty : ByteSizeFormatter.Format(Length);

    public string ModifiedDisplay => Modified.ToString("dd/MM/yyyy HH:mm");

    private FileAttributes ReadAttributes()
    {
        try
        {
            return Info.Attributes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Broken symlink, vanished item, or an ACL we cannot read.
            return default;
        }
    }

    private long ReadLength()
    {
        if (Info is not FileInfo file)
        {
            return 0;
        }

        try
        {
            return file.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private string ResolveTypeDescription()
    {
        if (IsDirectory)
        {
            return IsLink ? "Shortcut to a folder" : "File Folder";
        }

        if (IsLink)
        {
            return "Shortcut";
        }

        return FileTypeTable.Describe(Extension);
    }
}
