using System.Collections.Generic;
using System.Linq;

namespace Conqueror.Net.FileBrowserUi.Services;

/// <summary>
/// Clipboard payload for file cut/copy, in Explorer's own format.
/// </summary>
/// <remarks>
/// Uses theCF_HDROP file-drop list plus the "Preferred DropEffect" marker (2 = move, 5 =
/// copy) so files cut here paste as moves in Explorer and vice versa. Stored alongside
/// the plain path list so paste works even where the native format is unavailable.
/// </remarks>
public sealed record FileClipboardPayload(IReadOnlyList<string> Paths, bool IsCut)
{
    /// <summary>Shell ID for the "Preferred DropEffect" clipboard format.</summary>
    public const string PreferredDropEffectFormat = "Preferred DropEffect";

    public string Effect => IsCut ? "move" : "copy";

    public bool IsEmpty => Paths.Count == 0;

    public static FileClipboardPayload Empty { get; } = new FileClipboardPayload([], false);

    public static FileClipboardPayload FromPaths(IEnumerable<string> paths, bool isCut)
    {
        var list = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(System.StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new FileClipboardPayload(list, isCut);
    }

    /// <summary>
    /// Parses an Explorer file-drop clipboard payload back into paths. Used when pasting
    /// files copied from outside the app.
    /// </summary>
    public static bool TryParseDropFiles(string? text, out IReadOnlyList<string> paths)
    {
        paths = [];
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var list = text
            .Split(['\r', '\n'], System.StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim().Trim('"'))
            .Where(p => p.Length > 1 && p[1] == ':')
            .ToList();

        if (list.Count == 0)
        {
            return false;
        }

        paths = list;
        return true;
    }
}
