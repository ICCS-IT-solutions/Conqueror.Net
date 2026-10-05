using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Conqueror.Net.WindowRoot.ViewModels;

/// <summary>
/// Turns an <see cref="Core.Tabs.ITabViewModel.IconKey"/> such as "Icon.Folder" into the
/// matching <see cref="Geometry"/> from the icon resource dictionary.
/// </summary>
public sealed class IconKeyToGeometryConverter : IValueConverter
{
    public static IconKeyToGeometryConverter Instance { get; } = new();

    public object? Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) => value is string key ? FindGeometry(key) : null;

    public object? ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) => throw new NotSupportedException();

    /// <summary>Looks a key up in Application.Resources, which no StaticResource binding can reach here.</summary>
    internal static Geometry? FindGeometry(string key)
    {
        var app = Application.Current;

        return
            app is not null
            && app.Resources.TryGetResource(key, app.ActualThemeVariant, out var value)
            ? value as Geometry
            : null;
    }
}

/// <summary>
/// Picks the folder/file glyph for an entry: an open folder shape for directories,
/// a page shape for files.
/// </summary>

/// <summary>
/// Tints the entry glyph the way Explorer does: manila-yellow folders, near-white pages
/// for files. Without this every item would be drawn in the folder colour.
/// </summary>
public sealed class FileEntryFillConverter : IValueConverter
{
    public static FileEntryFillConverter Instance { get; } = new();

    private static readonly IBrush Folder = new SolidColorBrush(Color.Parse("#E8B84A"));
    private static readonly IBrush File = new SolidColorBrush(Color.Parse("#FDFDFD"));

    public object? Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) => value is true ? Folder : File;

    public object? ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) => throw new NotSupportedException();
}

/// <summary>Outline colour matching <see cref="FileEntryFillConverter"/>.</summary>
public sealed class FileEntryStrokeConverter : IValueConverter
{
    public static FileEntryStrokeConverter Instance { get; } = new();

    private static readonly IBrush Folder = new SolidColorBrush(Color.Parse("#9C7B22"));
    private static readonly IBrush File = new SolidColorBrush(Color.Parse("#7A7A7A"));

    public object? Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) => value is true ? Folder : File;

    public object? ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) => throw new NotSupportedException();
}

public sealed class FileEntryIconConverter : IValueConverter
{
    public static FileEntryIconConverter Instance { get; } = new();

    public object? Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) => IconKeyToGeometryConverter.FindGeometry(value is true ? "Icon.FolderOpen" : "Icon.File");

    public object? ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) => throw new NotSupportedException();
}
