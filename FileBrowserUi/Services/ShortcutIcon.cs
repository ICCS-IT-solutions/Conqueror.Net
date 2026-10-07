using System;
using System.Runtime.InteropServices;

namespace Conqueror.Net.FileBrowserUi.Services;

/// <summary>
/// Extracts the icon a shortcut points at — the artwork Explorer draws underneath the
/// shortcut arrow — as raw 32-bit BGRA pixels.
/// </summary>
/// <remarks>
/// <para>
/// The shell is asked about the <c>.lnk</c> itself (<c>SHGetFileInfo</c>), so resolving the
/// link and locating its target's icon stays Windows' job. Callers lay
/// <c>xp-shortcutarrow</c> over the result; when this reports failure — broken link, shell
/// unavailable, conversion refused — they fall back to the generic XP program artwork, so
/// failure is a designed-for case rather than an error.
/// </para>
/// <para>
/// The GDI half is hand-rolled because the project targets plain <c>net8.0</c> and carries no
/// drawing package; this is the only place one would be needed. Rows come back tightly packed
/// top-down and premultiplied, matching <c>AlphaFormat.Premul</c> on Avalonia's writeable
/// bitmap.
/// </para>
/// </remarks>
public static class ShortcutIcon
{
    private const uint ShgfiIcon = 0x00000100;
    private const uint ShgfiLargeIcon = 0x00000000;
    private const uint ShgfiSmallIcon = 0x00000001;

    /// <summary>
    /// Extracts the icon at roughly <paramref name="maxSize"/> pixels (16 or less asks the
    /// shell's small icon, anything more the 32 px one) as <paramref name="width"/> ×
    /// <paramref name="height"/> premultiplied BGRA rows.
    /// </summary>
    /// <returns>False when there is no icon to show; the caller uses the fallback artwork.</returns>
    public static bool TryExtract(
        string path,
        int maxSize,
        out int width,
        out int height,
        out byte[] bgra)
    {
        width = 0;
        height = 0;
        bgra = Array.Empty<byte>();

        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(path))
        {
            return false;
        }

        var shellFile = default(ShellFileInfo);

