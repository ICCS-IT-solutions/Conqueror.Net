using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Conqueror.Net.Shell.Models;

namespace Conqueror.Net.Shell.ViewModels;

/// <summary>View-model for the taskbar window.</summary>
public sealed partial class TaskbarViewModel : ObservableObject
{
    private readonly Shell.Services.ShellWindowManager _windows;
    private readonly Shell.Services.ShellSettings _settings;

    [ObservableProperty]
    private string _clock = DateTime.Now.ToString("HH:mm");

    [ObservableProperty]
    private Shell.Models.TaskbarEdge _edge;

    /// <summary>Whether the bar is running along the top or bottom rather than a side.</summary>
    [ObservableProperty]
    private bool _isHorizontal = true;

    /// <summary>Where the Start button sits within the bar.</summary>
    /// <remarks>
    /// A left-docked bar runs vertically, so the Start button docks to its top instead of its
    /// left. <see cref="DockPanel.Dock"/> is a bindable attached property, so this drives the
    /// layout directly rather than needing a second set of styles.
    /// </remarks>
    public Avalonia.Controls.Dock StartDock => IsHorizontal ? Avalonia.Controls.Dock.Left : Avalonia.Controls.Dock.Top;

    /// <summary>Where the quick-launch row sits.</summary>
    public Avalonia.Controls.Dock QuickLaunchDock => StartDock;

    /// <summary>Where the tray clock sits - always the far end of the bar.</summary>
    public Avalonia.Controls.Dock TrayDock => IsHorizontal ? Avalonia.Controls.Dock.Right : Avalonia.Controls.Dock.Bottom;

    /// <summary>Start button width: wide on a horizontal bar, bar-thickness on a vertical one.</summary>
    /// <remarks>
    /// Without this a 54 px button is forced into a 30 px bar and Avalonia squashes it into an
    /// ellipse, which is what the first left-docked screenshot showed.
    /// </remarks>
    public double StartWidth => IsHorizontal ? 54 : 30;

    /// <summary>Start button height, the mirror of <see cref="StartWidth"/>.</summary>
    public double StartHeight => IsHorizontal ? 30 : 54;

    /// <summary>How far round the Start orb's label sits, so it reads along the bar.</summary>
    /// <remarks>
    /// XP rotated the orb's text when the taskbar was vertical. Binding the angle rather than
    /// swapping in a second layout keeps one template for all four edges.
    /// </remarks>
    public double OrbRotation => IsHorizontal ? 0 : -90;

    /// <summary>Whether the tray stacks its clock along the bar rather than across it.</summary>
    public Avalonia.Layout.Orientation TrayOrientation =>
        IsHorizontal ? Avalonia.Layout.Orientation.Horizontal : Avalonia.Layout.Orientation.Vertical;

    /// <summary>Whether the quick-launch row runs along the bar or down it.</summary>
    /// <remarks>
    /// Without this, four 26 px buttons are laid out horizontally and squeezed into a 30 px bar,
    /// which showed only the first icon. The first left-docked screenshot showed exactly that.
    /// </remarks>
    public Avalonia.Layout.Orientation QuickLaunchOrientation => TrayOrientation;

    public TaskbarViewModel(Shell.Services.ShellWindowManager windows, Shell.Services.ShellSettings settings)
    {
        _windows = windows;
        _settings = settings;
        _edge = settings.Edge;

        // The manager decides which edge is actually in effect - which differs from the
        // configured one when following the Windows taskbar - and reports it here.
        _windows.EdgeChanged += ApplyResolvedEdge;

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

    /// <summary>
    /// Reflects the edge the manager actually applied.
    /// </summary>
    /// <remarks>
    /// Named <c>ApplyResolvedEdge</c> rather than <c>OnEdgeChanged</c> because the source
    /// generator for <c>[ObservableProperty]</c> already emits a partial method of that name for
    /// the Edge property, and the two would collide. The dock sides are re-announced here because
    /// the generator only notifies for IsHorizontal itself, and the layout binds to these.
    /// </remarks>
    private void ApplyResolvedEdge(Shell.Models.TaskbarEdge edge)
    {
        var horizontal = edge.IsHorizontal();

        if (horizontal != IsHorizontal)
        {
            IsHorizontal = horizontal;

            foreach (var name in LayoutProperties)
            {
                OnPropertyChanged(name);
            }
        }
    }

    /// <summary>Properties that follow IsHorizontal and must be re-announced when it flips.</summary>
    /// <remarks>
    /// The source generator notifies only for IsHorizontal itself. Every derived property is
    /// listed here because each one is bound from the XAML.
    /// </remarks>
    private static readonly string[] LayoutProperties =
    [
        nameof(StartDock),
        nameof(QuickLaunchDock),
        nameof(TrayDock),
        nameof(StartWidth),
        nameof(StartHeight),
        nameof(OrbRotation),
        nameof(TrayOrientation),
        nameof(QuickLaunchOrientation),
    ];

    /// <summary>
    /// Moves the taskbar to a new edge and remembers the choice.
    /// </summary>
    /// <remarks>
    /// Invoked from the bar's context menu. Saving immediately rather than on exit means the
    /// choice survives even if the shell is killed, which is a normal way to end a shell.
    /// </remarks>
    [RelayCommand]
    private void SetEdge(Shell.Models.TaskbarEdge edge)
    {
        _settings.Edge = edge;
        _settings.Save();

        // Reposition resolves the edge and raises EdgeChanged, so the view-model's
        // IsHorizontal follows without this having to update it directly.
        _windows.Reposition();
    }
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
