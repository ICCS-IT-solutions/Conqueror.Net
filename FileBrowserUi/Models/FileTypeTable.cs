namespace Conqueror.Net.FileBrowserUi.Models;

/// <summary>Formats byte counts the way Explorer's status bar and details view do.</summary>
public static class ByteSizeFormatter
{
    private static readonly string[] Units = ["bytes", "KB", "MB", "GB", "TB"];

    public static string Format(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes:N0} {Units[0]}";
        }

        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // Explorer drops the decimals once the number gets comfortably large.
        return value >= 100 ? $"{value:N0} {Units[unit]}" : $"{value:N1} {Units[unit]}";
    }
}

/// <summary>Maps file extensions onto the friendly type names Explorer shows.</summary>
public static class FileTypeTable
{
    private static readonly Dictionary<string, string> Known =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".txt"] = "Text Document",
            [".log"] = "Log File",
            [".ini"] = "Configuration Settings",
            [".cfg"] = "Configuration Settings",
            [".xml"] = "XML Document",
            [".json"] = "JavaScript Object Notation File",
            [".csv"] = "CSV File",
            [".rtf"] = "Rich Text Document",
            [".doc"] = "Microsoft Word Document",
            [".docx"] = "Microsoft Word Document",
            [".xls"] = "Microsoft Excel Worksheet",
            [".xlsx"] = "Microsoft Excel Worksheet",
            [".ppt"] = "Microsoft PowerPoint Presentation",
            [".pptx"] = "Microsoft PowerPoint Presentation",
            [".pdf"] = "PDF Document",
            [".zip"] = "Compressed (zipped) Folder",
            [".7z"] = "7-Zip Archive",
            [".rar"] = "WinRAR Archive",
            [".gz"] = "Gzip Archive",
            [".exe"] = "Application",
            [".msi"] = "Windows Installer Package",
            [".bat"] = "Batch File",
            [".cmd"] = "Windows Command Script",
            [".ps1"] = "PowerShell Script",
            [".lnk"] = "Shortcut",
            [".url"] = "Internet Shortcut",
            [".htm"] = "HTML Document",
            [".html"] = "HTML Document",
            [".css"] = "Cascading Style Sheet",
            [".js"] = "JavaScript File",
            [".cs"] = "C# Source File",
            [".xaml"] = "XAML Document",
            [".png"] = "PNG Image",
            [".jpg"] = "JPEG Image",
            [".jpeg"] = "JPEG Image",
            [".gif"] = "GIF Image",
            [".bmp"] = "Bitmap Image",
            [".ico"] = "Icon",
            [".svg"] = "Scalable Vector Graphics",
            [".mp3"] = "MP3 Audio File",
            [".wav"] = "Wave Audio",
            [".mp4"] = "MP4 Video",
            [".mkv"] = "Matroska Video",
            [".avi"] = "Audio Video Interleave",
            [".ttf"] = "TrueType Font",
            [".dll"] = "Application Extension",
            [".nupkg"] = "NuGet Package",
            [".iso"] = "Disc Image File",
        };

    public static string Describe(string extension)
    {
        if (string.IsNullOrEmpty(extension))
        {
            return "File";
        }

        return Known.TryGetValue(extension, out var description)
            ? description
            : $"{extension[1..].ToUpperInvariant()} File";
    }
}
