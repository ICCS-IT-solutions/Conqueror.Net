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
/// Icons exist at three resolutions in <c>Assets/Icons/xp/{16|32|256}/</c>. The 16 and 32 px
/// PNGs are pre-rendered by <c>tools/import-xp-from-psd.ps1</c> for the tab strip, toolbar and
/// small-icon file-list views where pixel-perfect hard-scaled art matters. Any other requested
/// size loads the 256 px base and downscales it at runtime through Avalonia's render target,
/// which lets the medium/large/extra-large file views share one set of source artwork.
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
        ["Icon.Xp.LibFile"] = "xp-libfile",
        ["Icon.Xp.Audio"] = "xp-audio",
        ["Icon.Xp.Video"] = "xp-video",

        // The editor tab. xp-configfile is the XP notepad-with-gear artwork from the pack,
        // which reads as "text/config file" the way a plain document glyph would not.
        ["Icon.Xp.ConfigFile"] = "xp-configfile",

        // Clipboard file-operation glyphs.
        ["Icon.Xp.Copy"] = "xp-copy",
        ["Icon.Xp.Cut"] = "xp-cut",
        ["Icon.Xp.Paste"] = "xp-paste",
        ["Icon.Xp.Delete"] = "xp-delete",
        ["Icon.Xp.Rename"] = "xp-rename",
        ["Icon.Xp.Refresh"] = "xp-refresh",

        // Checkbox states for select-all / invert-select toggles.
        ["Icon.Xp.CheckboxCheck"] = "xp-checkbox-check",
        ["Icon.Xp.CheckboxClear"] = "xp-checkbox-clear",
        ["Icon.Xp.CheckboxHalf"] = "xp-checkbox-half",
        ["Icon.Xp.CheckboxFilter"] = "xp-checkbox-filter",
        ["Icon.Xp.CheckboxShaded"] = "xp-checkbox-shaded",
        ["Icon.Xp.Checklist"] = "xp-checklist",
        ["Icon.Xp.InvertSelect"] = "xp-invertselect",
        ["Icon.Xp.SelectAll"] = "xp-selectall",
        ["Icon.Xp.SelectNone"] = "xp-selectnone",

        // Folder glyphs.
        ["Icon.Xp.ClosedFolder"] = "xp-closedfolder",
        ["Icon.Xp.OpenFolder"] = "xp-openfolder",
        ["Icon.Xp.NewFolder"] = "xp-newfolder",

        // Generic file / document glyphs.
        ["Icon.Xp.GenericDocument"] = "xp-genericdocument",
        ["Icon.Xp.NewFile"] = "xp-newfile",
        ["Icon.Xp.ProgramShortcut"] = "xp-programshortcut",

        // Window close control.
        ["Icon.Xp.Close"] = "xp-close",

        // Properties dialog.
        ["Icon.Xp.Properties"] = "xp-properties",
    };

    /// <summary>
    /// Chrome sizes with pre-rendered PNGs. Any other size is derived from the 256 px base
    /// at runtime so the file-browser views can request 48, 64, 96, 128 or 256 without
    /// shipping extra assets.
    /// </summary>
    private static readonly HashSet<int> ChromeSizes = new() { 16, 32 };

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

        var size = ResolveSize(parameter);

        // Shortcut keys may carry the link's full path after '@' (see FileSystemEntry), which
        // is what lets a program shortcut extract its target's own icon. Known shortcut keys
        // are composed below; everything else is a plain stem lookup in Files.
        var at = key.IndexOf('@');
        var template = at > 0 ? key[..at] : key;

        if (
            template is "Icon.Xp.ProgramShortcut" or "Icon.Xp.FileShortcut" or "Icon.Xp.BatFileShortcut"
        )
        {
            return ComposeShortcut(template, at > 0 ? key[(at + 1)..] : null, size);
        }

        if (at > 0 || !Files.TryGetValue(template, out var stem))
        {
            return null;
        }

        return LoadIcon(stem, size);
    }

    /// <summary>
    /// Composes a base icon with the overlay arrow that marks shortcuts. A program link
    /// (.lnk) may extract its target icon directly from the shell; otherwise the pack's
    /// generic document, bat-file, or program artwork is used as the base.
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
        var isBatShortcut = template is "Icon.Xp.BatFileShortcut";

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
            composed = Compose(size, own, LoadIcon("xp-shortcutarrow", size));
        }

        // The fallback for program links, the whole path for document shortcuts, and bat-file
        // shortcuts (which use the bat-file artwork rather than extracting a target icon).
        // A missing asset makes Compose return null, which hands the row back to the SVG icon
        // underneath.
        composed ??= Compose(
            size,
            isProgram
                ? LoadIcon("xp-program", size)
                : isBatShortcut
                    ? LoadIcon("xp-batfile", size)
                    : LoadIcon("xp-genericdocument", size),
            LoadIcon("xp-shortcutarrow", size)
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

    /// <summary>
    /// Loads and caches one PNG from <c>Assets/Icons/xp</c> at the requested pixel size,
    /// or <c>null</c> if it is missing.
    /// </summary>
    /// <remarks>
    /// The 16 and 32 px artwork is pre-rendered into size-specific folders; those paths are
    /// loaded directly. Any other size (e.g. the file list's medium/extra-large modes) loads
    /// the 256 px base and downscales through a render target so the full icon set ships in
    /// only three resolutions rather than a bespoke PNG per display size.
    /// </remarks>
    private static Bitmap? LoadIcon(string stem, int size)
    {
        if (ChromeSizes.Contains(size))
        {
            return LoadChrome(stem, size);
        }

        return Downscale(LoadBase(stem), size);
    }

    /// <summary>
    /// Loads a pre-rendered 16 or 32 px PNG from <c>Assets/Icons/xp/{size}/{stem}.png</c>.
    /// </summary>
    private static Bitmap? LoadChrome(string stem, int size)
    {
        var uri = $"{Prefix}{size}/{stem}.png";

        return Load(uri);
    }

    /// <summary>
    /// Loads the 256 px base asset from <c>Assets/Icons/xp/256/{stem}.png</c>.
    /// </summary>
    private static Bitmap? LoadBase(string stem)
    {
        var uri = $"{Prefix}256/{stem}.png";

        return Load(uri);
    }

    /// <summary>
    /// Shared decode: opens the avares URI through the asset loader and caches the result.
    /// Returns <c>null</c> on any load failure so a single bad icon never breaks a binding.
    /// </summary>
    private static Bitmap? Load(string uri)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(uri, out var cached))
            {
                return cached;
            }
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

        lock (Cache)
        {
            Cache[uri] = bitmap;
        }

        return bitmap;
    }

    /// <summary>
    /// Renders <paramref name="source"/> (expected 256 px) onto a new <paramref name="size"/>
    /// square canvas so file-browser views can request 48, 64, 96, 128, etc. without separate
    /// assets. The 256 base is cached separately, so repeated requests at the same size reuse a
    /// single downscaled bitmap.
    /// </summary>
    private static Bitmap? Downscale(Bitmap? source, int size)
    {
        if (source is null)
        {
            return null;
        }

        // Already the requested size: nothing to do.
        if (source.PixelSize.Width == size && source.PixelSize.Height == size)
        {
            return source;
        }

        var key = $"downscale:{source.PixelSize.Width}to{size}";

        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        var target = new RenderTargetBitmap(new PixelSize(size, size));

        try
        {
            using (var context = target.CreateDrawingContext())
            {
                context.DrawImage(source, new Rect(0, 0, size, size));
            }

            lock (Cache)
            {
                Cache[key] = target;
            }

            return target;
        }
        catch (Exception)
        {
            target.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Draws the layers bottom-first onto a size×size canvas.
    /// </summary>
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

    /// <summary>
    /// Wraps extracted pixels in a writeable bitmap the drawing context can blit.
    /// </summary>
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
    /// Resolves the pixel size from the binding parameter. The parameter is usually a string
    /// like "16", "32", "48", "96" or "256" coming from a DataTemplate's width/height or from
    /// the file browser's icon-size enumeration. A missing or unrecognised value defaults to
    /// 16, the tab-strip size and by far the most common case.
    /// </summary>
    /// <remarks>
    /// Non-chrome sizes (anything other than 16 or 32) are not pre-rendered; they are handled
    /// by <see cref="LoadIcon"/>, which loads the 256 px base and downscales at runtime.
    /// </remarks>
    private static int ResolveSize(object? parameter)
    {
        if (parameter is null)
        {
            return 16;
        }

        var text = parameter.ToString();

        if (
            double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var pixels)
            && pixels > 0
        )
        {
            return (int)Math.Round(pixels);
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
