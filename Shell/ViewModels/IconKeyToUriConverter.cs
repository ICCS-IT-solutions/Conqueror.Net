namespace Conqueror.Net.Shell.ViewModels;

/// <summary>
/// Turns an icon key into the avares URI that <c>Avalonia.Svg.Skia.Svg</c> loads.
/// </summary>
/// <remarks>
/// The file manager has an equivalent converter. It is duplicated rather than shared because
/// the shell is a separate assembly, and a project reference would drag CEF and the entire
/// browser stack into a program that only needs to draw a taskbar. The two must agree on the
/// asset path, which they do because both read the same <c>Assets/Icons</c> folder - hence the
/// <c>Link</c> in <c>Conqueror.Net.Shell.csproj</c>. Only the assembly name differs.
/// </remarks>
public sealed class IconKeyToUriConverter : Avalonia.Data.Converters.IValueConverter
{
    private const string AssetPrefix = "avares://Conqueror.Net.Shell/Assets/Icons/";

    public static IconKeyToUriConverter Instance { get; } = new();

    public object? Convert(
        object? value,
        Type targetType,
        object? parameter,
        System.Globalization.CultureInfo culture) =>
        AssetPrefix + (value is string key && key.Length > 0 ? key : "text") + ".svg";

    public object? ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}
