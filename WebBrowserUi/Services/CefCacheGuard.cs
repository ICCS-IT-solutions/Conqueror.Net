using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Conqueror.Net.WebBrowserUi.Services;

/// <summary>
/// Validates the CEF cache directory for corruption before the engine initializes.
/// On corruption, quarantines the directory so CEF creates a fresh cache —
/// eliminating the "crash until cache is manually deleted" problem on Linux.
/// </summary>
internal static class CefCacheGuard
{
    // JSON files that must parse; half-written on crash -> CHECK abort.
    private static readonly string[] JsonFiles = { "Preferences", "Local State" };

    // Extensionless SQLite databases inside the cache root.
    private static readonly string[] SqliteDbNames =
    {
        "Cookies", "History", "Top Sites", "Favicons", "Web Data", "OS Crypto",
    };

    // Extensions that also indicate SQLite databases anywhere in the tree.
    private static readonly string[] SqliteExtensions = { ".db", ".sqlite", ".sqlite3" };

    // Stale lock sentinels left by killed CEF processes.
    private static readonly string[] LockFiles =
    {
        "SingletonSocket", "SingletonLock", "SingletonCookie", "lockfile", "cef.pid",
    };

    /// <summary>
    /// Ensures <paramref name="cachePath"/> is usable. If corrupted, moves it
    /// aside to *.quarantined-YYYYMMDD-HHMMSS and recreates an empty directory.
    /// Always returns the original path to use.
    /// </summary>
    public static string ValidateOrQuarantine(string cachePath)
    {
        if (!Directory.Exists(cachePath))
        {
            Directory.CreateDirectory(cachePath);
            return cachePath;
        }

        CleanStaleLocks(cachePath);

        if (DetectCorruption(cachePath))
        {
            Quarantine(cachePath);
            Directory.CreateDirectory(cachePath);
            App.Log?.Invoke(
                "[CefCacheGuard] Quarantined corrupt cache (CEF will create a fresh one)."
            );
        }

        PruneOldQuarantines(cachePath);

        return cachePath;
    }

    #region stale locks

    private static void CleanStaleLocks(string cachePath)
    {
        var currentPid = Environment.ProcessId;

        // Process-name match works for the compiled binary on Windows and Linux.
        // Under `dotnet run` the host is 'dotnet', so we conservatively assume
        // no other instance and clean. The singleton socket is OS-locked by fd,
        // so deleting the name file only affects dead processes.
        var otherAlive = false;
        try
        {
            otherAlive = System.Diagnostics.Process
                .GetProcessesByName("Conqueror.Net")
                .Any(p => p.Id != currentPid);
        }
        catch
        {
            /* If enumeration fails, we can't be sure — leave locks alone. */
            otherAlive = true;
        }

        if (otherAlive)
        {
            return;
        }

        foreach (var name in LockFiles)
        {
            TryDeleteFile(Path.Combine(cachePath, name));
        }

        // Stale lock files in cache subdirectories (GPUCache, Media Cache, etc.)
        try
        {
            foreach (var subdir in Directory.EnumerateDirectories(cachePath))
            {
                foreach (var name in LockFiles)
                {
                    TryDeleteFile(Path.Combine(subdir, name));
                }
            }
        }
        catch { /* best-effort */ }
    }

    #endregion
    
    #region corruption detection

    private static bool DetectCorruption(string cachePath)
    {
        // 1. JSON files that must parse.
        foreach (var jsonName in JsonFiles)
        {
            var path = Path.Combine(cachePath, jsonName);
            if (File.Exists(path) && IsJsonCorrupt(path))
                return true;
        }

        // 2. Default-profile JSON files (Secure Preferences, etc.)
        var defaultDir = Path.Combine(cachePath, "Default");
        if (Directory.Exists(defaultDir))
        {
            var securePrefs = Path.Combine(defaultDir, "Secure Preferences");
            if (File.Exists(securePrefs) && IsJsonCorrupt(securePrefs))
                return true;
        }

        // 3. Known extensionless SQLite databases.
        foreach (var dbName in SqliteDbNames)
        {
            var dbPath = Path.Combine(cachePath, dbName);
            if (File.Exists(dbPath) && IsSqliteCorrupt(dbPath))
                return true;
        }

        // 4. Any *.db / *.sqlite / *.sqlite3 in the tree.
        foreach (var ext in SqliteExtensions)
        {
            try
            {
                var pattern = "*" + ext;
                foreach (var db in Directory.EnumerateFiles(
                    cachePath, pattern, SearchOption.AllDirectories))
                {
                    if (IsSqliteCorrupt(db))
                        return true;
                }
            }
            catch { /* best-effort */ }
        }

        // 5. Recent crash artifacts (.dmp / .crash).
        try
        {
            var crashFiles = Directory
                .EnumerateFiles(cachePath, "*.dmp", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(
                    cachePath, "*.crash", SearchOption.AllDirectories))
                .ToArray();

            foreach (var crash in crashFiles)
            {
                try
                {
                    if ((DateTime.Now - File.GetLastWriteTime(crash)).TotalHours < 24)
                        return true;
                }
                catch { /* best-effort */ }
            }
        }
        catch { /* best-effort */ }

        // 6. Zero-byte Preferences (CEF rejects an empty prefs file).
        var prefsPath = Path.Combine(cachePath, "Preferences");
        if (File.Exists(prefsPath))
        {
            try
            {
                if (new FileInfo(prefsPath).Length == 0)
                    return true;
            }
            catch { /* best-effort */ }
        }

        return false;
    }

