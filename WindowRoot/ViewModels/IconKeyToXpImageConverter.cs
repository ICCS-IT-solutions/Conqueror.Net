using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Conqueror.Net.WindowRoot.ViewModels;

/// <summary>
/// Resolves an XP tab icon key to a decoded bitmap, e.g. "Icon.Xp.Explorer" to the 16 px
/// Explorer folder-with-magnifier from <c>Assets/Icons/xp</c>.
/// </summary>
/// <remarks>
/// <para>
/// Returns a <see cref="Bitmap"/> rather than a URI string because <c>Image.Source</c> is
/// <c>IImage</c>. A literal URI in XAML is converted by the XAML compiler, but a string arriving
/// from a binding has no matching runtime conversion, so it silently draws nothing.
/// </para>
/// <para>
/// The size suffix follows the requested render size, because the pack is downscaled to exactly
/// the two sizes the chrome draws. Anything else falls back to 16, the tab-strip size.
/// </para>
/// </remarks>
public sealed class IconKeyToXpImageConverter : IValueConverter
{
    private const string Prefix = "avares://Conqueror.Net/Assets/Icons/xp/";

    /// <summary>
    /// Icon keys to manifest file stems. The pack is kebab-case and does not follow from the key,
    /// so this is spelled out rather than derived: "Icon.Xp.CommandPrompt" is
    /// "xp-command-prompt", not "xp-commandprompt".
    /// </summary>
    private static readonly Dictionary<string, string> Files = new(StringComparer.Ordinal)
    {
        ["Icon.Xp.Explorer"] = "xp-explorer",
        ["Icon.Xp.CommandPrompt"] = "xp-command-prompt",
        ["Icon.Xp.Connection"] = "xp-connection",
        ["Icon.Xp.Back"] = "xp-back",
        ["Icon.Xp.Forward"] = "xp-forward",
        ["Icon.Xp.Up"] = "xp-up",
        ["Icon.Xp.Go"] = "xp-go",

        // File-list artwork. These carry the shortcut arrow that the flat SVG theme has no
        // equivalent for, so a .lnk in the file list looks like a real XP shortcut rather than
        // a plain document.
        ["Icon.Xp.ProgramShortcut"] = "xp-programshortcut",
        ["Icon.Xp.FileShortcut"] = "xp-fileshortcut",
        ["Icon.Xp.BatFile"] = "xp-batfile",
        ["Icon.Xp.Program"] = "xp-program",

        // The editor tab. xp-configfile is the XP notepad-with-gear artwork from the pack,
        // which reads as "text/config file" the way a plain document glyph would not.
        ["Icon.Xp.ConfigFile"] = "xp-configfile",
    };

    /// <summary>
    /// Decoded icons, keyed by avares URI. Every tab binds through here, so without this the
    /// same file would be re-decoded once per tab and once per redraw.
    /// </summary>
    private static readonly Dictionary<string, Bitmap> Cache = new();

    public static IconKeyToXpImageConverter Instance { get; } = new();

    public object? Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    )
    {
        if (value is not string key || !Files.TryGetValue(key, out var stem))
        {
            return null;
        }

        var uri = $"{Prefix}{stem}{ChooseSize(parameter)}.png";

        // Locked because several tab DataTemplates can convert concurrently during startup.
        lock (Cache)
        {
            if (Cache.TryGetValue(uri, out var cached))
            {
                return cached;
            }

            Bitmap bitmap;

            try
            {
                // The Bitmap(string) constructor treats its argument as a file path, not as an
                // avares URI, so it looks in the working directory and throws. Going through the
                // asset loader explicitly is what makes "avares://" work from C#.
                bitmap = new Bitmap(AssetLoader.Open(new Uri(uri)));
            }
            catch (Exception)
            {
                // A converter that throws makes the whole binding fail silently, which is how a
                // missing or renamed asset turns into "no icon" with no clue why. Returning null
                // leaves the other icon binding in the template free to take over.
                return null;
            }

            Cache[uri] = bitmap;

            return bitmap;
        }
    }

    /// <summary>
    /// Picks 16 or 32 from whatever the binding passed. An unset or unrecognised parameter
    /// yields 16, the tab-strip size and by far the most common case.
    /// </summary>
    private static int ChooseSize(object? parameter)
    {
        var text = parameter?.ToString();

        if (
            double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var pixels)
            && pixels > 20
        )
        {
            return 32;
        }

        return 16;
    }

    public object? ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) => throw new NotSupportedException();
}