using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Conqueror.Net.CodeEditorUi.ViewModels;

/// <summary>
/// Maps the editor's <c>WordWrap</c> bool onto the <see cref="TextBox"/>'s
/// <see cref="TextWrapping"/> enum. Bool and enum are not implicitly convertible in a
/// binding, so without this the Wrap checkbox would silently do nothing.
/// </summary>
public sealed class BoolToWrapConverter : IValueConverter
{
    public static BoolToWrapConverter Instance { get; } = new();

    public object? Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) => value is true ? TextWrapping.Wrap : TextWrapping.NoWrap;

    public object? ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) => value is TextWrapping.Wrap;
}