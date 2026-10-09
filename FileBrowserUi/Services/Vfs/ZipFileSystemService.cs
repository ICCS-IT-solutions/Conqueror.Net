using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Conqueror.Net.FileBrowserUi.Services.Vfs;

/// <summary>A zip archive presented as a browsable folder tree.</summary>
internal sealed class ZipFileSystemService : ArchiveFileSystemService
{
    public ZipFileSystemService(string archivePath)
        : base(archivePath) { }

    protected override string Scheme => "zip";

    protected override IReadOnlyList<ArchiveEntry> ReadEntries()
    {
        var result = new List<ArchiveEntry>();

        // Read the whole archive once. ZipArchive exposes every entry up front, so a directory
        // listing is a scan over this list rather than a seek per child.
        using var archive = ZipFile.OpenRead(ArchivePath);

        foreach (var entry in archive.Entries)
        {
            // A zip directory entry has an empty Name and a FullName ending in '/'. Folders are
            // implied by Children() from file paths anyway, so an explicit entry only adds its
            // timestamp; keep it, but never surface a nameless root.
            var normalized = NormalizeZipPath(entry.FullName);
            if (normalized.Length == 0)
            {
                continue;
            }

            var isDirectory = entry.FullName.EndsWith('/') || (entry.Length == 0 && entry.Name.Length == 0);
            if (isDirectory)
            {
                result.Add(new ArchiveEntry(normalized, true, 0, entry.LastWriteTime.LocalDateTime));
            }
            else
            {
                result.Add(new ArchiveEntry(
                    normalized, false, entry.Length, entry.LastWriteTime.LocalDateTime));
            }
        }

        return result;
    }

    protected override Stream? OpenEntry(string innerPath)
    {
        using var archive = ZipFile.OpenRead(ArchivePath);
        var target = NormalizeZipPath(innerPath);
        var entry = archive.GetEntry(target);
        if (entry is null)
        {
            return null;
        }

        // Copy into memory: the archive handle is disposed when this method returns, so the
        // returned stream must not reference it.
        var buffer = new MemoryStream();
        using (var source = entry.Open())
        {
            source.CopyTo(buffer);
        }

        buffer.Position = 0;
        return buffer;
    }

    private static string NormalizeZipPath(string fullName) =>
        fullName.Replace('\\', '/').Trim().Trim('/');
}
