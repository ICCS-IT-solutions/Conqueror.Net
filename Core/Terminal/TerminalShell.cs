using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Conqueror.Net.Core.Terminal;

/// <summary>
/// Represents a known shell with its executable, default arguments, and prompt format.
/// </summary>
public sealed record TerminalShell(
    string Id,
    string DisplayName,
    string Executable,
    string? Arguments = null,
    string? PromptFormat = null,  // {0} = working directory
    string[]? Platforms = null)   // null = all platforms
{
    public bool IsAvailableOnCurrentPlatform =>
        Platforms is null || Platforms.Length == 0 ||
        (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Platforms.Contains("windows")) ||
        (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && Platforms.Contains("linux")) ||
        (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && Platforms.Contains("osx"));

    /// <summary>Formats the prompt string for the given working directory.</summary>
    public string FormatPrompt(string workingDirectory) =>
        string.IsNullOrEmpty(PromptFormat) ? "" : string.Format(PromptFormat, workingDirectory);
}