using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Conqueror.Net.FileBrowserUi.Models;

/// <summary>Which task-pane tab is showing. Bound to the tab strip's selected item.</summary>
public enum TaskPaneTab
{
    /// <summary>The folder tree and the shell-folder links.</summary>
    Folders,

    /// <summary>The recursive search, which walks the subtree below the current folder.</summary>
    Search,

    /// <summary>Commands that act on the current folder or open a well-known place.</summary>
    CommonTasks,
}

/// <summary>
/// One entry in the task-pane tab strip. <see cref="IsSelected"/> is two-way so the radio button
/// writes the user's choice straight back onto the tab list, and the view-model keeps the rest of
/// the tabs consistent from there.
/// </summary>
public sealed partial class TaskPaneTabItem : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public TaskPaneTabItem(TaskPaneTab tab, string name)
    {
        Tab = tab;
        Name = name;
    }

    /// <summary>The tab this entry switches to.</summary>
    public TaskPaneTab Tab { get; }

    /// <summary>Caption on the tab.</summary>
    public string Name { get; }

    /// <summary>True for the entry matching <see cref="FileBrowserViewModel.SelectedTaskPaneTab"/>.</summary>
    public bool Matches(TaskPaneTab tab) => Tab == tab;
}

/// <summary>
/// One row of the Common Tasks tab: a caption, an optional second line, and the action to run.
/// </summary>
/// <remarks>
/// Deliberately an <c>Action</c> and not a command type. The tasks are window-level operations
/// (new folder, go up, show hidden files) that already exist as commands or handlers on the owning
/// view-model, and wrapping each one in another ICommand would add indirection without adding
/// behaviour. The tab only has to invoke them.
/// </remarks>
public sealed class TaskPaneTask
{
    private readonly Action _invoke;

    public TaskPaneTask(string name, Action invoke, string? description = null, string? iconKey = null)
    {
        Name = name;
        _invoke = invoke;
        Description = description;
        IconKey = iconKey;
    }

    /// <summary>Caption shown in the task list.</summary>
    public string Name { get; }

    /// <summary>Optional second line under the caption, as Explorer shows for slower actions.</summary>
    public string? Description { get; }

    /// <summary>
    /// Key into the SVG icon theme, or null to fall back to the generic task arrow. See
    /// <see cref="Icons.FileIconResolver"/>.
    /// </summary>
    public string? IconKey { get; }

    public void Invoke() => _invoke();
}

/// <summary>
/// A titled, collapsible block in the task pane, such as Explorer's "Other Places". The
/// chevron in the header is driven by <see cref="IsExpanded"/>.
/// </summary>
public sealed partial class TaskPaneSection : ObservableObject
{
    private bool _isExpanded = true;

    public TaskPaneSection(string title, string? description = null)
    {
        Title = title;
        Description = description;
    }

    /// <summary>Caption shown in the section header.</summary>
    public string Title { get; }

    /// <summary>Optional second line under the title.</summary>
    public string? Description { get; }

    public ObservableCollection<ShellFolder> Items { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }
}