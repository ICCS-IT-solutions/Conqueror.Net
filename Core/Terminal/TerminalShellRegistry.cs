using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Conqueror.Net.Core.Terminal;

/// <summary>
/// Central registry of known shells and user-selected shell preference.
/// </summary>
public static class TerminalShellRegistry
{
    private static readonly TerminalShell[] KnownShells =
    [
        // Windows shells
        new("pwsh", "PowerShell 7+ (pwsh)", "pwsh.exe", "-NoLogo", "{0}> ", ["windows"]),
        new("powershell", "Windows PowerShell", "powershell.exe", "-NoLogo", "{0}> ", ["windows"]),
        new("cmd", "Command Prompt (cmd)", "cmd.exe", null, "{0}> ", ["windows"]),

        // Unix shells
        new("bash", "Bash", "/bin/bash", "-i", "\u001b[1;32m{0}\u001b[0m$ ", ["linux", "osx"]),
        new("zsh", "Zsh", "/bin/zsh", "-i", "%~ %# ", ["linux", "osx"]),
        new("fish", "Fish", "/usr/bin/fish", "-i", "", ["linux", "osx"]),
        new("sh", "POSIX sh", "/bin/sh", "-i", "{0}$ ", ["linux", "osx"]),
    ];

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Conqueror.Net",
        "terminal-shell.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string? _cachedSelection;

    /// <summary>All known shells, filtered for the current platform.</summary>
    public static IReadOnlyList<TerminalShell> AvailableShells =>
        KnownShells.Where(s => s.IsAvailableOnCurrentPlatform).ToArray();

    /// <summary>The user's preferred shell ID, or null for platform default.</summary>
    public static string? SelectedShellId
    {
        get => _cachedSelection ??= Load();
        set
        {
            _cachedSelection = value;
            Save(value);
        }
    }

    /// <summary>Gets the shell to use, falling back to platform default if selection is unavailable.</summary>
    public static TerminalShell GetSelectedShell()
    {
        if (!string.IsNullOrEmpty(SelectedShellId))
        {
            var selected = AvailableShells.FirstOrDefault(s => s.Id == SelectedShellId);
            if (selected is not null)
            {
                return selected;
            }
        }

        // Platform default: first available shell
        return AvailableShells[0];
    }

    /// <summary>Resolves a shell executable path for the given shell ID.</summary>
    public static (string fileName, string? arguments) ResolveShell(string? shellId = null)
    {
        var shell = string.IsNullOrEmpty(shellId) ? GetSelectedShell() : AvailableShells.FirstOrDefault(s => s.Id == shellId);
        if (shell is null)
        {
            shell = GetSelectedShell();
        }

        // Verify the executable exists
        if (File.Exists(shell.Executable) || IsInPath(shell.Executable))
        {
            return (shell.Executable, shell.Arguments);
        }

        // Fallback: try other available shells
        foreach (var fallback in AvailableShells)
        {
            if (File.Exists(fallback.Executable) || IsInPath(fallback.Executable))
            {
                return (fallback.Executable, fallback.Arguments);
            }
        }

        // Ultimate fallback
        return (shell.Executable, shell.Arguments);
    }

    private static bool IsInPath(string fileName)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar))
        {
            return false;
        }

        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(dir.Trim(), fileName)))
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry
            }
        }

        return false;
    }

    private static string? Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var data = JsonSerializer.Deserialize<ShellSettings>(json, JsonOptions);
                return data?.ShellId;
            }
        }
        catch (Exception) { /* ignore - use default */ }
        return null;
    }

    private static void Save(string? shellId)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var data = new ShellSettings { ShellId = shellId };
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(data, JsonOptions));
        }
        catch (Exception) { /* ignore - best effort */ }
    }

    private sealed record ShellSettings(string? ShellId = null);
}