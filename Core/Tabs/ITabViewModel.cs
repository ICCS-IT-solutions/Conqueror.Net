using System.ComponentModel;

namespace Conqueror.Net.Core.Tabs;

/// <summary>
/// Implemented by the view-models that can live inside a shell tab. The shell (window root)
/// talks to every tab through this interface only, which is what allows a single address bar,
/// tab strip and navigation model to drive both file-system tabs and web browser tabs.
/// </summary>
/// <remarks>
/// Extends <see cref="INotifyPropertyChanged"/> because the shell binds directly to these
/// properties; every implementation is a CommunityToolkit.Mvvm <c>ObservableObject</c>.
/// </remarks>
public interface ITabViewModel : INotifyPropertyChanged
{
    /// <summary>Text shown on the tab and used as the window title suffix.</summary>
    string Title { get; }

    /// <summary>Key used by the view layer to look up the tab's icon geometry.</summary>
    string IconKey { get; }

    /// <summary>Current location, shown in the shared address bar.</summary>
    string Location { get; }

    bool CanGoBack { get; }

    bool CanGoForward { get; }
    public bool CanNewFile { get; }
    public bool CanNewFolder { get; }
    public bool CanCut { get; }
    public bool CanCopy { get; }
    public bool CanPaste { get; }
    public bool CanDelete { get; }
    public bool CanRename { get; }
    public bool CanProperties { get; }
    public bool CanSelectAll { get; }
    public bool CanSelectNone { get; }
    public bool CanInvertSelect { get; }

    /// <summary>True while the tab is busy loading, so the shell can show a stop button.</summary>
    bool IsBusy { get; }

    /// <summary>0..100 when <see cref="IsBusy"/> is true, otherwise null.</summary>
    double? LoadProgress { get; }

    /// <summary>
    /// Text for the shell's status bar. Both tab kinds have something to say here: the
    /// object count and free space for a folder, load state or an error for a page.
    /// </summary>
    string StatusText { get; }

    /// <summary>
    /// Text of the shell's search box. File tabs filter the folder listing with it.
    /// </summary>
    string SearchText { get; set; }

    /// <summary>
    /// True only for folder tabs. The shell uses this to hide the folder-pane and
    /// show-hidden-files controls, which mean nothing on a web page.
    /// </summary>
    bool IsFileBrowser { get; }

    void GoBack();

    void GoForward();

    /// <summary>Navigates to <paramref name="location"/>, interpreting it as appropriate for the tab kind.</summary>
    void Navigate(string location);

    /// <summary>Requests the tab reload its current location.</summary>
    void Reload();

    /// <summary>Stops any in-flight work started by <see cref="Navigate"/>.</summary>
    void Stop();
}
