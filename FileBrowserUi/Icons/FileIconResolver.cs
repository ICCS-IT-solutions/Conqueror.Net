using Avalonia.Platform;

namespace Conqueror.Net.FileBrowserUi.Icons;
/// <summary>
/// Maps a file, folder or volume onto a key in the SVG icon theme.
/// </summary>
/// <remarks>
/// Keys are the file names produced by <c>tools/import-icon-theme.ps1</c>, which selects a
/// small curated subset from an upstream pack. Any extension not listed falls back to
/// <see cref="Text"/>, which is the page icon - the same catch-all Explorer uses.
/// </remarks>
public static class FileIconResolver
{
    private const string AssetPrefix = "avares://Conqueror.Net/Assets/Icons/";

    public const string Folder = "folder";
    public const string FolderRemote = "folder-remote";
    public const string Text = "text";
    public const string Script = "script";
    public const string Image = "image";
    public const string Audio = "audio";
    public const string Video = "video";
    public const string Archive = "archive";
    public const string Package = "package";
    public const string Executable = "executable";
    public const string Install = "install";
    public const string Pdf = "pdf";
    public const string Document = "document";
    public const string DriveHardDisk = "drive-harddisk";
    public const string DriveOptical = "drive-optical";
    public const string Network = "network";
    public const string Desktop = "desktop";
    public const string Home = "home";
    public const string Trash = "trash";

    /// <summary>Icon key by extension, with the dot omitted.</summary>
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        // Text and code
        [".txt"] = Text, [".log"] = Text, [".md"] = Text, [".ini"] = Text, [".cfg"] = Text,
        [".conf"] = Text, [".csv"] = Text, [".tsv"] = Text, [".json"] = Text, [".xml"] = Text,
        [".yaml"] = Text, [".yml"] = Text, [".toml"] = Text, [".rtf"] = Document,
        [".cs"] = Script, [".xaml"] = Script, [".csproj"] = Script, [".sln"] = Script,
        [".py"] = Script, [".js"] = Script, [".ts"] = Script, [".java"] = Script,
        [".c"] = Script, [".h"] = Script, [".cpp"] = Script, [".rs"] = Script, [".go"] = Script,
        [".sh"] = Script, [".ps1"] = Script, [".bat"] = Script, [".cmd"] = Script,

        // Documents
        [".doc"] = Document, [".docx"] = Document, [".odt"] = Document,
        [".xls"] = Document, [".xlsx"] = Document, [".ods"] = Document, [".csv.xls"] = Document,
        [".ppt"] = Document, [".pptx"] = Document, [".odp"] = Document,
        [".pdf"] = Pdf, [".ps"] = Document, [".eps"] = Document,

        // Images
        [".png"] = Image, [".jpg"] = Image, [".jpeg"] = Image, [".gif"] = Image,
        [".bmp"] = Image, [".tif"] = Image, [".tiff"] = Image, [".svg"] = Image,
        [".webp"] = Image, [".ico"] = Image, [".psd"] = Image,

        // Media
        [".mp3"] = Audio, [".wav"] = Audio, [".flac"] = Audio, [".m4a"] = Audio,
        [".ogg"] = Audio, [".aac"] = Audio, [".wma"] = Audio,
        [".mp4"] = Video, [".mkv"] = Video, [".avi"] = Video, [".mov"] = Video,
        [".wmv"] = Video, [".webm"] = Video, [".m4v"] = Video, [".flv"] = Video,

        // Archives and packages
        [".zip"] = Archive, [".7z"] = Archive, [".rar"] = Archive, [".tar"] = Archive,
        [".gz"] = Archive, [".bz2"] = Archive, [".xz"] = Archive, [".cab"] = Archive,
        [".nupkg"] = Package, [".whl"] = Package, [".jar"] = Package, [".apk"] = Package,
        [".deb"] = Package, [".rpm"] = Package,

