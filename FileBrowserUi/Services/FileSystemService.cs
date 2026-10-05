using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Conqueror.Net.FileBrowserUi.Models;

namespace Conqueror.Net.FileBrowserUi.Services;

/// <inheritdoc cref="IFileSystemService"/>
public sealed class FileSystemService : IFileSystemService
{
    public DirectoryListing ListDirectory(string path)
    {
        try
        {
            var directory = new DirectoryInfo(path);
            if (!directory.Exists)
            {
                return new DirectoryListing([], $"Cannot find '{path}'.");
            }

            // EnumerateFileSystemInfos touches every entry, so a single unreadable child
            // (a denied junction, say) should not sink the whole listing: take what we can.
            var entries = new List<FileSystemEntry>();

            foreach (var info in directory.EnumerateFileSystemInfos())
            {
                entries.Add(new FileSystemEntry(info));
            }

            return new DirectoryListing(entries, null);
        }
        catch (UnauthorizedAccessException)
        {
            return new DirectoryListing([], $"You do not have permission to access '{path}'.");
        }
        catch (IOException ex)
        {
            return new DirectoryListing([], ex.Message);
        }
    }

    public bool IsDirectory(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    public bool Exists(string path)
    {
        try
        {
            return File.Exists(path) || Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    public string? GetParent(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var directory = Directory.GetParent(path);
            return directory?.FullName;
        }
        catch
        {
            // Malformed paths and inaccessible parents both land here.
            return null;
        }
    }

    public string? ResolvePath(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var text = input.Trim().Trim('"');

        // Strip a file:// wrapper so shell paths pasted from Explorer work.
        if (text.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
        {
            text = Uri.UnescapeDataString(text["file:///".Length..])
                .Replace('/', Path.DirectorySeparatorChar);
        }

        text = Environment.ExpandEnvironmentVariables(text);

        try
        {
            if (Path.IsPathRooted(text))
            {
                var full = Path.GetFullPath(text);
                return Exists(full) ? full : null;
            }

            // A bare name: look it up against the shell folders and the working directory.
            foreach (var folder in GetShellFolders())
            {
                var candidate = Path.Combine(folder.Path, text);
                if (Exists(candidate))
                {
                    return candidate;
                }
            }

            var relative = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), text));
            return Exists(relative) ? relative : null;
        }
        catch (Exception ex)
            when (ex
                    is ArgumentException
                        or NotSupportedException
                        or PathTooLongException
                        or IOException
            )
        {
            return null;
        }
    }

