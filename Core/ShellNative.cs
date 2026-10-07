using System.Runtime.InteropServices;

namespace Conqueror.Net.Core;

/// <summary>The few shell entry points the UI needs that the BCL does not wrap.</summary>
internal static class ShellNative
{
    private const uint ShopFilePath = 0;

    /// <summary>
    /// Shows the shell's property sheet for a file or folder path — the same dialog
    /// Explorer's Properties menu opens. Unlike ShellExecute's "properties" verb this does
    /// not resolve the verb through the file type's registry association first, so folders,
    /// extensionless files and unassociated types (where the verb fails with
    /// ERROR_ASSOCIATION, "No application is associated with the specified file") all work.
    /// </summary>
    /// <returns>False when the shell declined; the caller falls back to the verb.</returns>
    internal static bool ShowFileProperties(string path)
    {
        try
        {
            return SHObjectProperties(IntPtr.Zero, ShopFilePath, path, null);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            App.Log?.Invoke($"SHObjectProperties unavailable: {ex.Message}");
            return false;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHObjectProperties(
        IntPtr hwnd, uint dwType, string pszObject, string? pszPage);
}