using System.Collections.Concurrent;
using System.Diagnostics;
using MimeDetective;
using MimeDetective.Definitions;

namespace Conqueror.Net.FileBrowserUi.Services;

/// <summary>
/// Works out a file's MIME type so the icon resolver can pick artwork.
/// </summary>
/// <remarks>
/// Two stages, deliberately in this order:
/// <list type="number">
/// <item>Extension lookup in an in-memory table. This is what actually answers for almost
/// every file, and it costs nothing.</item>
/// <item>Magic-number sniffing of the file's first bytes, for files that are misnamed or have
/// no extension at all - the case where the extension would pick the wrong icon.</item>
/// </list>
/// Sniffing opens the file, so it must never run while enumerating a directory: it is lazy
/// and results are cached per extension and per path. See
/// <see cref="SniffFile"/> for the per-call cost.
/// </remarks>
public sealed class MimeTypeResolver
{
    /// <summary>How many leading bytes magic-number matching actually needs.</summary>
    /// <remarks>
    /// Longest signature in the default definition set is well under this, and it is two
    /// page reads for a typical 4 KB cluster.
    /// </remarks>
    public const int HeaderLength = 512;

    /// <summary>Shared instance; the underlying tables are read-only after construction.</summary>
    public static MimeTypeResolver Shared { get; } = new();

    /// <summary>What an unresolved file is reported as.</summary>
    public const string FallbackMimeType = "application/octet-stream";

    /// <summary>
    /// Extensions whose type is settled by the extension alone, whatever the bytes say.
    /// </summary>
    /// <remarks>
    /// Sniffing is a heuristic and these are the cases where it misfires. A Markdown or HTML
    /// file begins with readable ASCII that collides with several text-based magic numbers -
    /// <c>README.md</c> is reported as <c>message/rfc822</c> by the raw matcher - and the
    /// extension is far more trustworthy. The same goes for source files, which look like plain
    /// text but are not <c>text/plain</c> to a user.
    /// </remarks>
    private static readonly HashSet<string> ExtensionWins = new(StringComparer.OrdinalIgnoreCase)
    {
        // Collide with mail/usenet/news magic numbers.
        ".md", ".markdown", ".rst", ".txt", ".log", ".nfo", ".me",
        ".html", ".htm", ".xhtml", ".eml", ".mht",
        // Source and markup: readable text, but a script/document icon is more useful.
        ".cs", ".xaml", ".csproj", ".sln", ".props", ".targets", ".xml", ".json", ".yaml", ".yml",
        ".toml", ".ini", ".cfg", ".conf", ".py", ".js", ".ts", ".tsx", ".jsx", ".java", ".c", ".h",
        ".cpp", ".hpp", ".rs", ".go", ".sh", ".bash", ".ps1", ".bat", ".cmd", ".sql", ".css", ".scss",
    };

    private readonly IContentInspector _inspector;
    private readonly ConcurrentDictionary<string, string> _byPath = new(
        StringComparer.OrdinalIgnoreCase
    );

    private MimeTypeResolver()
    {
        _inspector = new ContentInspectorBuilder { Definitions = DefaultDefinitions.All() }.Build();
    }

    /// <summary>
    /// Resolves the MIME type for a path.
    /// </summary>
    /// <remarks>
    /// The order matters and was chosen from measured behaviour, not intuition:
    /// <list type="number">
    /// <item>Extensions in <see cref="ExtensionWins"/> short-circuit, because sniffing
    /// misidentifies them (see there).</item>
    /// <item>Everything else is sniffed, which is what catches a <c>.png</c> that is really a
    /// PDF or a file with no extension at all.</item>
    /// </list>
    /// Results are cached per path, so revisiting a folder does no further I/O.
    /// </remarks>
    public string Resolve(string path)
    {
        if (_byPath.TryGetValue(path, out var known))
        {
            return known;
        }

        var extension = Path.GetExtension(path);
        var mimeType = ExtensionWins.Contains(extension) ? FromExtension(extension) : SniffFile(path);

        _byPath[path] = mimeType;

        return mimeType;
    }

    /// <summary>
    /// MIME type implied by an extension, without touching the disk.
    /// </summary>
    private static string FromExtension(string extension) =>
        extension.ToLowerInvariant() switch
        {
            ".md" or ".markdown" or ".rst" or ".txt" or ".log" or ".nfo" or ".me" => "text/plain",
            ".html" or ".htm" or ".xhtml" => "text/html",
            ".xml" or ".xaml" => "application/xml",
            ".json" => "application/json",
            ".yaml" or ".yml" or ".toml" or ".ini" or ".cfg" or ".conf" or ".props"
                or ".targets" or ".csproj" or ".sln" => "text/plain",
            _ => "text/x-source",
        };

    /// <summary>
    /// Detects the MIME type from a file's leading bytes.
    /// </summary>
    /// <remarks>
    /// Returns <see cref="FallbackMimeType"/> when the file is missing, unreadable, locked or
    /// simply not recognised. Callers should treat that as "no better idea", not as an error.
    /// </remarks>
    public string SniffFile(string path)
    {
        try
        {
            // Magic numbers live in the first few hundred bytes; reading the whole file would
            // make a folder listing proportional to the size of its contents.
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: HeaderLength,
                FileOptions.SequentialScan
            );

            var header = new byte[(int)Math.Min(stream.Length, HeaderLength)];
            var read = stream.Read(header, 0, header.Length);

            if (read <= 0)
            {
                return FallbackMimeType;
            }

            var match = _inspector.Inspect(header, 0, read).ByMimeType().FirstOrDefault();

            if (!string.IsNullOrEmpty(match?.MimeType))
            {
                return match.MimeType;
            }

            // No magic number matched. If the header decodes as text, calling it a binary
            // blob would be actively misleading - a bare "README" is text/plain.
            return LooksLikeText(header, read) ? "text/plain" : FallbackMimeType;
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Locked by another process, a broken symlink, a special file. Not exceptional in
            // a file manager, which races every other program touching the disk.
            return FallbackMimeType;
        }
    }

    /// <summary>
    /// Heuristic for "this is text": no NUL or other C0 control bytes other than tab,
    /// carriage return and newline.
    /// </summary>
    private static bool LooksLikeText(byte[] header, int length)
    {
        for (var i = 0; i < length; i++)
        {
            var b = header[i];

            var isControl = b < 0x20 && b is not (byte)'\t' and not (byte)'\r' and not (byte)'\n';

            if (isControl)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Measures <see cref="SniffFile"/>, for diagnostics and tests.</summary>
    public static TimeSpan MeasureSniff(string path, int iterations = 20)
    {
        var stopwatch = Stopwatch.StartNew();

        for (var i = 0; i < iterations; i++)
        {
            Shared.SniffFile(path);
        }

        stopwatch.Stop();

        return stopwatch.Elapsed / iterations;
    }
}
