using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Conqueror.Net.WebBrowserUi.Services;

public sealed class FileBasedExtensionService : IExtensionService
{
    private readonly string _storeDir;
    private readonly string _statePath;
    private readonly Dictionary<string, bool> _enabledOverride = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>Raised after any change to the enabled set; CefExtensionHost re-syncs on it.</summary>
    public event Action? ExtensionsChanged;

    public FileBasedExtensionService(string? storeDir = null)
    {
        _storeDir = storeDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Conqueror.Net", "extensions");
        _statePath = Path.Combine(_storeDir, "enabled.json");
        Directory.CreateDirectory(_storeDir);
        LoadState();
    }

    public Task<IEnumerable<ExtensionInfo>> GetInstalledExtensionsAsync()
    {
        var list = new List<ExtensionInfo>();
        foreach (var dir in Directory.GetDirectories(_storeDir))
        {
            var manifest = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifest)) continue;

            try
            {
                list.Add(ReadManifest(Path.GetFileName(dir), manifest));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // Prevent one corrupted extension from crashing enumeration
            }
        }

        return Task.FromResult<IEnumerable<ExtensionInfo>>(list);
    }

    public Task<ExtensionInfo> LoadUnpackedExtensionAsync(string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
        {
            throw new FileNotFoundException("Manifest file not found.", manifestPath);
        }

        var sourceDir = Path.GetDirectoryName(manifestPath)!;
        var id = ReadId(manifestPath) ?? Path.GetFileName(sourceDir);
        var destDir = Path.Combine(_storeDir, id);

        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            var dest = Path.Combine(destDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }

        var info = ReadManifest(id, Path.Combine(destDir, "manifest.json"));
        lock (_gate)
        {
            _enabledOverride[id] = true;
            SaveStateLocked();
        }

        ExtensionsChanged?.Invoke();
        return Task.FromResult(info with { IsEnabled = true });
    }

    public Task EnableExtensionAsync(string extensionId)
    {
        SetEnabled(extensionId, true);
        return Task.CompletedTask;
    }

    public Task DisableExtensionAsync(string extensionId)
    {
        SetEnabled(extensionId, false);
        return Task.CompletedTask;
    }

    public Task UnloadExtensionAsync(string extensionId)
    {
        lock (_gate)
        {
            _enabledOverride.Remove(extensionId);
            SaveStateLocked();
        }

        var dir = Path.Combine(_storeDir, extensionId);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        ExtensionsChanged?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Absolute root directories of enabled extensions that carry a manifest.json —
    /// ready for CefRequestContext.LoadExtension.
    /// </summary>
    public IReadOnlyList<string> GetEnabledExtensionRoots()
    {
        var roots = new List<string>();

        foreach (var dir in Directory.GetDirectories(_storeDir))
        {
            if (!IsEnabled(Path.GetFileName(dir))) continue;
            if (File.Exists(Path.Combine(dir, "manifest.json")))
            {
                roots.Add(dir);
            }
        }

        return roots;
    }

    /// <summary>
    /// Evaluates active extensions against the current tab URL and groups the matching
    /// content-script file paths per extension root, in execution order. Only roots CEF
    /// could not load natively should be injected from here — a natively hosted
    /// extension injects its own scripts at the manifest-declared times.
    /// </summary>
    public IReadOnlyList<ContentScriptGroup> GetEnabledContentScriptGroups(string? targetUrl = null)
    {
        var groups = new List<ContentScriptGroup>();

        foreach (var dir in Directory.GetDirectories(_storeDir))
        {
            var id = Path.GetFileName(dir);
            if (!IsEnabled(id)) continue;

            var manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath)) continue;

            var files = new List<string>();
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                if (!doc.RootElement.TryGetProperty("content_scripts", out var contentScripts) ||
                    contentScripts.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var csBlock in contentScripts.EnumerateArray())
                {
                    // Match URL patterns against Chrome extension match rules
                    if (!string.IsNullOrEmpty(targetUrl) && csBlock.TryGetProperty("matches", out var matchesArr))
                    {
                        var patterns = matchesArr.EnumerateArray().Select(m => m.GetString()).OfType<string>();
                        if (!IsUrlMatchingPatterns(targetUrl, patterns))
                        {
                            continue;
                        }
                    }

                    // Collect script files in order
                    if (csBlock.TryGetProperty("js", out var jsArr) && jsArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var jsFile in jsArr.EnumerateArray())
                        {
                            var rel = jsFile.GetString();
                            if (string.IsNullOrWhiteSpace(rel)) continue;

                            var full = Path.Combine(dir, rel);
                            if (File.Exists(full))
                            {
                                files.Add(full);
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // Ignore faulty extension files during active navigation
            }

            if (files.Count > 0)
            {
                groups.Add(new ContentScriptGroup(dir, files));
            }
        }

        return groups;
    }

    /// <summary>
    /// Tests a URL against Chrome match patterns. Gates only the *fallback* injection
    /// path; a natively loaded extension is pattern-matched by CEF itself, which knows
    /// the real Chrome semantics.
    /// </summary>
    private static bool IsUrlMatchingPatterns(string url, IEnumerable<string> matchPatterns)
    {
        foreach (var pattern in matchPatterns)
        {
            if (pattern == "<all_urls>")
            {
                if (Regex.IsMatch(url, @"^(https?|ftp|file):", RegexOptions.IgnoreCase)) return true;
                continue;
            }

            var regex = TryBuildMatchPatternRegex(pattern);
            if (regex is null) continue;

            try
            {
                if (Regex.IsMatch(url, regex, RegexOptions.IgnoreCase)) return true;
            }
            catch (ArgumentException)
            {
                // A pattern that cannot compile must not take navigation down.
            }
        }
        return false;
    }

    /// <summary>
    /// Builds a regex for one Chrome match pattern: <c>scheme://host/path</c> where
    /// <c>*</c> is the only wildcard (a leading <c>*</c> on a host widens to subdomains
    /// as well as the apex, the port is ignored, and <c>*</c> in the path spans '/').
    /// Returns null for malformed patterns. E.g. <c>*://*.google.com/*</c> becomes
    /// <c>^(?:https?|ftp|file)://(?:[^/:]+\.)?google\.com(?::\d+)?/.*$</c>.
    /// </summary>
    private static string? TryBuildMatchPatternRegex(string pattern)
    {
        var schemeEnd = pattern.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0) return null;

        var rest = pattern[(schemeEnd + 3)..];
        var pathStart = rest.IndexOf('/');
        if (pathStart < 0) return null;

        var schemePart = pattern[..schemeEnd];
        var hostPart = rest[..pathStart];
        var pathPart = rest[pathStart..];

        // "*" covers the schemes match patterns allow.
        var schemeRegex = schemePart == "*"
            ? @"(?:https?|ftp|file)"
            : Regex.Escape(schemePart);

        string hostRegex;
        if (hostPart == "*")
        {
            hostRegex = "[^/]*";
        }
        else if (hostPart.StartsWith("*.", StringComparison.Ordinal))
        {
            hostRegex = "(?:[^/:]+\\.)?" + Regex.Escape(hostPart[2..]);
        }
        else
        {
            hostRegex = Regex.Escape(hostPart);
        }

        // Match patterns ignore the port; the wildcard host already stops at '/'.
        if (hostPart != "*")
        {
            hostRegex += @"(?::\d+)?";
        }

        // Path wildcards: * spans any characters (including /), ? exactly one.
        var pathRegex = string.Concat(pathPart.Select(c => c switch
        {
            '*' => ".*",
            '?' => ".",
            _ => Regex.Escape(c.ToString()),
        }));

        return "^" + schemeRegex + "://" + hostRegex + pathRegex + "$";
    }

    private ExtensionInfo ReadManifest(string id, string manifestPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = doc.RootElement;
        var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? id : id;
        var version = root.TryGetProperty("version", out var v) ? v.GetString() ?? "0.0" : "0.0";
        var description = root.TryGetProperty("description", out var d) ? d.GetString() ?? string.Empty : string.Empty;

        string? icon = null;
        if (root.TryGetProperty("icons", out var icons) && icons.ValueKind == JsonValueKind.Object)
        {
            var best = -1;
            foreach (var prop in icons.EnumerateObject())
            {
                if (int.TryParse(prop.Name, out var size) && size > best)
                {
                    var candidate = Path.Combine(Path.GetDirectoryName(manifestPath)!, prop.Value.GetString() ?? string.Empty);
                    if (File.Exists(candidate))
                    {
                        icon = candidate;
                        best = size;
                    }
                }
            }
        }

        return new ExtensionInfo(id, name, version, description, IsEnabled(id), icon);
    }

    private static string? ReadId(string manifestPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (doc.RootElement.TryGetProperty("id", out var id))
            {
                return id.GetString();
            }

            if (doc.RootElement.TryGetProperty("name", out var name))
            {
                var slug = new string(name.GetString()?.Where(char.IsLetterOrDigit).ToArray());
                return string.IsNullOrEmpty(slug) ? null : slug.ToLowerInvariant();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    private bool IsEnabled(string id)
    {
        lock (_gate)
        {
            return !_enabledOverride.TryGetValue(id, out var enabled) || enabled;
        }
    }

    private void SetEnabled(string id, bool enabled)
    {
        lock (_gate)
        {
            _enabledOverride[id] = enabled;
            SaveStateLocked();
        }

        ExtensionsChanged?.Invoke();
    }

    private void LoadState()
    {
        try
        {
            if (!File.Exists(_statePath)) return;

            using var doc = JsonDocument.Parse(File.ReadAllText(_statePath));
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.True || prop.Value.ValueKind == JsonValueKind.False)
                {
                    _enabledOverride[prop.Name] = prop.Value.GetBoolean();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
    }

    private void SaveStateLocked()
    {
        File.WriteAllText(_statePath, JsonSerializer.Serialize(_enabledOverride));
    }
}
