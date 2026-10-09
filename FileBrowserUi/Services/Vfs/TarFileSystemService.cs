using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;

namespace Conqueror.Net.FileBrowserUi.Services.Vfs;

/// <summary>
/// A tar archive (plain, or gzip-compressed as .tar.gz/.tgz) presented as a browsable folder
/// tree.
/// </summary>
internal sealed class TarFileSystemService : ArchiveFileSystemService
{
    public TarFileSystemService(string archivePath)
        : base(archivePath) { }

    protected override string Scheme => "tar";

    protected override IReadOnlyList<ArchiveEntry> ReadEntries()
    {
        var result = new List<ArchiveEntry>();

        using var fileStream = File.OpenRead(ArchivePath);
        using var stream = MaybeDecompress(fileStream);

        using var reader = new TarReader(stream);
        while (reader.GetNextEntry(copyData: false) is { } entry)
        {
            var normalized = NormalizeTarPath(entry.Name);
            if (normalized.Length == 0)
            {
                continue;
            }

            var isDirectory = entry.EntryType is TarEntryType.Directory;
            var modified = entry.ModificationTime.LocalDateTime;

            result.Add(isDirectory
                ? new ArchiveEntry(normalized, true, 0, modified)
                : new ArchiveEntry(normalized, false, entry.Length, modified));
        }

        return result;
    }

    protected override Stream? OpenEntry(string innerPath)
    {
        var target = NormalizeTarPath(innerPath);

        using var fileStream = File.OpenRead(ArchivePath);
        using var stream = MaybeDecompress(fileStream);

        using var reader = new TarReader(stream);
        while (reader.GetNextEntry(copyData: true) is { } entry)
        {
            if (!NormalizeTarPath(entry.Name).Equals(target, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (entry.DataStream is null)
            {
                return null;
            }

            // Copy out before the reader/file are disposed on return.
            var buffer = new MemoryStream();
            entry.DataStream.CopyTo(buffer);
            buffer.Position = 0;
            return buffer;
        }

        return null;
    }

    /// <summary>Wraps the file stream in a gzip decoder when the archive name says it is compressed.</summary>
    private Stream MaybeDecompress(Stream fileStream)
    {
        var name = ArchivePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)
            || ArchivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase);

        return name ? new GZipStream(fileStream, CompressionMode.Decompress) : fileStream;
    }

    private static string NormalizeTarPath(string name) =>
        name.Replace('\\', '/').Trim().Trim('/');
}
