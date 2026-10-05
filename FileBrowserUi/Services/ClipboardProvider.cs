using Avalonia.Input.Platform;

namespace Conqueror.Net.FileBrowserUi.Services;

/// <summary>
/// Resolves the platform clipboard without coupling view-models to a visual tree.
/// </summary>
/// <remarks>
/// Avalonia 11 removed <c>Application.Current.Clipboard</c>; the clipboard lives on the
/// <c>TopLevel</c>. Views register it once (e.g. on load via
/// <c>TopLevel.GetTopLevel(this)?.Clipboard</c>) and view-models resolve it here. Null
/// until a view registers, which is also what makes the commands safe headless/tests.
/// </remarks>
public static class ClipboardProvider
{
    /// <summary>Registers the live clipboard. Called once by the shell view on load.</summary>
    public static void Register(IClipboard? clipboard) => Current = clipboard;

    /// <summary>The registered clipboard, or null when no view has registered yet.</summary>
    public static IClipboard? Current { get; private set; }
}
