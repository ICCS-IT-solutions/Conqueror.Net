using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Conqueror.Net.FileBrowserUi.Models;

namespace Conqueror.Net.FileBrowserUi.ViewModels;

/// <summary>
/// The Folders task pane: a lazily-expanded folder tree plus the collapsible "Other Places"
/// and "Details" sections seen in the Explorer screenshots.
/// </summary>
public sealed partial class FileBrowserViewModel
{
    public ObservableCollection<FolderTreeNode> TreeRoots { get; } = [];

    public ObservableCollection<TaskPaneSection> TaskPaneSections { get; } = [];

    /// <summary>Tasks shown on the Common Tasks tab, built once by <see cref="BuildCommonTasks"/>.</summary>
    public ObservableCollection<TaskPaneTask> CommonTasks { get; } = [];

    /// <summary>The three tab-strip entries, in display order.</summary>
    public ObservableCollection<TaskPaneTabItem> TaskPaneTabs { get; } =
    [
        new TaskPaneTabItem(TaskPaneTab.Folders, "Folders"),
        new TaskPaneTabItem(TaskPaneTab.Search, "Search"),
        new TaskPaneTabItem(TaskPaneTab.CommonTasks, "Tasks"),
    ];

    [ObservableProperty]
    private FolderTreeNode? _selectedTreeNode;

    /// <summary>
    /// The task pane is three tabs sharing the pane's width. Only the selected one is in the
    /// visual tree, so the folder tree does not rebuild while a search runs on it.
    /// </summary>
    [ObservableProperty]
    private TaskPaneTab _selectedTaskPaneTab = TaskPaneTab.Folders;

    public bool IsFoldersTabVisible => SelectedTaskPaneTab == TaskPaneTab.Folders;

    public bool IsSearchTabVisible => SelectedTaskPaneTab == TaskPaneTab.Search;

    public bool IsCommonTasksTabVisible => SelectedTaskPaneTab == TaskPaneTab.CommonTasks;

    /// <summary>
    /// Keeps the tab strip and the pane content in step.
    /// </summary>
    /// <remarks>
    /// The tab strip is a set of radio buttons over these entries, so the buttons own
    /// <c>IsChecked</c> and a click arrives here as a <c>CommandParameter</c> rather than as a
    /// property change. The loop below re-asserts the selection on every entry, because the
    /// generator would otherwise leave whichever button lost the group check still checked.
    /// </remarks>
    [RelayCommand]
    private void SelectTaskPaneTab(TaskPaneTabItem? item)
    {
        if (item is null)
        {
            return;
        }

        foreach (var tab in TaskPaneTabs)
        {
            tab.IsSelected = ReferenceEquals(tab, item);
        }

        SelectedTaskPaneTab = item.Tab;
    }

    /// <summary>
    /// Raises the three tab-content visibility properties, which are computed from
    /// <see cref="SelectedTaskPaneTab"/> rather than generated.
    /// </summary>
    partial void OnSelectedTaskPaneTabChanged(TaskPaneTab value)
    {
        OnPropertyChanged(nameof(IsFoldersTabVisible));
        OnPropertyChanged(nameof(IsSearchTabVisible));
        OnPropertyChanged(nameof(IsCommonTasksTabVisible));

        // The strip is driven from the view-model too, so a tab changed from anywhere else
        // (including the initial default) still shows the right button checked.
        foreach (var tab in TaskPaneTabs)
        {
            tab.IsSelected = tab.Tab == value;
        }
    }

    [RelayCommand]
    private void ToggleSection(TaskPaneSection? section)
    {
        if (section is not null)
        {
            section.IsExpanded = !section.IsExpanded;
        }
    }