        // Executables and installers
        [".exe"] = Executable, [".dll"] = Executable, [".msi"] = Install, [".appx"] = Install,
    };

    /// <summary>Icon key for a file extension, or <see cref="Text"/> when unrecognised.</summary>
    public static string ForExtension(string extension) =>
        !string.IsNullOrEmpty(extension) && ByExtension.TryGetValue(extension, out var key)
            ? key
            : Text;

    /// <summary>Icon key for a file, taking its extension into account.</summary>
    public static string ForFile(FileBrowserUi.Models.FileSystemEntry entry) =>
        ForExtension(entry.Extension);

    /// <summary>
    /// Icon key chosen from a MIME type rather than a file extension.
    /// </summary>
    /// <remarks>
    /// The upstream pack is a freedesktop.org icon theme, so its mimetype artwork is already
    /// named after MIME types - <c>application/pdf</c> is <c>application-pdf.svg</c>. That
    /// makes most lookups a direct transform, and the generic per-top-level-type icons act as
    /// the fallback, which is what the theme's own viewers do.
    /// </remarks>
    public static string ForMimeType(string mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType))
        {
            return Text;
        }

        var slash = mimeType.IndexOf('/');

        if (slash <= 0)
        {
            return Text;
        }

        var top = mimeType[..slash];
        var subtype = mimeType[(slash + 1)..].TrimEnd(';', ' ');

        // The theme spells generic members as "image-x-generic" and "text-x-generic", so a
        // literal substitution would miss them.
        var generic = top switch
        {
            "image" => Image,
            "audio" => Audio,
            "video" => Video,
            "text" => Text,
            _ => Text,
        };

        // Exact match first: only the curated assets actually exist on disk, so a candidate
        // has to be checked rather than assumed.
        foreach (var candidate in Candidates(subtype, generic))
        {
            if (IsBundledIcon(candidate))
            {
                return candidate;
            }
        }

        return generic;
    }

    /// <summary>
    /// Names to try for a MIME subtype, most specific first.
    /// </summary>
    /// <remarks>
    /// The final entry is always <paramref name="generic"/>, so this never returns empty and
    /// the caller cannot fall off the end.
    /// </remarks>
    private static IEnumerable<string> Candidates(string subtype, string generic)
    {
        if (subtype.Length > 0)
        {
            // "vnd.openxmlformats-officedocument.wordprocessingml.document" -> as-is, then
            // progressively truncated at '.' and '-' boundaries, which is how the theme names
            // its more specific siblings (e.g. "application-vnd.ms-excel").
            var trimmed = subtype;

            yield return "application-" + trimmed;

            while (true)
            {
                var cut = trimmed.LastIndexOfAny(['.', '-']);

                if (cut <= 0)
                {
                    break;
                }

                trimmed = trimmed[..cut];

                yield return "application-" + trimmed;
            }

            // Non-application types keep their own top-level name.
            yield return subtype;
        }

        yield return generic;
    }

    /// <summary>
    /// Whether an icon key is one of the curated assets.
    /// </summary>
    /// <remarks>
    /// Probed through <see cref="AssetLoader"/> rather than against a hand-kept list, which
    /// could drift from what <c>tools/import-icon-theme.ps1</c> actually copied. Avalonia packs
    /// every <c>AvaloniaResource</c> into a single embedded blob rather than into individually
    /// named manifest resources, so assembly inspection would not see them at all.
    /// </remarks>
    private static bool IsBundledIcon(string key)
    {
        lock (_probeLock)
        {
            if (_bundled.Contains(key))
            {
                return true;
            }
        }

        if (!AssetLoader.Exists(new Uri(FileIconResolver.UriFor(key), UriKind.Absolute)))
        {
            return false;
        }

        lock (_probeLock)
        {
            _bundled.Add(key);
        }

        return true;
    }

    private static readonly HashSet<string> _bundled = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Guards <see cref="_bundled"/>. <see cref="FileIconResolver"/> is called from the UI
    /// thread while folders load, and list enumeration can run concurrently.
    /// </summary>
    /// <remarks>
    /// <c>System.Threading.Lock</c> would read better but is .NET 9+; this targets net8.0.
    /// The critical sections here are a hash-set probe, so contention is irrelevant.
    /// </remarks>
    private static readonly object _probeLock = new();

    /// <summary>Icon key for a folder, distinguishing the well-known shell locations.</summary>
    public static string ForFolder(string fullPath)
    {
        var path = fullPath.TrimEnd(System.IO.Path.DirectorySeparatorChar);

        string? desktop = null;
        string? documents = null;

        try
        {
            desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Restricted profile: fall through to the generic folder icon.
        }

        if (string.Equals(path, desktop, StringComparison.OrdinalIgnoreCase))
        {
            return Desktop;
        }

        return string.Equals(path, documents, StringComparison.OrdinalIgnoreCase) ? Home : Folder;
    }

    /// <summary>Icon key for a drive or network location.</summary>
    /// <remarks>
    /// Network artwork in this pack embeds a base64 PNG, so
    /// <c>tools/import-icon-theme.ps1</c> deliberately refuses it; <see cref="Network"/> is then
    /// absent and callers must fall back to <see cref="Folder"/>.
    /// </remarks>
    public static string ForShellFolder(FileBrowserUi.Models.ShellFolder folder) =>
        folder.Kind switch
        {
            FileBrowserUi.Models.ShellFolderKind.Drive => DriveHardDisk,
            FileBrowserUi.Models.ShellFolderKind.Network => Network,
            _ => ForFolder(folder.Path),
        };

    /// <summary>Whether an icon key was actually imported as an asset.</summary>
    public static bool IsBundled(string key) => IsBundledIcon(key);

    /// <summary>Turns an icon key into the avares URI the SVG control loads.</summary>
    public static string UriFor(string key) => AssetPrefix + key + ".svg";
}

/// <summary>
/// Converts an icon key from the view-model into the avares URI that
/// <c>Avalonia.Svg.Skia.Svg</c> loads. Keeps the asset path in one place.
/// </summary>
public sealed class IconKeyToUriConverter : Avalonia.Data.Converters.IValueConverter
{
    public static IconKeyToUriConverter Instance { get; } = new();

    public object? Convert(
        object? value,
        Type targetType,
        object? parameter,
        System.Globalization.CultureInfo culture) =>
        FileIconResolver.UriFor(value is string key && key.Length > 0 ? key : FileIconResolver.Text);

    public object? ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}
