using System;

namespace Conqueror.Net.FileBrowserUi.Services.Vfs;

/// <summary>
/// A parsed location in the KIO sense: a <see cref="Scheme"/> plus a path that is interpreted
/// by that scheme's service. A bare OS path (or one wrapped in <c>file:</c>) is the local
/// scheme, so every existing call site that passes "C:\foo" or "/home/me" keeps working
/// unchanged.
/// </summary>
/// <remarks>
/// <para>
/// The schemes recognised here are the ones the file pane can render as a folder. Network
/// schemes (<c>ftp</c>, <c>sftp</c>, <c>smb</c>) are accepted by <see cref="Parse"/> so the
/// address bar does not reject them outright, but they are not yet backed by a service; the
/// dispatcher reports them as unsupported rather than throwing.
/// </para>
/// <para>
/// A scheme is only a scheme when it is at least two characters and is not a single drive
/// letter. That is what stops "C:\Users" being read as scheme "c" with path "\Users" — the
/// classic ambiguity on Windows. "file:", "zip:", "tar:" etc. are all two or more characters.
/// </para>
/// </remarks>
public readonly struct VfsLocation : IEquatable<VfsLocation>
{
    /// <summary>Scheme used for the real file system (the default).</summary>
    public const string FileScheme = "file";

    private VfsLocation(string scheme, string path)
    {
        Scheme = scheme;
        Path = path;
    }

    /// <summary>Lower-cased scheme, e.g. "file", "zip", "tar", "ftp".</summary>
    public string Scheme { get; }

    /// <summary>
    /// The path portion, with the scheme prefix removed. For the local scheme this is the
    /// ordinary OS path; for an archive scheme it is the archive file's own path.
    /// </summary>
    public string Path { get; }

    /// <summary>True when this location names something on the real file system.</summary>
    public bool IsLocal => string.Equals(Scheme, FileScheme, StringComparison.Ordinal);

    /// <summary>
    /// Schemes the file pane knows how to render as folders. Anything outside this set is
    /// accepted by <see cref="Parse"/> but has no service behind it yet.
    /// </summary>
    public static bool IsSupportedScheme(string scheme) =>
        string.Equals(scheme, FileScheme, StringComparison.Ordinal)
        || string.Equals(scheme, "zip", StringComparison.Ordinal)
        || string.Equals(scheme, "tar", StringComparison.Ordinal);

    /// <summary>A location on the real file system.</summary>
    public static VfsLocation Local(string path) => new(FileScheme, path);

    /// <summary>
    /// Parses <paramref name="input"/> into a scheme and path.
    /// </summary>
    /// <remarks>
    /// Accepts "zip:/home/me/a.zip", "tar:///srv/b.tar.gz", a plain "/home/me" and a Windows
    /// "C:\Users". A "file:" prefix is stripped. A URI authority ("//host") is ignored for the
    /// local scheme but preserved for the network schemes, where the host is the server.
    /// </remarks>
    public static VfsLocation Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return Local(string.Empty);
        }

        var text = input.Trim();

        var colon = text.IndexOf(':');
        if (colon >= 2)
        {
            var candidate = text[..colon];
            if (IsSchemeToken(candidate))
            {
                var scheme = candidate.ToLowerInvariant();
                var rest = text[(colon + 1)..];

                // "file:///C:/x" and "zip:///srv/a.zip" carry an empty authority before the
                // path; drop a leading "//" so the path starts at the real root.
                if (rest.StartsWith("//", StringComparison.Ordinal))
                {
                    // A non-empty authority (//host/...) is meaningful for network schemes;
                    // for file/zip/tar an authority is meaningless and is discarded.
                    var afterSlashes = rest[2..];
                    var slash = afterSlashes.IndexOf('/');
                    if (IsSupportedScheme(scheme) || slash < 0)
                    {
                        rest = slash >= 0 ? afterSlashes[slash..] : string.Empty;
                    }
                }

                return new VfsLocation(scheme, rest);
            }
        }

        return Local(text);
    }

    /// <summary>
    /// A scheme token is a letter followed by letters, digits, '+', '-' or '.', at least two
    /// characters long so a Windows drive letter is never mistaken for one.
    /// </summary>
    private static bool IsSchemeToken(string token)
    {
        if (token.Length < 2)
        {
            return false;
        }

        if (!char.IsLetter(token[0]))
        {
            return false;
        }

        foreach (var c in token)
        {
            if (!char.IsLetterOrDigit(c) && c != '+' && c != '-' && c != '.')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Canonical text form: the bare path for local, "scheme:path" otherwise.</summary>
    public override string ToString() =>
        IsLocal ? Path : string.Concat(Scheme, ":", Path);

    public bool Equals(VfsLocation other) =>
        string.Equals(Scheme, other.Scheme, StringComparison.Ordinal)
        && string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => obj is VfsLocation other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        StringComparer.Ordinal.GetHashCode(Scheme),
        StringComparer.OrdinalIgnoreCase.GetHashCode(Path)
    );

    public static bool operator ==(VfsLocation left, VfsLocation right) => left.Equals(right);

    public static bool operator !=(VfsLocation left, VfsLocation right) => !left.Equals(right);
}