    /// <summary>
    /// Loads a node's children the first time it opens. One directory rather than a volume,
    /// on an explicit click rather than at start-up, which is what keeps the pane instant.
    /// </summary>
    private void OnTreeNodeExpanded(FolderTreeNode node)
    {
        if (node.ChildrenLoaded)
        {
            return;
        }

        node.SetChildren(
            _fileSystem
                .ListChildDirectories(node.Path)
                .OrderBy(p => p, StringComparer.CurrentCultureIgnoreCase)
                .Select(p => new FolderTreeNode(
                    Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar)),
                    p,
                    ShellFolderKind.Folder
                ))
        );
    }

    /// <summary>Navigates when a row in the tree is chosen.</summary>
    [RelayCommand]
    private void NavigateToTreeNode(FolderTreeNode? node)
    {
        if (node is not null && _fileSystem.IsDirectory(node.Path))
        {
            PushHistory(node.Path);
        }
    }

    /// <summary>
    /// Reveals <paramref name="path"/> in the tree: opens each ancestor and selects the node, so
    /// the pane stays in step when navigation came from the address bar or the entry list.
    /// </summary>
    /// <remarks>
    /// The reveal walks the real on-disk path rather than the already-loaded tree, because
    /// typing a deep path into the address bar never expanded those ancestors — a search over
    /// loaded children would silently find nothing and leave the pane looking stale. Each
    /// ancestor is expanded on demand (which loads its children) before the next segment is
    /// looked up, so the chain appears one level at a time, exactly as Explorer does.
    /// </remarks>
    public void RevealInTree(string path)
    {
        var full = SafeFullPath(path);
        if (full is null)
        {
            return;
        }

        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
        {
            return;
        }

        // The path as a stack of directory names under the root, e.g. C:\a\b\c -> [a, b, c].
        var segments = full[root.Length..]
            .Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);

        var current = FindRoot(root);
        if (current is null)
        {
            return;
        }

        // Expand the root itself so its children are enumerated before we look for the first
        // segment; without this the root's expander stays shut and the reveal halts.
        ExpandToLoad(current);

        foreach (var segment in segments)
        {
            var next = FindChild(current, segment);
            if (next is null)
            {
                // The target is not a directory we list (a file, a virtual path, a folder
                // that has since been deleted) — reveal as far as we could and stop.
                return;
            }

            ExpandToLoad(next);
            current = next;
        }

        current.IsSelected = true;
        SelectedTreeNode = current;
    }

    /// <summary>Finds the tree root whose path matches <paramref name="rootPath"/>, if any.</summary>
    private FolderTreeNode? FindRoot(string rootPath)
    {
        foreach (var root in TreeRoots)
        {
            if (PathsEqual(root.Path, rootPath))
            {
                return root;
            }
        }

        return null;
    }

    /// <summary>Among <paramref name="parent"/>'s loaded children, the one named <paramref name="segment"/>, if any.</summary>
    private static FolderTreeNode? FindChild(FolderTreeNode parent, string segment)
    {
        foreach (var child in parent.Children)
        {
            if (string.Equals(child.Name, segment, StringComparison.OrdinalIgnoreCase))
            {
                return child;
            }
        }

        return null;
    }

    /// <summary>Expands <paramref name="node"/> if it has not enumerated its children yet.</summary>
    private static void ExpandToLoad(FolderTreeNode node)
    {
        if (!node.ChildrenLoaded)
        {
            node.IsExpanded = true;
        }
    }

    /// <summary>
    /// Builds the tree roots and the task-pane sections. The roots mirror the top level of the
    /// screenshot: Desktop and My Documents, then My Computer with the drives beneath it.
    /// </summary>
    private void BuildFolderPane()
    {
        FolderTreeNode? desktop = null;
        FolderTreeNode? myDocuments = null;
        FolderTreeNode? myComputer = null;

        foreach (var folder in _fileSystem.GetShellFolders())
        {
            switch (folder.Kind)
            {
                case ShellFolderKind.Drive:
                    // Explorer hangs the drives under My Computer rather than listing them
                    // beside it, so the tree does the same.
                    myComputer ??= AddRoot("My Computer", folder.Path);
                    myComputer.AddChild(new FolderTreeNode(folder.Name, folder.Path, folder.Kind));
                    break;

                case ShellFolderKind.Network:
                    AddRoot(folder.Name, folder.Path, ShellFolderKind.Network);
                    break;

                default:
                    if (string.Equals(folder.Name, "Desktop", StringComparison.OrdinalIgnoreCase))
                    {
                        desktop ??= AddRoot(folder);
                    }
                    else if (string.Equals(folder.Name, "My Documents", StringComparison.OrdinalIgnoreCase))
                    {
                        myDocuments ??= AddRoot(folder);
                    }
                    else if (string.Equals(folder.Name, "My Computer", StringComparison.OrdinalIgnoreCase))
                    {
                        myComputer ??= AddRoot(folder);
                    }

                    break;
            }
        }

        // Create My Computer even on a machine where the profile folder was unavailable but
        // drives were still listed, so the drives never end up stranded at the root.
        myComputer ??= AddRoot(
            "My Computer",
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        );

        ReorderRoots(desktop, myDocuments, myComputer);

        foreach (var node in TreeRoots)
        {
            node.ExpandRequested += OnTreeNodeExpanded;
        }

        BuildTaskPaneSections();
    }

    private FolderTreeNode AddRoot(FolderTreeNode node) => AddRoot(node.Name, node.Path, node.Kind);

    private FolderTreeNode AddRoot(ShellFolder folder) =>
        AddRoot(folder.Name, folder.Path, folder.Kind);

    private FolderTreeNode AddRoot(string name, string path, ShellFolderKind kind = ShellFolderKind.Folder)
    {
        var node = new FolderTreeNode(name, path, kind);
        TreeRoots.Add(node);
        return node;
    }

    /// <summary>Moves the named roots to the top in the order given, as Explorer presents them.</summary>
    private void ReorderRoots(params FolderTreeNode?[] ordered)
    {
        var wanted = ordered.Where(n => n is not null).ToList();

        for (var i = wanted.Count - 1; i >= 0; i--)
        {
            TreeRoots.Move(TreeRoots.IndexOf(wanted[i]!), 0);
        }
    }

    /// <summary>
    /// The collapsible sections from the task-pane screenshot. "Details" carries the current
    /// location as its description; the content list already reports the object count, so
    /// repeating it here would just show the same number twice.
    /// </summary>
    private void BuildTaskPaneSections()
    {
        var otherPlaces = new TaskPaneSection("Other Places");

        foreach (
            var folder in _fileSystem
                .GetShellFolders()
                .Where(f => f.Kind is ShellFolderKind.Network or ShellFolderKind.Folder)
                .Take(6)
        )
        {
            otherPlaces.Items.Add(folder);
        }

        TaskPaneSections.Add(otherPlaces);
        TaskPaneSections.Add(new TaskPaneSection("Details"));

        BuildCommonTasks();
    }

    /// <summary>
    /// The Common Tasks tab. Each entry invokes a command that already exists on this view-model
    /// rather than reimplementing it, so the pane and the menu bar cannot drift apart.
    /// </summary>
    /// <remarks>
    /// Each row captures a lambda rather than a method group: the commands generated from
    /// <see cref="RelayCommandAttribute"/> all expose <c>Execute(object?)</c>, so passing
    /// <c>GoUpCommand.Execute</c> straight into an <see cref="Action"/> parameter would not
    /// compile.
    /// </remarks>
    private void BuildCommonTasks()
    {
        CommonTasks.Add(
            new TaskPaneTask(
                "Go up one level",
                () => GoUpCommand.Execute(null),
                "Opens the parent folder",
                Icons.FileIconResolver.Folder
            )
        );
        CommonTasks.Add(
            new TaskPaneTask(
                "Refresh",
                () => RefreshCommand.Execute(null),
                "Reloads this folder",
                Icons.FileIconResolver.Folder
            )
        );
        CommonTasks.Add(
            new TaskPaneTask(
                "New folder",
                () => CreateNewFolderCommand.Execute(null),
                "Creates a folder here",
                Icons.FileIconResolver.Folder
            )
        );

        void AddViewMode(string name, FileViewMode mode, string description) =>
            CommonTasks.Add(
                new TaskPaneTask(
                    name,
                    () => SetViewModeCommand.Execute(mode),
                    description,
                    Icons.FileIconResolver.Folder
                )
            );

        AddViewMode("Show as icons", FileViewMode.Icons, "Large icons");
        AddViewMode("Show as list", FileViewMode.List, "One item per row");
        AddViewMode("Show as details", FileViewMode.Details, "Columns and file details");
    }

    private static string? SafeFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex)
            when (ex
                is ArgumentException
                    or NotSupportedException
                    or PathTooLongException
                    or IOException
            )
        {
            return null;
        }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            a.TrimEnd(Path.DirectorySeparatorChar),
            b.TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase
        );
}