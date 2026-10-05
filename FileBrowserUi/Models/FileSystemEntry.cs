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
    private string? _xpIconKey;

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

    /// <summary>Key into the SVG icon theme; see <see cref="Icons.FileIconResolver"/>.</summary>
    /// <remarks>
    /// Content wins where it can be trusted: a <c>.png</c> that is really a PDF gets the PDF
    /// icon. Folders never sniff - a directory's leading bytes are meaningless, and opening one
    /// per row during enumeration would be a serious cost in a folder of several thousand.
    /// </remarks>
    public string IconKey
    {
        get
        {
            if (_iconKey is not null)
            {
                return _iconKey;
            }

            if (IsDirectory)
            {
                return _iconKey = Icons.FileIconResolver.ForFolder(FullPath);
            }

            var mime = Services.MimeTypeResolver.Shared.Resolve(FullPath);

            _iconKey = mime switch
            {
                Services.MimeTypeResolver.FallbackMimeType => Icons.FileIconResolver.ForExtension(Extension),
                _ => Icons.FileIconResolver.ForMimeType(mime),
            };

            return _iconKey;
        }
    }

    /// <summary>
    /// XP raster icon key for the file list, or null when the SVG theme should draw this entry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only four cases are rasterised: shortcuts, which need the arrow overlay the flat theme has
    /// no artwork for; batch files; and executables. Everything else keeps its vector icon, so
    /// the converter returning null is the normal path rather than a failure.
    /// </para>
    /// <para>
    /// A shortcut is classified by its target, which costs a COM round trip, so the answer is
    /// cached here for the life of the entry. Enumeration does not call this: it runs when the
    /// view asks for an icon, so a folder that is listed but never drawn never pays for it.
    /// </para>
    /// </remarks>
    public string? XpIconKey
    {
        get
        {
            // Distinct from IconKey's null-as-unset cache: null is a real answer here, so an
            // explicit flag is needed to avoid re-resolving on every redraw.
            if (_xpIconKeyResolved)
            {
                return _xpIconKey;
            }

            _xpIconKeyResolved = true;
            _xpIconKey = ResolveXpIconKey();

            return _xpIconKey;
        }
    }

    private bool _xpIconKeyResolved;

    private string? ResolveXpIconKey()
    {
        // Directories keep the theme's folder artwork, which already reads correctly. A .lnk that
        // points at a folder is still a file, so it is handled by the shortcut branch below.
        if (IsDirectory)
        {
            return null;
        }

        if (string.Equals(Extension, ".lnk", StringComparison.OrdinalIgnoreCase))
        {
            // An unreadable or broken link still looks like a shortcut to the user, so it falls
            // back to the plain document-arrow artwork rather than losing the overlay entirely.
            return Services.ShortcutTarget.IsProgramShortcut(FullPath)
                ? "Icon.Xp.ProgramShortcut"
                : "Icon.Xp.FileShortcut";
        }

        if (Services.ShortcutTarget.ProgramExtensions.Contains(Extension))
        {
            // .exe and .com are programs; .bat is a script and has its own artwork.
            return string.Equals(Extension, ".bat", StringComparison.OrdinalIgnoreCase)
                ? "Icon.Xp.BatFile"
                : "Icon.Xp.Program";
        }

        return string.Equals(Extension, ".cmd", StringComparison.OrdinalIgnoreCase)
            ? "Icon.Xp.BatFile"
            : null;
    }

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