    public IReadOnlyList<ShellFolder> GetShellFolders()
    {
        var folders = new List<ShellFolder>();

        void AddSpecial(Environment.SpecialFolder kind, string label)
        {
            try
            {
                var path = Environment.GetFolderPath(kind);
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                {
                    folders.Add(new ShellFolder(label, path, ShellFolderKind.Folder));
                }
            }
            catch
            {
                // Some special folders are unavailable under a restricted profile.
            }
        }

        AddSpecial(Environment.SpecialFolder.Desktop, "Desktop");
        AddSpecial(Environment.SpecialFolder.MyDocuments, "My Documents");
        AddSpecial(Environment.SpecialFolder.MyPictures, "My Pictures");
        AddSpecial(Environment.SpecialFolder.MyMusic, "My Music");
        AddSpecial(Environment.SpecialFolder.MyVideos, "My Videos");
        AddSpecial(Environment.SpecialFolder.DesktopDirectory, "Desktop");
        AddSpecial(Environment.SpecialFolder.UserProfile, "My Computer");

        foreach (var drive in DriveInfo.GetDrives())
        {
            // A mapped network drive that is not currently reachable (here: Z:\) throws
            // as soon as any property beyond Name is touched, so each read is guarded.
            var label = drive.Name;

            try
            {
                if (drive.IsReady && !string.IsNullOrEmpty(drive.VolumeLabel))
                {
                    label = $"{drive.VolumeLabel} ({drive.Name})";
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Keep the bare drive letter for unreachable volumes.
            }

            folders.Add(new ShellFolder(label, drive.Name, ShellFolderKind.Drive));
        }

        return folders;
    }

    public IReadOnlyList<string> ListChildDirectories(string path)
    {
        try
        {
            var directory = new DirectoryInfo(path);
            if (!directory.Exists)
            {
                return [];
            }

            return directory
                .EnumerateDirectories()
                .Select(d => d.FullName)
                .ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException)
        {
            // A denied junction or a vanished folder leaves the node simply childless.
            return [];
        }
    }

    public void Search(
        string root,
        string term,
        IProgress<FileSystemEntry> progress,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(term) || !IsDirectory(root))
        {
            return;
        }

        var needle = term.Trim();

        // The loop is explicit rather than EnumerationOptions.RecurseSubdirectories because
        // that option swallows nothing useful here and still rethrows on the first denied
        // directory; taking control of the walk is what lets a single bad branch be skipped.
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var current = pending.Pop();

            string[] children;
            try
            {
                children = Directory.GetFileSystemEntries(current);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException)
            {
                continue;
            }

            foreach (var child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();

                FileSystemInfo info;
                var isDirectory = false;

                try
                {
                    var attributes = File.GetAttributes(child);
                    isDirectory = attributes.HasFlag(FileAttributes.Directory);
                    info = isDirectory ? new DirectoryInfo(child) : new FileInfo(child);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException)
                {
                    continue;
                }

                if (isDirectory)
                {
                    // Queue for descent before testing the name, so a folder is both a
                    // result and a branch - which is what "All files and folders" means.
                    pending.Push(child);
                }

                if (
                    !info.Name.Contains(needle, StringComparison.CurrentCultureIgnoreCase)
                    && !info.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                )
                {
                    continue;
                }

                progress.Report(new FileSystemEntry(info));
            }
        }
    }

    public (bool Success, string? Error) CreateDirectory(string parent, string name)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(parent, name));
            return (true, null);
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (false, ex.Message);
        }
    }

    public (bool Success, string? Error) CreateFile(string parent, string name)
    {
        try
        {
            var full = Path.Combine(parent, name);
            if (File.Exists(full) || Directory.Exists(full))
            {
                return (false, $"'{name}' already exists.");
            }

            using (File.Create(full))
            {
            }

            return (true, null);
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (false, ex.Message);
        }
    }

    public (bool Success, string? NewPath, string? Error) Rename(string path, string newName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(newName)
                || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return (false, null, $"'{newName}' is not a valid name.");
            }

            var parent = Path.GetDirectoryName(Path.GetFullPath(path));
            if (parent is null)
            {
                return (false, null, "Cannot rename a drive root.");
            }

            var dest = Path.Combine(parent, newName.Trim());
            if (File.Exists(dest) || Directory.Exists(dest))
            {
                return (false, null, $"'{newName.Trim()}' already exists.");
            }

            if (Directory.Exists(path))
            {
                Directory.Move(path, dest);
            }
            else if (File.Exists(path))
            {
                File.Move(path, dest);
            }
            else
            {
                return (false, null, $"Cannot find '{path}'.");
            }

            return (true, dest, null);
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (false, null, ex.Message);
        }
    }

    public (bool Success, string? Error) Delete(IEnumerable<string> paths)
    {
        var list = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (list.Count == 0)
        {
            return (false, "Nothing selected.");
        }

        try
        {
            foreach (var path in list)
            {
                if (Directory.Exists(path))
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                        path,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
                else if (File.Exists(path))
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                        path,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
            }

            return (true, null);
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (false, ex.Message);
        }
    }

    public (bool Success, string? Error) Copy(IEnumerable<string> paths, string destinationDirectory)
    {
        try
        {
            Directory.CreateDirectory(destinationDirectory);

            foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                if (Directory.Exists(path))
                {
                    CopyDirectory(path, Path.Combine(destinationDirectory, Path.GetFileName(path)));
                }
                else if (File.Exists(path))
                {
                    var dest = Path.Combine(destinationDirectory, Path.GetFileName(path));
                    File.Copy(path, dest, overwrite: true);
                }
            }

            return (true, null);
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (false, ex.Message);
        }
    }

    public (bool Success, string? Error) Move(IEnumerable<string> paths, string destinationDirectory)
    {
        try
        {
            Directory.CreateDirectory(destinationDirectory);

            foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                var dest = Path.Combine(destinationDirectory, Path.GetFileName(path));

                if (Directory.Exists(path))
                {
                    try
                    {
                        Directory.Move(path, dest);
                    }
                    catch (IOException)
                    {
                        CopyDirectory(path, dest);
                        Directory.Delete(path, recursive: true);
                    }
                }
                else if (File.Exists(path))
                {
                    File.Move(path, dest, overwrite: true);
                }
            }

            return (true, null);
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (false, ex.Message);
        }
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);

        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
        }
    }

    public string GetVolumeLabel(string pathOrDrive)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(pathOrDrive));
            if (root is null)
            {
                return string.Empty;
            }

            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.VolumeLabel : string.Empty;
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return string.Empty;
        }
    }

    public (long Total, long Free) GetDriveInfo(string pathOrDrive)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(pathOrDrive));
            if (root is null)
            {
                return (0, 0);
            }

            var drive = new DriveInfo(root);
            return (drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (0, 0);
        }
    }
}
