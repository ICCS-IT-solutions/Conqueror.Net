using System.Text.Json;
using System.Text.Json.Serialization;
using Conqueror.Net.Shell.Models;

namespace Conqueror.Net.Shell.Services;

/// <summary>
/// The shell's user preferences, persisted between runs.
/// </summary>
/// <remarks>
/// A single JSON file under %LOCALAPPDATA%\Conqueror.Net, the same place the file manager keeps
/// its CEF cache. Serialisation is intentionally failure-tolerant: a missing, unreadable or
/// malformed file falls back to defaults rather than preventing the shell from starting, because
/// a shell that will not launch is worse than a shell that forgets an edge.
/// </remarks>
public sealed class ShellSettings
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Which edge the taskbar is anchored to.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="TaskbarEdge.Bottom"/> - XP's traditional position. Setting it to
    /// <see cref="TaskbarEdge.MatchWindows"/> makes the shell follow the real taskbar instead.
    /// </remarks>
    public TaskbarEdge Edge { get; set; } = TaskbarEdge.Bottom;

    /// <summary>Whether the Start menu opens from the taskbar's own edge rather than the screen's.</summary>
    public bool MenuFollowsBar { get; set; } = true;

    /// <summary>
    /// Where the settings file lives.
    /// </summary>
    /// <remarks>
    /// A method rather than a constant so tests and tooling can point it elsewhere; the shell
    /// itself never reassigns it.
    /// </remarks>
    public static string FilePath { get; private set; } = DefaultPath();

    private static string DefaultPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Conqueror.Net",
            "shell-settings.json"
        );

    /// <summary>Loads settings, or returns defaults when the file cannot be used.</summary>
    public static ShellSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<ShellSettings>(json, Options);

                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            ShellApp.Log?.Invoke($"settings unreadable, using defaults: {ex.Message}");
        }

        return new ShellSettings();
    }

    /// <summary>Writes settings, logging rather than throwing if the disk refuses.</summary>
    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The bar still works this run; it just will not remember the choice next time.
            ShellApp.Log?.Invoke($"could not save settings: {ex.Message}");
        }
    }
}