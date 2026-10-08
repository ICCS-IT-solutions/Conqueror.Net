using System;

namespace Conqueror.Net.CodeEditorUi.ViewModels;

/// <summary>
/// Minimal text-editor surface the view-model drives through its edit commands.
/// Abstracts the concrete control so the same cut/copy/paste logic works whether
/// the view uses a plain <see cref="TextBox"/> or an AvaloniaEdit
/// <see cref="AvaloniaEdit.TextEditor"/>.
/// </summary>
public interface IEditorBox
{
    string? Text { get; set; }

    string? SelectedText { get; }

    int SelectionStart { get; }

    /// <summary>
    /// Caret position (character offset). In AvaloniaEdit this maps to
    /// <c>CaretOffset</c>; in a plain TextBox it is <c>CaretIndex</c>.
    /// </summary>
        int CaretIndex { get; set; }

    /// <summary>
    /// Selects a range of text. Used by Find/Replace operations.
    /// </summary>
    void SelectRange(int start, int length);

    void SelectAll();
}