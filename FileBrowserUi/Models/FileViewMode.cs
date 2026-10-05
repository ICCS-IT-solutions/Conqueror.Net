namespace Conqueror.Net.FileBrowserUi.Models;

/// <summary>The four folder-pane layouts offered by Explorer's "Views" menu.</summary>
public enum FileViewMode
{
    /// <summary>Large icons, no details. Explorer's "Icons" view.</summary>
    Icons,

    /// <summary>Small icons in a flowing grid. Explorer's "List" view.</summary>
    List,

    /// <summary>Small icons with a name column beside each. Explorer's "Details" view.</summary>
    Details,

    /// <summary>Large icon plus metadata, one per row. Explorer's "Tiles" view.</summary>
    Tiles,
}

/// <summary>Columns of the details view, in their default left-to-right order.</summary>
public enum FileSortColumn
{
    Name,
    Size,
    Type,
    DateModified,
}
