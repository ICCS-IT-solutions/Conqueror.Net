using System.IO;
using System.Text.Json;

namespace Conqueror.Net.WebBrowserUi.Services;

/// <summary>Default <see cref="IExtensionService"/>: manifest.json folders under the extensions store.</summary>
public sealed class FileBasedExtensionService : IExtensionService
{
    private readonly string _storeDir;
    private readonly string _statePath;
    private readonly Dictionary<string, bool> _enabledOverride = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

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
            if (!File.Exists(manifest))
            {
                continue;
            }

            try
            {
                list.Add(ReadManifest(Path.GetFileName(dir), manifest));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // One bad folder must not hide the rest.
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

        return Task.CompletedTask;
    }

    /// <summary>Content-script files for the enabled extensions, injected after navigation.</summary>
    public IReadOnlyList<string> GetEnabledContentScripts()
    {
        var scripts = new List<string>();
        foreach (var dir in Directory.GetDirectories(_storeDir))
        {
            var id = Path.GetFileName(dir);
            if (!IsEnabled(id))
            {
                continue;
            }

            var manifest = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifest))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                if (doc.RootElement.TryGetProperty("content_scripts", out var cs)
                    && cs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in cs.EnumerateArray())
                    {
                        if (entry.TryGetProperty("js", out var js)
                            && js.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var f in js.EnumerateArray())
                            {
                                var rel = f.GetString();
                                if (!string.IsNullOrWhiteSpace(rel))
                                {
                                    var full = Path.Combine(dir, rel);
                                    if (File.Exists(full))
                                    {
                                        scripts.Add(full);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
            }
        }

        return scripts;
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
                var slug = new string(name.GetString()?.Where(c => char.IsLetterOrDigit(c)).ToArray());
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
    }

    private void LoadState()
    {
        try
        {
            if (!File.Exists(_statePath))
            {
                return;
            }

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
