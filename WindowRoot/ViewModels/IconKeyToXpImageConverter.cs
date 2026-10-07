using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Conqueror.Net.FileBrowserUi.Services;

namespace Conqueror.Net.WindowRoot.ViewModels;

/// <summary>
/// Resolves an XP icon key — tab chrome and file-list artwork — to a decoded bitmap, e.g.
/// "Icon.Xp.Explorer" to the 16 px Explorer folder-with-magnifier from <c>Assets/Icons/xp</c>,
/// or a shortcut key to a composed base-plus-arrow image.
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

        // File-list artwork. The shortcut keys are absent on purpose: a .lnk goes through
        // ComposeShortcut, which layers xp-shortcutarrow over the target's own extracted icon
        // (program links) or over xp-genericdocument, instead of loading whole pre-composed art.
        ["Icon.Xp.BatFile"] = "xp-batfile",
        ["Icon.Xp.Program"] = "xp-program",

        // The editor tab. xp-configfile is the XP notepad-with-gear artwork from the pack,
        // which reads as "text/config file" the way a plain document glyph would not.
        ["Icon.Xp.ConfigFile"] = "xp-configfile",
    };

    /// <summary>
    /// Decoded icons, keyed by avares URI for single-asset keys and by key+path+size for
    /// composed shortcut art. Every row and tab binds through here, so without this the same
    /// image would be re-decoded once per redraw — and the shell extraction behind a program
    /// shortcut would be repeated with it.
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
        if (value is not string key || key.Length == 0)
        {
            return null;
        }

        var size = ChooseSize(parameter);

        // Shortcut keys may carry the link's full path after '@' (see FileSystemEntry), which
        // is what lets a program shortcut extract its target's own icon. Known shortcut keys
        // are composed below; everything else is a plain stem lookup in Files.
        var at = key.IndexOf('@');
        var template = at > 0 ? key[..at] : key;

        if (template is "Icon.Xp.ProgramShortcut" or "Icon.Xp.FileShortcut")
        {
            return ComposeShortcut(template, at > 0 ? key[(at + 1)..] : null, size);
        }

        if (at > 0 || !Files.TryGetValue(template, out var stem))
        {
            return null;
        }

        var uri = $"{Prefix}{stem}{size}.png";

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
    /// Builds a shortcut icon: <c>xp-shortcutarrow</c> over a base image. A program shortcut
    /// gets its target's own icon when the shell can supply one — what Explorer draws — and
    /// falls back to the XP program artwork when it cannot; every other shortcut gets the XP
    /// generic document, so an unreadable link still reads as a shortcut rather than losing
    /// the overlay.
    /// </summary>
    private static Bitmap? ComposeShortcut(string template, string? linkPath, int size)
    {
        var cacheKey = $"{template}|{linkPath}|{size}";

        lock (Cache)
        {
            if (Cache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }
        }

        Bitmap? composed = null;
        var isProgram = template is "Icon.Xp.ProgramShortcut";

        if (
            isProgram
            && linkPath is not null
            && ShortcutIcon.TryExtract(
                linkPath,
                size,
                out var width,
                out var height,
                out var pixels
            )
        )
        {
            using var own = ToWriteableBitmap(width, height, pixels);
            composed = Compose(size, own, LoadPng("xp-shortcutarrow", size));
        }

        // The fallback for program links, and the whole path for document shortcuts. A missing
        // asset makes Compose return null, which hands the row back to the SVG icon underneath.
        composed ??= Compose(
            size,
            LoadPng(isProgram ? "xp-program" : "xp-genericdocument", size),
            LoadPng("xp-shortcutarrow", size)
        );

        if (composed is not null)
        {
            lock (Cache)
            {
                Cache[cacheKey] = composed;
            }
        }

        return composed;
    }

    /// <summary>Loads and caches one PNG from <c>Assets/Icons/xp</c>, or null if it is missing.</summary>
    private static Bitmap? LoadPng(string stem, int size)
    {
        var uri = $"{Prefix}{stem}{size}.png";

        lock (Cache)
        {
            if (Cache.TryGetValue(uri, out var cached))
            {
                return cached;
            }

            try
            {
                var bitmap = new Bitmap(AssetLoader.Open(new Uri(uri)));
                Cache[uri] = bitmap;
                return bitmap;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>Draws the layers bottom-first onto a size×size canvas.</summary>
    private static Bitmap? Compose(int size, params Bitmap?[] layers)
    {
        foreach (var layer in layers)
        {
            if (layer is null)
            {
                return null;
            }
        }

        var target = new RenderTargetBitmap(new PixelSize(size, size));

        try
        {
            using (var context = target.CreateDrawingContext())
            {
                var destination = new Rect(0, 0, size, size);

                foreach (var layer in layers)
                {
                    context.DrawImage(layer!, destination);
                }
            }

            return target;
        }
        catch (Exception)
        {
            // Same contract as a failed asset load: null, never a thrown binding.
            target.Dispose();
            return null;
        }
    }

    /// <summary>Wraps extracted pixels in a writeable bitmap the drawing context can blit.</summary>
    private static WriteableBitmap ToWriteableBitmap(int width, int height, byte[] bgra)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul
        );

        using var framebuffer = bitmap.Lock();

        if (framebuffer.RowBytes == width * 4)
        {
            Marshal.Copy(bgra, 0, framebuffer.Address, bgra.Length);
        }
        else
        {
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(
                    bgra,
                    y * width * 4,
                    IntPtr.Add(framebuffer.Address, y * framebuffer.RowBytes),
                    width * 4
                );
            }
        }

        return bitmap;
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