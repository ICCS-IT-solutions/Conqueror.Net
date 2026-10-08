using AvaloniaEdit;

namespace Conqueror.Net.CodeEditorUi.ViewModels;

/// <summary>
/// Adapts AvaloniaEdit's <see cref="TextEditor"/> to the <see cref="IEditorBox"/>
/// contract the view-model's edit commands drive. Only the surface the VM needs
/// (text, selection, caret, select-all) is exposed; everything else — syntax
/// highlighting, line numbers, undo/redo — is wired in the view directly.
/// </summary>
public sealed class TextEditorAdapter : IEditorBox
{
    private readonly TextEditor _editor;

    public TextEditorAdapter(TextEditor editor) => _editor = editor;

    public string? Text
    {
        get => _editor.Text;
        set => _editor.Text = value ?? string.Empty;
    }

    public string? SelectedText => _editor.SelectedText;

    public int SelectionStart => _editor.SelectionStart;

    /// <summary>
    /// AvaloniaEdit stores the caret as <c>CaretOffset</c> (character index),
    /// which maps directly to the interface's <c>CaretIndex</c>.
    /// </summary>
    public int CaretIndex
    {
        get => _editor.CaretOffset;
        set => _editor.CaretOffset = value;
    }

        public void SelectRange(int start, int length) => _editor.Select(start, length);

    public void SelectAll() => _editor.SelectAll();
}