    private static bool IsJsonCorrupt(string filePath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(filePath));
            return false;
        }
        catch { return true; }
    }

    private static bool IsSqliteCorrupt(string dbPath)
    {
        try
        {
            var info = new FileInfo(dbPath);
            if (info.Length == 0)
                return true;

            Span<byte> header = stackalloc byte[16];
            using (var fs = File.OpenRead(dbPath))
            {
                fs.Read(header);
            }

            ReadOnlySpan<byte> expected =
                Encoding.ASCII.GetBytes("SQLite format 3\0");
            return !header.SequenceEqual(expected);
        }
        catch { return true; }
    }

    #endregion

    #region quarantine / cleanup

    private static void Quarantine(string cachePath)
    {
        var quarantineName = $"cef-cache.quarantined-{DateTime.Now:yyyyMMdd-HHmmss}";
        var parentDir = Path.GetDirectoryName(cachePath);
        if (parentDir is null)
        {
            App.Log?.Invoke("[CefCacheGuard] Cannot quarantine: no parent dir.");
            return;
        }

        var quarantinePath = Path.Combine(parentDir, quarantineName);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.Move(cachePath, quarantinePath);
                App.Log?.Invoke($"[CefCacheGuard] Moved corrupt cache to {quarantinePath}");
                return;
            }
            catch (Exception ex) when (attempt < 2)
            {
                App.Log?.Invoke($"[CefCacheGuard] Quarantine attempt {attempt + 1}: {ex.Message}");
                System.Threading.Thread.Sleep(200);
            }
            catch (Exception ex)
            {
                App.Log?.Invoke(
                    $"[CefCacheGuard] Quarantine failed: {ex.Message}. "
                    + "Deleting corrupt files individually."
                );
                PartialCleanup(cachePath);
                return;
            }
        }
    }

    /// <summary>
    /// Deletes only the corrupt files, preserving valid cookies / localStorage.
    /// </summary>
    private static void PartialCleanup(string cachePath)
    {
        var toDelete = new List<string>();

        // JSON files.
        foreach (var jsonName in JsonFiles)
        {
            var path = Path.Combine(cachePath, jsonName);
            if (File.Exists(path) && IsJsonCorrupt(path))
                toDelete.Add(path);
        }

        var defaultDir = Path.Combine(cachePath, "Default");
        if (Directory.Exists(defaultDir))
        {
            try
            {
                foreach (var jf in Directory.GetFiles(defaultDir, "*.json"))
                    if (IsJsonCorrupt(jf))
                        toDelete.Add(jf);
            }
            catch { /* best-effort */ }
        }

        // SQLite files.
        foreach (var dbName in SqliteDbNames)
        {
            var path = Path.Combine(cachePath, dbName);
            if (File.Exists(path) && IsSqliteCorrupt(path))
                toDelete.Add(path);
        }

        foreach (var ext in SqliteExtensions)
        {
            try
            {
                var pattern = "*" + ext;
                foreach (var db in Directory.EnumerateFiles(
                    cachePath, pattern, SearchOption.AllDirectories))
                {
                    if (IsSqliteCorrupt(db))
                        toDelete.Add(db);
                }
            }
            catch { /* best-effort */ }
        }

        // Crash artifacts.
        try
        {
            toDelete.AddRange(Directory.EnumerateFiles(
                cachePath, "*.dmp", SearchOption.AllDirectories));
            toDelete.AddRange(Directory.EnumerateFiles(
                cachePath, "*.crash", SearchOption.AllDirectories));
        }
        catch { /* best-effort */ }

        foreach (var file in toDelete)
            TryDeleteFile(file);
    }

    /// <summary>
    /// Removes quarantined cache directories older than 30 days.
    /// </summary>
    private static void PruneOldQuarantines(string cachePath)
    {
        var parentDir = Path.GetDirectoryName(cachePath);
        if (parentDir is null)
            return;

        try
        {
            foreach (var dir in Directory.GetDirectories(
                parentDir, "cef-cache.quarantined-*"))
            {
                try
                {
                    var info = new DirectoryInfo(dir);
                    if ((DateTime.Now - info.CreationTime).TotalDays > 30)
                        Directory.Delete(dir, recursive: true);
                }
                catch { /* best-effort */ }
            }
        }
        catch { /* best-effort */ }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch { /* best-effort */ }
    }
}

#endregion