        try
        {
            var flags = ShgfiIcon | (maxSize > 16 ? ShgfiLargeIcon : ShgfiSmallIcon);

            if (
                SHGetFileInfo(
                    path,
                    0,
                    ref shellFile,
                    (uint)Marshal.SizeOf<ShellFileInfo>(),
                    flags
                ) == IntPtr.Zero
                || shellFile.hIcon == IntPtr.Zero
            )
            {
                return false;
            }

            return TryConvert(shellFile.hIcon, out width, out height, out bgra);
        }
        catch (Exception ex)
            when (
                ex is DllNotFoundException
                    or EntryPointNotFoundException
                    or MarshalDirectiveException
                    or ExternalException
                    or ArgumentException
                    or NotSupportedException
            )
        {
            // No shell32, a blocked API, or a path the shell refuses — all of which just
            // mean "generic fallback art".
            return false;
        }
        finally
        {
            if (shellFile.hIcon != IntPtr.Zero)
            {
                DestroyIcon(shellFile.hIcon);
            }
        }
    }

    private static bool TryConvert(IntPtr hIcon, out int width, out int height, out byte[] bgra)
    {
        width = 0;
        height = 0;
        bgra = Array.Empty<byte>();

        if (!GetIconInfo(hIcon, out var iconInfo))
        {
            return false;
        }

        try
        {
            // GetIconInfo hands back copies of the icon's bitmaps; the caller owns them.
            if (iconInfo.hbmColor != IntPtr.Zero)
            {
                return TryConvertColor(
                    iconInfo.hbmColor,
                    iconInfo.hbmMask,
                    out width,
                    out height,
                    out bgra);
            }

            return iconInfo.hbmMask != IntPtr.Zero
                && TryConvertMonochrome(iconInfo.hbmMask, out width, out height, out bgra);
        }
        finally
        {
            if (iconInfo.hbmColor != IntPtr.Zero)
            {
                DeleteObject(iconInfo.hbmColor);
            }

            if (iconInfo.hbmMask != IntPtr.Zero)
            {
                DeleteObject(iconInfo.hbmMask);
            }
        }
    }

    private static bool TryConvertColor(
        IntPtr hColor,
        IntPtr hMask,
        out int width,
        out int height,
        out byte[] bgra)
    {
        width = 0;
        height = 0;
        bgra = Array.Empty<byte>();

        if (!TryReadBits(hColor, 32, out var pixels, out var w, out var h))
        {
            return false;
        }

        var minAlpha = byte.MaxValue;
        var maxAlpha = (byte)0;

        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] < minAlpha)
            {
                minAlpha = pixels[i];
            }

            if (pixels[i] > maxAlpha)
            {
                maxAlpha = pixels[i];
            }
        }

        if (minAlpha == maxAlpha && maxAlpha == 0)
        {
            // Classic icon: the colour bitmap carries no alpha at all and the AND mask has
            // the shape. A mask that will not read as 1-bit means "no icon", which is fine.
            if (!TryApplyMask(pixels, w, h, hMask))
            {
                return false;
            }
        }
        else if (minAlpha == 255)
        {
            // Opaque everywhere: either a square image, where the mask is all-visible and
            // applying it changes nothing, or a converted classic icon, where the mask holds
            // the real shape. Best effort either way — the colour alone still reads.
            TryApplyMask(pixels, w, h, hMask);
        }
        else
        {
            FinishAlphaChannel(pixels);
        }

        width = w;
        height = h;
        bgra = pixels;
        return true;
    }

    private static bool TryConvertMonochrome(
        IntPtr hMask,
        out int width,
        out int height,
        out byte[] bgra)
    {
        width = 0;
        height = 0;
        bgra = Array.Empty<byte>();

        // Without a colour bitmap the mask holds both planes: XOR image above AND shape.
        if (
            !TryReadBits(hMask, 1, out var bits, out var w, out var totalHeight)
            || totalHeight % 2 != 0
        )
        {
            return false;
        }

        var h = totalHeight / 2;
        var stride = ((w + 31) / 32) * 4;
        var pixels = new byte[w * h * 4];

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (IsMaskSet(bits, stride, x, y + h))
                {
                    continue; // transparent; the alpha here is already zero
                }

                var p = (y * w + x) * 4;
                var value = IsMaskSet(bits, stride, x, y) ? byte.MaxValue : (byte)0;
                pixels[p] = pixels[p + 1] = pixels[p + 2] = value;
                pixels[p + 3] = byte.MaxValue;
            }
        }

        width = w;
        height = h;
        bgra = pixels;
        return true;
    }

    /// <summary>
    /// Normalises a colour bitmap's own alpha channel: fixes up straight-alpha sources, then
    /// clears colour under fully transparent pixels so a premultiplied consumer never sees
    /// colour where there is none.
    /// </summary>
    private static void FinishAlphaChannel(byte[] bgra)
    {
        var straight = false;

        for (var i = 0; i < bgra.Length; i += 4)
        {
            var a = bgra[i + 3];

            if (a is 0 or 255)
            {
                continue;
            }

            // Premultiplied data can never carry a channel above its own alpha.
            if (bgra[i] > a || bgra[i + 1] > a || bgra[i + 2] > a)
            {
                straight = true;
                break;
            }
        }

        if (straight)
        {
            for (var i = 0; i < bgra.Length; i += 4)
            {
                var a = bgra[i + 3];
                bgra[i] = (byte)(bgra[i] * a / 255);
                bgra[i + 1] = (byte)(bgra[i + 1] * a / 255);
                bgra[i + 2] = (byte)(bgra[i + 2] * a / 255);
            }
        }

        for (var i = 0; i < bgra.Length; i += 4)
        {
            if (bgra[i + 3] == 0)
            {
                bgra[i] = bgra[i + 1] = bgra[i + 2] = 0;
            }
        }
    }

    private static bool TryApplyMask(byte[] bgra, int width, int height, IntPtr hMask)
    {
        if (
            !TryReadBits(hMask, 1, out var bits, out var w, out var h)
            || w != width
            || h != height
        )
        {
            return false;
        }

        var stride = ((width + 31) / 32) * 4;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var p = (y * width + x) * 4;

                if (IsMaskSet(bits, stride, x, y))
                {
                    bgra[p] = bgra[p + 1] = bgra[p + 2] = bgra[p + 3] = 0;
                }
                else
                {
                    bgra[p + 3] = byte.MaxValue;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Reads a bitmap as top-down rows at <paramref name="bitCount"/> into a tightly packed
    /// buffer, discovering its dimensions from the bitmap itself.
    /// </summary>
    /// <remarks>
    /// Dimensions come from GDI's own header query — a <c>GetDIBits</c> call with no pixel
    /// buffer — rather than from <c>GetObject</c>: on x64 <c>GetObject</c> insists on a
    /// 32-byte structure, which is not the layout of the published <c>BITMAP</c> and silently
    /// fails at the documented size. The second call converts to the caller's depth where GDI
    /// can, and reports failure where it cannot — the caller then falls back to the generic
    /// artwork.
    /// </remarks>
    private static bool TryReadBits(
        IntPtr hBitmap,
        int bitCount,
        out byte[] bits,
        out int width,
        out int height)
    {
        bits = Array.Empty<byte>();
        width = 0;
        height = 0;

        if (hBitmap == IntPtr.Zero)
        {
            return false;
        }

        var hdc = CreateCompatibleDC(IntPtr.Zero);

        if (hdc == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var header = new BitMapInfoHeader { biSize = 40 };

            if (
                GetDIBits(hdc, hBitmap, 0, 0, null, ref header, 0) == 0
                || header.biWidth <= 0
                || header.biHeight == 0
            )
            {
                return false;
            }

            width = header.biWidth;
            height = Math.Abs(header.biHeight);

            // Negative height: top-down rows, which is what the callers index as.
            header.biHeight = -height;
            header.biPlanes = 1;
            header.biBitCount = (ushort)bitCount;
            header.biCompression = 0; // BI_RGB, so 32 bpp reads as BGRA
            header.biSizeImage = 0;
            header.biClrUsed = 0;
            header.biClrImportant = 0;

            var stride = ((width * bitCount + 31) / 32) * 4;
            var buffer = new byte[stride * height];

            if (GetDIBits(hdc, hBitmap, 0, (uint)height, buffer, ref header, 0) == 0)
            {
                return false;
            }

            bits = buffer;
            return true;
        }
        finally
        {
            DeleteDC(hdc);
        }
    }

    /// <summary>Reads one bit of a 1 bpp AND mask, MSB first along each row.</summary>
    private static bool IsMaskSet(byte[] bits, int stride, int x, int y) =>
        (bits[y * stride + (x >> 3)] & (0x80 >> (x & 7))) != 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool fIcon;

        public uint xHotspot;
        public uint yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitMapInfoHeader
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref ShellFileInfo psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(IntPtr hIcon, out IconInfo piconinfo);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(
        IntPtr hdc,
        IntPtr hbm,
        uint uStartScan,
        uint cScanLines,
        byte[]? lpvBits,
        ref BitMapInfoHeader lpbmi,
        uint uUsage);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);
}
