using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Xml;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;

namespace Conqueror.Net.CodeEditorUi.ViewModels;

/// <summary>
/// Maps a file extension to an AvaloniaEdit <see cref="IHighlightingDefinition"/>,
/// using built-in definitions where AvaloniaEdit ships one and custom XSHD
/// definitions embedded as assembly resources otherwise.
/// </summary>
/// <remarks>
/// Custom definitions are registered once on first use via
/// <see cref="RegisterCustomHighlightings"/>, so lookups after the first call
/// are a plain dictionary + manager.GetDefinition hit.
/// </remarks>
public static class SyntaxHighlightingResolver
{
    // Built-in AvaloniaEdit definition names, keyed by extension.
    // These match what HighlightingManager.Instance already knows about.
    private static readonly Dictionary<string, string> BuiltIns = new(StringComparer.OrdinalIgnoreCase)
    {
        { ".cs", "C#" },
        { ".css", "CSS" },
        { ".js", "JavaScript" },
        { ".json", "Json" },
        { ".md", "MarkDown" },
        { ".ps1", "PowerShell" },
        { ".psm1", "PowerShell" },
        { ".psd1", "PowerShell" },
        { ".xml", "XML" },
        { ".htm", "HTML" },
        { ".html", "HTML" },
    };

    // Extensions that fall back to a custom XSHD definition.
    private static readonly Dictionary<string, string> CustomLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        { ".ini", "INI" },
        { ".cfg", "INI" },
        { ".conf", "INI" },
        { ".yaml", "Yaml" },
        { ".yml", "Yaml" },
        { ".toml", "Toml" },
        { ".bat", "Batch" },
        { ".sh", "Shell" },
        { ".reg", "RegFile" },
    };

    private static readonly Assembly Assembly = typeof(SyntaxHighlightingResolver).Assembly;

    private static readonly Lazy<bool> _registered = new(static () =>
    {
        RegisterCustomHighlightings();
        return true;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Resolves an <see cref="IHighlightingDefinition"/> for the given file extension
    /// (including the leading dot). Returns null for plain-text types like .log or .txt.
    /// </summary>
    public static IHighlightingDefinition? Resolve(string? extension)
    {
        if (string.IsNullOrEmpty(extension))
        {
            return null;
        }

        // Trigger one-time registration of custom XSHD definitions.
        _ = _registered.Value;

        // Try built-in definitions first.
                if (BuiltIns.TryGetValue(extension, out var builtInName))
        {
            return HighlightingManager.Instance.GetDefinition(builtInName);
        }

        // Then custom definitions.
                if (CustomLanguages.TryGetValue(extension, out var customName))
        {
            return HighlightingManager.Instance.GetDefinition(customName);
        }

        // .txt, .log, and anything else: plain text (null highlighting).
        return null;
    }

    private static void RegisterCustomHighlightings()
    {
        RegisterFromResource("INI",     "Conqueror.Net.CodeEditorUi.Highlighting.Ini.xshd",     new[] { ".ini", ".cfg", ".conf" });
        RegisterFromResource("Yaml",    "Conqueror.Net.CodeEditorUi.Highlighting.Yaml.xshd",    new[] { ".yaml", ".yml" });
        RegisterFromResource("Toml",    "Conqueror.Net.CodeEditorUi.Highlighting.Toml.xshd",    new[] { ".toml" });
        RegisterFromResource("Batch",   "Conqueror.Net.CodeEditorUi.Highlighting.Batch.xshd",   new[] { ".bat" });
        RegisterFromResource("Shell",   "Conqueror.Net.CodeEditorUi.Highlighting.Shell.xshd",   new[] { ".sh" });
        RegisterFromResource("RegFile", "Conqueror.Net.CodeEditorUi.Highlighting.RegFile.xshd", new[] { ".reg" });
    }

    private static void RegisterFromResource(string name, string resourceName, string[] extensions)
    {
        var stream = Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            App.Log?.Invoke($"syntax highlighting resource not found: {resourceName}");
            return;
        }

        try
        {
            using var reader = new XmlTextReader(stream);
                        var definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            HighlightingManager.Instance.RegisterHighlighting(name, extensions, definition);
        }
        catch (Exception ex)
        {
            App.Log?.Invoke($"failed to load highlighting '{name}': {ex.Message}");
        }
    }
}
