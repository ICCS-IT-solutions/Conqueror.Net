using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Conqueror.Net.FileBrowserUi.Models;

/// <summary>
/// One row of the Folders task pane: a folder, its lazily-loaded children, and the expansion
/// state the <c>TreeView</c> binds to.
/// </summary>
/// <remarks>
/// Children are enumerated on demand rather than up front. Building the whole drive tree at
/// start-up would mean listing every directory on every volume before the first frame appears,
/// which is exactly what makes Explorer's own pane feel slow on a large disk.
/// </remarks>
public sealed partial class FolderTreeNode : ObservableObject
{
    private bool _canExpand;
    private bool _isExpanded;
    private bool _isSelected;

    public FolderTreeNode(string name, string path, ShellFolderKind kind, bool canExpand = true)
    {
        Name = name;
        Path = path;
        Kind = kind;
        _canExpand = canExpand;

        // The network artwork is deliberately not bundled, so it needs the same guarded
        // fallback as ShellFolder.IconKey rather than rendering as an empty gap.
        IconKey = kind switch
        {
            ShellFolderKind.Drive => Icons.FileIconResolver.DriveHardDisk,
            ShellFolderKind.Network => Icons.FileIconResolver.IsBundled(
                    Icons.FileIconResolver.Network
                )
                ? Icons.FileIconResolver.Network
                : Icons.FileIconResolver.Folder,
            _ => Icons.FileIconResolver.ForFolder(path),
        };
    }

    public string Name { get; }

    public string Path { get; }

    public ShellFolderKind Kind { get; }

    /// <summary>Key into the SVG icon theme; see <see cref="Icons.FileIconResolver"/>.</summary>
    public string IconKey { get; }

    /// <summary>Set as children are added, so a navigation can walk up to reveal a node.</summary>
    public FolderTreeNode? Parent { get; internal set; }

    public ObservableCollection<FolderTreeNode> Children { get; } = [];

    /// <summary>True once the children have been read, so re-expanding does not re-hit the disk.</summary>
    public bool ChildrenLoaded { get; private set; }

    /// <summary>
    /// Whether the row shows an expander box. Starts optimistic for anything that could hold
    /// folders and is corrected once the children are actually known.
    /// </summary>
    public bool CanExpand
    {
        get => _canExpand;
        private set => SetProperty(ref _canExpand, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value) && value)
            {
                ExpandRequested?.Invoke(this);
            }
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>Raised when the row expands, so the view-model can enumerate its children.</summary>
    public event Action<FolderTreeNode>? ExpandRequested;

    /// <summary>
    /// Appends a child without marking the children loaded, so the node still reads as
    /// "not yet enumerated" and expands from disk on first use.
    /// </summary>
    public void AddChild(FolderTreeNode child)
    {
        child.Parent = this;
        Children.Add(child);
        CanExpand = true;
    }

    /// <summary>
    /// Fills the children once. A second call is ignored unless <paramref name="force"/> is set,
    /// which is what Refresh uses to pick up folders created since the first listing.
    /// </summary>
    public void SetChildren(IEnumerable<FolderTreeNode> children, bool force = false)
    {
        if (ChildrenLoaded && !force)
        {
            return;
        }

        ChildrenLoaded = true;

        if (force)
        {
            Children.Clear();
        }

        foreach (var child in children)
        {
            child.Parent = this;
            Children.Add(child);
        }

        // An empty folder has nothing to open, so its expander box disappears - the same
        // correction Explorer makes once it has listed the folder.
        CanExpand = Children.Count > 0;
    }
}