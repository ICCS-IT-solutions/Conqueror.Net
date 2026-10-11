using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Conqueror.Net.WebBrowserUi.ViewModels;

/// <summary>
/// View-model for the User Agent configuration dialog.
/// Provides preset user agent strings for OAuth 2.0 compatibility and allows custom values.
/// </summary>
public partial class UserAgentDialogViewModel : ObservableObject
{
    /// <summary>Predefined user agent presets for common OAuth scenarios.</summary>
    public IReadOnlyList<UserAgentPreset> Presets { get; } = new List<UserAgentPreset>
    {
        new UserAgentPreset("Chrome 120 (Windows)", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"),
        new UserAgentPreset("Edge 120 (Windows)", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 Edg/120.0.0.0"),
        new UserAgentPreset("Firefox 121 (Windows)", "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0"),
        new UserAgentPreset("Safari 17 (macOS)", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Safari/605.1.15"),
        new UserAgentPreset("Custom", ""),
    };

    [ObservableProperty]
    private string _userAgent = string.Empty;

    [ObservableProperty]
    private UserAgentPreset? _selectedPreset;

    public UserAgentDialogViewModel(string? currentUserAgent = null)
    {
        UserAgent = currentUserAgent ?? string.Empty;

        // Try to match current user agent to a preset
        if (!string.IsNullOrWhiteSpace(UserAgent))
        {
            SelectedPreset = Presets.FirstOrDefault(p => p.Value == UserAgent) ?? Presets[^1]; // "Custom"
        }
        else
        {
            SelectedPreset = Presets[0]; // Default to Chrome
        }
    }

    /// <summary>Updates the user agent text when a preset is selected.</summary>
    partial void OnSelectedPresetChanged(UserAgentPreset? value)
    {
        if (value != null)
        {
            UserAgent = value.Value;
        }
    }
}

public sealed record UserAgentPreset(string Name, string Value);