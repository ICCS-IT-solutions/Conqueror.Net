using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Conqueror.Net.Shell.ViewModels;

/// <summary>View-model for the taskbar window.</summary>
public sealed partial class TaskbarViewModel : ObservableObject
{
    private readonly Shell.Services.ShellWindowManager _windows;

    [ObservableProperty]
    private string _clock = DateTime.Now.ToString("HH:mm");

    public TaskbarViewModel(Shell.Services.ShellWindowManager windows)
    {
        _windows = windows;

        // Built here rather than in a field initializer: the items call Launch, which is an
        // instance method.
        Buttons =
        [
            new("My Documents", "folder", () => Launch(Environment.SpecialFolder.MyDocuments)),
            new("My Pictures", "image", () => Launch(Environment.SpecialFolder.MyPictures)),
            new("My Music", "audio", () => Launch(Environment.SpecialFolder.MyMusic)),
            new("My Videos", "video", () => Launch(Environment.SpecialFolder.MyVideos)),
        ];

        // XP showed the clock from a single shared timer rather than one per instance; a
        // DispatcherTimer is the Avalonia equivalent and keeps this process self-contained.
        var timer = new Avalonia.Threading.DispatcherTimer(
            TimeSpan.FromSeconds(10),
            Avalonia.Threading.DispatcherPriority.Background,
            (_, _) =>
            {
                Clock = DateTime.Now.ToString("HH:mm");
                _windows.Reposition();
            }
        );

        timer.Start();
    }

    /// <summary>Buttons for the applications the shell knows how to launch.</summary>
    public ObservableCollection<TaskbarButton> Buttons { get; }

    private void Launch(Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder);

        if (!string.IsNullOrEmpty(path))
        {
            TryOpen(path);
        }
    }

    /// <summary>
    /// Opens a path in the user's default handler.
    /// </summary>
    /// <remarks>
    /// Shell execution on purpose: this is what makes a folder open in Explorer rather than
    /// spawning a copy of whichever application happens to be registered for .txt.
    /// </remarks>
    private static void TryOpen(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
            when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            ShellApp.Log?.Invoke($"could not open {path}: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ToggleStartMenu() => _windows.ToggleStartMenu();
}

/// <summary>
/// One quick-launch button on the taskbar.
/// </summary>
/// <remarks>
/// A view-model rather than a plain record, for the same reason as
/// <see cref="StartMenuItem"/>: XAML cannot bind a command to an <see cref="Action"/>.
/// </remarks>
public sealed partial class TaskbarButton : ObservableObject
{
    private readonly Action _activate;

    public TaskbarButton(string label, string iconKey, Action activate)
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
