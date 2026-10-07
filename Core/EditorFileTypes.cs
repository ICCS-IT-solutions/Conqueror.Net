using System.Text.Json;

namespace Conqueror.Net.Core;

/// <summary>
/// File extensions the in-process code/config editor opens — the shell's claim on types it
/// handles itself instead of handing to the OS.
/// </summary>
/// <remarks>
/// One list, two consumers: the shell routes a double-click straight to an editor tab for
/// these types instead of asking the OS, and the file browser's Edit verb greys itself out
/// on anything else. Sharing the set is what makes Edit mean exactly "open this in the
/// editor the double-click would have chosen", only without the web/shell routing.
///
/// The list is user-editable from Tools ▸ Editor File Types… and persisted next to
/// shell-settings.json with the same failure tolerance: a missing, unreadable or malformed
/// file falls back to <see cref="Shipped"/>. Behaviour always reads <see cref="Contains"/>
/// live, so an applied change takes effect on the next action; only menu grey states need
/// prompting afterwards (MainWindowViewModel.NotifyEditorFileTypesChanged).
/// </remarks>
public static class EditorFileTypes
{
    /// <summary>The list the product ships with; the dialog's Defaults button offers it.</summary>
    private static readonly string[] ShippedExtensions =
    [
        ".txt", ".ini", ".cfg", ".conf", ".config", ".json", ".xml", ".yaml", ".yml",
        ".toml", ".log", ".md", ".cs", ".css", ".js", ".ps1", ".bat", ".sh", ".reg",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Where the user's list lives — the same folder as shell-settings.json.</summary>
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Conqueror.Net",
        "editor-filetypes.json");

    // Declared after everything Load touches: static initialisers run in source order.
    private static readonly HashSet<string> Extensions = Load();

    /// <summary>The shipped list, offered by the dialog's Defaults button before OK commits it.</summary>
    public static IReadOnlyList<string> Shipped => ShippedExtensions;

    /// <summary>The current set, sorted, for the dialog's list.</summary>
    public static IReadOnlyList<string> Sorted =>
        Extensions.OrderBy(e => e, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>True when <paramref name="extension"/> (leading dot included) routes to the editor.</summary>
    public static bool Contains(string? extension) =>
        extension is not null && Extensions.Contains(extension);

    /// <summary>
    /// Replaces the set — the dialog's OK — normalising each entry and persisting the result.
    /// </summary>
    public static void Set(IEnumerable<string> extensions)
    {
        Extensions.Clear();
        foreach (var raw in extensions)
        {
            if (Normalize(raw) is { } extension)
            {
                Extensions.Add(extension);
            }
        }

        Save();
    }

    /// <summary>
    /// Trims, prepends a missing dot, and rejects input that could not be part of an
    /// extension (whitespace or path/pattern characters, i.e. a mistyped file name);
    /// null when unusable.
    /// </summary>
    public static string? Normalize(string? raw)
    {
        var trimmed = raw?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (!trimmed.StartsWith('.'))
        {
            trimmed = "." + trimmed;
        }

        if (trimmed.Any(char.IsWhiteSpace)
            || trimmed.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0)
        {
            return null;
        }

        return trimmed;
    }

    private static HashSet<string> Load()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<string[]>(
                    File.ReadAllText(FilePath), JsonOptions);

                // An empty array is a deliberate "none of them" choice, so only unreadable
                // or malformed content falls back to the shipped list.
                if (loaded is not null)
                {
                    foreach (var raw in loaded)
                    {
                        if (Normalize(raw) is { } extension)
                        {
                            set.Add(extension);
                        }
                    }

                    return set;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            App.Log?.Invoke($"editor file types unreadable, using defaults: {ex.Message}");
        }

        foreach (var extension in ShippedExtensions)
        {
            set.Add(extension);
        }

        return set;
    }

    private static void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Sorted so the file diffs cleanly between runs.
            var ordered = Extensions.OrderBy(e => e, StringComparer.OrdinalIgnoreCase).ToArray();
            File.WriteAllText(FilePath, JsonSerializer.Serialize(ordered, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The session keeps the new list; it just will not be remembered next run.
            App.Log?.Invoke($"could not save editor file types: {ex.Message}");
        }
    }
}
