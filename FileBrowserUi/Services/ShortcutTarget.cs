using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Conqueror.Net.FileBrowserUi.Services;

/// <summary>
/// Resolves the target of a Windows shell link (a <c>.lnk</c>), which is what decides whether a
/// shortcut is drawn with the program artwork or the plain document arrow.
/// </summary>
/// <remarks>
/// <para>
/// The target lives in the shell's own property store rather than in anything readable from the
/// file as plain text, so this asks the shell through <c>WScript.Shell</c>. That is a Windows COM
/// object; on any other platform <see cref="TryGetTarget"/> reports failure and the caller falls
/// back to the generic icon.
/// </para>
/// <para>
/// Resolution costs a COM round trip and a file read, so callers should only ask about
/// <c>.lnk</c> files, and should cache the answer per entry rather than calling this per redraw.
/// </para>
/// </remarks>
public static class ShortcutTarget
{
    /// <summary>
    /// Extensions of a shortcut's <em>target</em> that make it a program shortcut.
    /// </summary>
    /// <remarks>
    /// <c>.exe</c> is the common case. <c>.bat</c> and <c>.com</c> count because a link to a
    /// legacy command-line program usually points at a batch file, and XP groups that with
    /// programs rather than with documents.
    /// </remarks>
    public static readonly HashSet<string> ProgramExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".exe", ".bat", ".com" };

    /// <summary>
    /// Reads the target path of the shell link at <paramref name="linkPath"/>, returning false
    /// when the link cannot be read or the shell is unavailable.
    /// </summary>
    public static bool TryGetTarget(string linkPath, out string target)
    {
        target = string.Empty;

        // WScript.Shell is a Windows COM object; touching it elsewhere throws rather than
        // returning null, so the platform guard has to come first.
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        object? shell = null;
        object? link = null;

        try
        {
            if (Type.GetTypeFromProgID("WScript.Shell") is not { } shellType)
            {
                return false;
            }

            if (Activator.CreateInstance(shellType) is not { } createdShell)
            {
                return false;
            }

            shell = createdShell;

            // Both hops go through InvokeMember because these are bare System.__ComObject
            // instances: they expose IDispatch only, so a normal CLR call site sees no members.
            link = shellType.InvokeMember(
                "CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args: [linkPath]
            );

            if (link is null)
            {
                return false;
            }

            if (
                link.GetType().InvokeMember(
                    "TargetPath",
                    System.Reflection.BindingFlags.GetProperty,
                    binder: null,
                    target: link,
                    args: null
                ) is not string path
                || path.Length == 0
            )
            {
                return false;
            }

            // A relative target is legal in a .lnk. Resolve it against the link's own folder so
            // the extension test below is looking at a real filename.
            target = Path.IsPathRooted(path)
                ? path
                : Path.GetFullPath(path, Path.GetDirectoryName(linkPath) ?? string.Empty);

            return true;
        }
        catch (Exception ex)
            when (
                ex is IOException
                    or UnauthorizedAccessException
                    or COMException
                    or NotSupportedException
                    or ArgumentException
            )
        {
            // Broken link, blocked COM, or a target that no longer resolves. All mean "no icon".
            return false;
        }
        finally
        {
            // COM references left unreleased stay alive for the life of the process, and a
            // folder full of shortcuts is enough to make that noticeable.
            ReleaseComObject(link);
            ReleaseComObject(shell);
        }
    }

    /// <summary>
    /// Whether the link at <paramref name="linkPath"/> points at a program rather than at a
    /// document or a folder.
    /// </summary>
    public static bool IsProgramShortcut(string linkPath) =>
        TryGetTarget(linkPath, out var target)
        && ProgramExtensions.Contains(Path.GetExtension(target));

    private static void ReleaseComObject(object? instance)
    {
        // Both this and IsComObject are Windows-only APIs, and the finally block runs on every
        // exit path, so the guard cannot rely on the earlier platform check having returned.
        if (OperatingSystem.IsWindows() && instance is not null && Marshal.IsComObject(instance))
        {
            Marshal.FinalReleaseComObject(instance);
        }
    }
}