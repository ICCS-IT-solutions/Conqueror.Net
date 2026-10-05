using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Conqueror.Net.Shell.ViewModels;

/// <summary>View-model for the Start menu.</summary>
public sealed partial class StartMenuViewModel : ObservableObject
{
    private readonly Action _close;
    private readonly Action<string?> _launchFileManager;

    public StartMenuViewModel(Action close, Action<string?> launchFileManager)
    {
        _close = close;
        _launchFileManager = launchFileManager;

        // Built here rather than in a field initializer, because the items capture
        // _launchFileManager and a field initializer cannot read an instance member.
        Items =
        [
            new("Conqueror.Net", "folder", () => _launchFileManager(null)),
            new("My Documents", "document", () => _launchFileManager(Documents)),
            new("My Pictures", "image", () => _launchFileManager(Special(Environment.SpecialFolder.MyPictures))),
            new("My Music", "audio", () => _launchFileManager(Special(Environment.SpecialFolder.MyMusic))),
            new("My Videos", "video", () => _launchFileManager(Special(Environment.SpecialFolder.MyVideos))),
            new("Recycle Bin", "trash", static () => { }),
        ];
    }

    private static readonly string? Documents = Special(Environment.SpecialFolder.MyDocuments);

    /// <summary>
    /// Resolves a special folder, returning null when the system will not say.
    /// </summary>
    /// <remarks>
    /// <c>GetFolderPath</c> throws on some redirected-profile configurations; a missing entry
    /// in the menu is far better than a shell that will not start.
    /// </remarks>
    private static string? Special(Environment.SpecialFolder folder)
    {
        try
        {
            var path = Environment.GetFolderPath(folder);

            return string.IsNullOrEmpty(path) ? null : path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public ObservableCollection<StartMenuItem> Items { get; }

    public string UserName => Environment.UserName;

    [RelayCommand]
    private void Close() => _close();
}

/// <summary>
/// One row in the Start menu.
/// </summary>
/// <remarks>
/// A view-model rather than a plain record: <see cref="Activate"/> is an <see cref="Action"/>,
/// which XAML cannot bind a <c>Command</c> to, so the action is wrapped in a relay command.
/// </remarks>
public sealed partial class StartMenuItem : ObservableObject
{
    private readonly Action _activate;

    public StartMenuItem(string label, string iconKey, Action activate)
    {
        Label = label;
        IconKey = iconKey;
        _activate = activate;
    }

    public string Label { get; }

    /// <summary>Key in the shared SVG icon theme.</summary>
    public string IconKey { get; }

    [RelayCommand]
    private void Activate() => _activate();
}
