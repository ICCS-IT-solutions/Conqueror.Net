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

    // Virtual entries (archive members, remote listings) carry their metadata directly rather
    // than through a FileSystemInfo, which only the real file system produces. Info stays null
    // for those, and every member below falls back to the stored fields when it is.
    private readonly bool _isVirtual;
    private readonly string _virtualPath = string.Empty;
    private readonly string _virtualName = string.Empty;
    private readonly bool _virtualIsDirectory;
    private readonly long _virtualLength;
    private readonly DateTime _virtualModified;

    public FileSystemEntry(FileSystemInfo info)
    {
        Info = info ?? throw new ArgumentNullException(nameof(info));
    }

    /// <summary>
    /// Builds an entry that is not backed by a <see cref="FileSystemInfo"/> — a node inside an
    /// archive, or a remote listing. <see cref="Info"/> is null; the metadata passed here is
    /// authoritative.
    /// </summary>
    public FileSystemEntry(
        string fullPath,
        string name,
        bool isDirectory,
        long length,
        DateTime modified
    )
    {
        _isVirtual = true;
        _virtualPath = fullPath;
        _virtualName = name;
        _virtualIsDirectory = isDirectory;
        _virtualLength = isDirectory ? 0 : length;
        _virtualModified = modified;
    }

    public FileSystemInfo? Info { get; }

    public string FullPath => Info?.FullName ?? _virtualPath;

    public string Name => Info?.Name ?? _virtualName;

    public FileAttributes Attributes => _attributes ??= ReadAttributes();

    public bool IsDirectory => _isVirtual
        ? _virtualIsDirectory
        : Attributes.HasFlag(FileAttributes.Directory);

    public bool IsHidden =>
        Attributes.HasFlag(FileAttributes.Hidden) || Attributes.HasFlag(FileAttributes.System);

    /// <summary>Junctions/symlinks. Explorer shows these with a shortcut overlay.</summary>
    public bool IsLink => !_isVirtual && Attributes.HasFlag(FileAttributes.ReparsePoint);

    public bool IsReadOnly => Attributes.HasFlag(FileAttributes.ReadOnly);

    public long Length => IsDirectory ? 0 : _length ??= ReadLength();

    public DateTime Modified => Info?.LastWriteTime ?? _virtualModified;

    public string Extension => IsDirectory
        ? string.Empty
        : Info?.Extension ?? Path.GetExtension(_virtualName);


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

            // Shortcuts always use the shortcut SVG icon — MIME sniffing a binary .lnk
            // returns text/plain, which would draw a text page behind the XP overlay.
            if (string.Equals(Extension, ".lnk", StringComparison.OrdinalIgnoreCase))
            {
                return _iconKey = Icons.FileIconResolver.Shortcut;
            }

            // A virtual entry's path is not a real file, so there is nothing to sniff; resolve
            // from the extension alone. This also avoids a wasted open attempt per row.
            if (_isVirtual)
            {
                return _iconKey = Icons.FileIconResolver.ForExtension(Extension);
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
    /// no artwork for; batch files; and executables. DLL/SYS files get the library artwork,
    /// and audio/video files get the XP speaker/filmstrip overlays. Everything else keeps
    /// its vector icon, so the converter returning null is the normal path rather than a failure.
    /// </para>
    /// <para>
    /// A shortcut is classified by its target and a program link then carries its path for icon
    /// extraction, both of which cost COM/shell round trips, so the answers are cached here for
    /// the life of the entry. Enumeration does not call this: it runs when the view asks for an
    /// icon, so a folder that is listed but never drawn never pays for it.
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
            // A program link carries its own path so the converter can try the target's real
            // icon first and fall back to the XP program artwork when the shell has none. An
            // unreadable or broken link cannot be classified at all, and keeps the plain
            // document-arrow artwork rather than losing the overlay entirely.
            // Batch file shortcuts get the bat-file icon with the arrow overlay — extracting
            // the target's icon from a .bat would show the default text icon behind it.
            if (Services.ShortcutTarget.TryGetTarget(FullPath, out var target))
            {
                var targetExt = Path.GetExtension(target);
                if (string.Equals(targetExt, ".bat", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(targetExt, ".cmd", StringComparison.OrdinalIgnoreCase))
                {
                    return "Icon.Xp.BatFileShortcut@" + FullPath;
                }
            }

            return Services.ShortcutTarget.IsProgramShortcut(FullPath)
                ? "Icon.Xp.ProgramShortcut@" + FullPath
                : "Icon.Xp.FileShortcut";
        }

        if (Services.ShortcutTarget.ProgramExtensions.Contains(Extension))
        {
            // .exe and .com are programs; .bat and .cmd are scripts and have their own artwork.
            return string.Equals(Extension, ".bat", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Extension, ".cmd", StringComparison.OrdinalIgnoreCase)
                ? "Icon.Xp.BatFile"
                : "Icon.Xp.Program";
        }

        // DLL and SYS files get the library artwork, distinct from the program icon.
        if (string.Equals(Extension, ".dll", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Extension, ".sys", StringComparison.OrdinalIgnoreCase))
        {
            return "Icon.Xp.LibFile";
        }

        // INI, CFG, CONF, and XML config files get the XP notepad-with-gear icon.
        if (string.Equals(Extension, ".ini", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Extension, ".cfg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Extension, ".conf", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Extension, ".xml", StringComparison.OrdinalIgnoreCase))
        {
            return "Icon.Xp.ConfigFile";
        }

        // Audio files (mp3, wav, flic, etc.) get the XP speaker overlay.
        if (Icons.FileIconResolver.IsAudioExtension(Extension))
        {
            return "Icon.Xp.Audio";
        }

        // Video files (mp4, avi, mkv, etc.) get the XP film-strip overlay.
        if (Icons.FileIconResolver.IsVideoExtension(Extension))
        {
            return "Icon.Xp.Video";
        }

        return null;
    }

    public string SizeDisplay => IsDirectory ? string.Empty : ByteSizeFormatter.Format(Length);

    public string ModifiedDisplay => Modified.ToString("dd/MM/yyyy HH:mm");

    private FileAttributes ReadAttributes()
    {
        // A virtual entry has no FileSystemInfo; synthesise the flags from the stored metadata.
        if (_isVirtual)
        {
            return _virtualIsDirectory ? FileAttributes.Directory : FileAttributes.Normal;
        }

        try
        {
            // _isVirtual is false here (the virtual branch returned above), so Info is non-null.
            return Info!.Attributes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Broken symlink, vanished item, or an ACL we cannot read.
            return default;
        }
    }

    private long ReadLength()
    {
        // A virtual entry carries its size directly; there is no FileInfo to ask.
        if (_isVirtual)
        {
            return _virtualLength;
        }

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
