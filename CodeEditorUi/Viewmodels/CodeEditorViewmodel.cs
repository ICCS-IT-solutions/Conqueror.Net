using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using AvaloniaEdit.Highlighting;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Conqueror.Net.Core.Tabs;
using Conqueror.Net.FileBrowserUi.Services;

namespace Conqueror.Net.CodeEditorUi.ViewModels;

/// <summary>
/// An in-process text/code/config editor tab. Reads and writes files directly instead of
/// shelling out to an external program, so config tweaks never leave the window.
/// </summary>
/// <remarks>
/// Text editing itself is delegated to the view's editor control (registered via
/// <see cref="EditorBoxProvider"/>) through the <see cref="IEditorBox"/> abstraction,
/// exactly like <c>TerminalViewModel.InputBoxProvider</c>: the VM owns file IO,
/// dirty tracking and clipboard verbs, while the control owns caret and selection
/// state and syntax highlighting. When no box is registered the commands fall back
/// to whole-text semantics so the VM stays testable headless.
/// </remarks>
public sealed partial class CodeEditorViewModel : ObservableObject, ITabViewModel
{
    /// <summary>Refuses files larger than this, so a stray multi-GB log cannot OOM the tab.</summary>
    private const long MaxFileBytes = 8 * 1024 * 1024;

    /// <summary>BOMs recognised on load, so saving preserves the file's existing encoding.</summary>
    private static readonly (byte[] Preamble, Encoding Encoding)[] KnownPreambles =
    [
        (Encoding.UTF8.GetPreamble(), Encoding.UTF8),

        // UTF-32 LE before UTF-16 LE: the UTF-32 BOM (FF FE 00 00) starts with the
        // UTF-16 BOM (FF FE), so the shorter one must not be tested first or a
        // UTF-32 file would be decoded as UTF-16 and save back as garbage.
        (Encoding.UTF32.GetPreamble(), Encoding.UTF32),
        (Encoding.Unicode.GetPreamble(), Encoding.Unicode),
        (Encoding.BigEndianUnicode.GetPreamble(), Encoding.BigEndianUnicode),
    ];

    private string _savedText = string.Empty;
    private Encoding _encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Full path of the open file, or null for a fresh untitled buffer.</summary>
    [ObservableProperty]
    private string? _filePath;

        /// <summary>Live buffer text. Two-way bound to the view's editor control.</summary>
    [ObservableProperty]
    private string _text = string.Empty;

                /// <summary>Word-wrap toggle, bound to the view's TextEditor.</summary>
        [ObservableProperty]
    private bool _wordWrap = true;

    /// <summary>Editor font size in device-independent pixels. Bound to the view's TextEditor.</summary>
    [ObservableProperty]
    private double _fontSize = 13;

    /// <summary>
    /// Syntax-highlighting definition resolved from <see cref="FilePath"/>'s extension,
    /// or null for plain-text types (.txt, .log, etc.). Bound to the view's TextEditor.
    /// </summary>
    [ObservableProperty]
    private IHighlightingDefinition? _syntaxHighlighting;

    /// <summary>Status-bar line: file state, encoding and size.</summary>
    [ObservableProperty]
    private string _statusText = "New file. Press Ctrl+S to save.";

    /// <summary>Present only to satisfy <see cref="ITabViewModel"/>; the editor has no search box of its own.</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>True while a load or save is in flight, so the shell can show a stop affordance.</summary>
    [ObservableProperty]
    private bool _isBusy;

    public bool CanNewFile => false;
    public bool CanNewFolder => false;
    public bool CanCut => true;
    public bool CanCopy => true;
    public bool CanPaste => true;
    public bool CanDelete => true;
    public bool CanRename => false;
    public bool CanProperties => false;
    public bool CanSelectAll => true;
    public bool CanSelectNone => true;
    public bool CanInvertSelect => true;

    public double? LoadProgress => null;
    public bool IsFileBrowser => false;
    public bool CanGoBack => false;
    public bool CanGoForward => false;

    public string IconKey => "Icon.Xp.ConfigFile";

    public string Title => (IsDirty ? "* " : "") + DisplayName;

    public string Location => FilePath ?? "Untitled";

    /// <summary>True when the buffer differs from what is on disk (or a new buffer has text).</summary>
    public bool IsDirty => !string.Equals(Text, _savedText, StringComparison.Ordinal);

        /// <summary>Provides the live editor control. Set by the view; null in tests/headless.</summary>
    public Func<IEditorBox?>? EditorBoxProvider { get; set; }

    /// <summary>
    /// Yes/No prompt for destructive verbs. Wired by the view to an XP-style dialog;
    /// unset (tests, headless) means proceed.
    /// </summary>
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>
    /// Save-As destination picker. Wired by the view to the platform file picker;
    /// unset means Save As reports that no destination was chosen.
    /// </summary>
    public Func<string?, Task<string?>>? RequestSaveAsPath { get; set; }

    /// <summary>
    /// Discard-changes prompt used by tab close. Wired by the view to an XP-style
    /// Yes/No dialog; unset (tests, headless) means discard.
    /// </summary>
    public Func<Task<bool>>? ConfirmDiscardAsync { get; set; }

    public CodeEditorViewModel(string? initialPath = null)
    {
        if (!string.IsNullOrWhiteSpace(initialPath))
        {
            _ = OpenFileAsync(initialPath);
        }
    }

    private string DisplayName =>
        string.IsNullOrWhiteSpace(FilePath) ? "Untitled" : Path.GetFileName(FilePath);

    partial void OnTextChanged(string value) => RefreshDerived();

    partial void OnFilePathChanged(string? value) => RefreshDerived();

        private void RefreshDerived()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Location));
        OnPropertyChanged(nameof(IsDirty));
        SyntaxHighlighting = SyntaxHighlightingResolver.Resolve(
            Path.GetExtension(FilePath ?? string.Empty));
        SaveCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
    }

    // ---- ITabViewModel -------------------------------------------------------

    /// <summary>Opens a file path, treating the bar text as a file to load.</summary>
    void ITabViewModel.Navigate(string location) => _ = OpenFileAsync(location);

    void ITabViewModel.GoBack()
    {
        // No history: the editor owns one buffer.
    }

    void ITabViewModel.GoForward()
    {
        // No history: the editor owns one buffer.
    }

    /// <summary>Reloads the open file from disk, prompting first when the buffer is dirty.</summary>
    void ITabViewModel.Reload() => _ = RevertAsync();

    void ITabViewModel.Stop()
    {
        // Loads and saves are short synchronous disk reads; nothing cancellable is in flight.
    }

    // ---- File IO -------------------------------------------------------------

    /// <summary>Loads <paramref name="path"/> into the buffer, detecting BOM encoding.</summary>
    public async Task OpenFileAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            StatusText = "Enter a file path in the address bar to open it.";
            return;
        }

        var fullPath = path.Trim().Trim('"');

        if (Directory.Exists(fullPath))
        {
            StatusText = $"'{fullPath}' is a folder, not a file.";
            return;
        }

        if (!File.Exists(fullPath))
        {
            StatusText = $"Cannot find '{fullPath}'.";
            return;
        }

        FileInfo info;
        try
        {
            info = new FileInfo(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Could not open '{fullPath}': {ex.Message}";
            return;
        }

        if (info.Length > MaxFileBytes)
        {
            StatusText =
                $"'{info.Name}' is {info.Length / 1024 / 1024:N0} MB, over the "
                + $"{MaxFileBytes / 1024 / 1024:N0} MB in-app limit. Left to an external program.";
            return;
        }

        IsBusy = true;
        try
        {
            var (text, encoding) = await Task.Run(() => ReadAllTextDetectingEncoding(fullPath));
            _encoding = encoding;
            _savedText = text;
            FilePath = fullPath;
            Text = text;
            StatusText = $"Editing {info.Name} - {encoding.WebName}, {text.Length:N0} characters.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Could not open '{fullPath}': {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Writes the buffer back to the open file.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(FilePath))
        {
            await SaveAsAsync(null);
            return;
        }

        await WriteToAsync(FilePath);
    }

    private bool CanSave() => IsDirty || string.IsNullOrWhiteSpace(FilePath);

    /// <summary>Writes the buffer to a picked destination and adopts it as the open file.</summary>
    [RelayCommand]
    private async Task SaveAsAsync(string? path)
    {
        var destination = path;

        if (string.IsNullOrWhiteSpace(destination) && RequestSaveAsPath is not null)
        {
            destination = await RequestSaveAsPath(DisplayName);
        }

        if (string.IsNullOrWhiteSpace(destination))
        {
            StatusText = "Save As cancelled: no destination chosen.";
            return;
        }

        destination = destination.Trim().Trim('"');
        await WriteToAsync(destination);
        FilePath = destination;
    }

    private async Task WriteToAsync(string destination)
    {
        IsBusy = true;
        try
        {
            var encoding = _encoding;
            var text = Text;
            await Task.Run(() => File.WriteAllText(destination, text, encoding));
            _savedText = text;
            RefreshDerived();
            StatusText = $"Saved {Path.GetFileName(destination)} - {text.Length:N0} characters.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Could not save '{destination}': {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Discards buffer edits by reloading from disk, after a Yes/No prompt.</summary>
    [RelayCommand(CanExecute = nameof(CanRevert))]
    private async Task RevertAsync()
    {
        if (!IsDirty)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(FilePath))
        {
            Text = string.Empty;
            StatusText = "Discarded the untitled buffer.";
            return;
        }

        var confirm =
            ConfirmAsync?.Invoke(
                $"Discard unsaved changes to '{DisplayName}' and reload from disk?",
                "Revert"
            ) ?? Task.FromResult(true);

        if (!await confirm)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var (disk, _) = await Task.Run(() => ReadAllTextDetectingEncoding(FilePath));
            _savedText = disk;
            Text = disk;
            StatusText = $"Reverted to the on-disk copy of {DisplayName}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Could not reload '{DisplayName}': {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRevert() => IsDirty;

    /// <summary>
    /// Shell close-guard: true when the tab may close. Prompts on dirty buffers and
    /// returns false when the user cancels.
    /// </summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        if (!IsDirty)
        {
            return true;
        }

        if (ConfirmDiscardAsync is null)
        {
            return true;
        }

        return await ConfirmDiscardAsync();
    }

    private static (string Text, Encoding Encoding) ReadAllTextDetectingEncoding(string path)
    {
        var bytes = File.ReadAllBytes(path);

        foreach (var (preamble, encoding) in KnownPreambles)
        {
            if (preamble.Length > 0
                && bytes.Length >= preamble.Length
                && bytes.AsSpan(0, preamble.Length).SequenceEqual(preamble))
            {
                return (encoding.GetString(bytes, preamble.Length, bytes.Length - preamble.Length), encoding);
            }
        }

        // No BOM: assume UTF-8, which round-trips plain-ASCII configs byte-for-byte.
        return (Encoding.UTF8.GetString(bytes), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    // ---- Text editing (driven by the shell's Edit menu) ----
    //
        // The view registers the live editor control via EditorBoxProvider (set in the code-behind
    // on DataContextChanged); when no box is registered the commands fall back to the
    // Text string with whole-text semantics.

    /// <summary>Cuts the selection (or all text headless) to the clipboard.</summary>
    [RelayCommand]
    private async Task CutAsync()
    {
        var box = EditorBoxProvider?.Invoke();
        if (box is null)
        {
            await CopyTextAsync(Text);
            Text = string.Empty;
            return;
        }

        var selected = box.SelectedText;
        if (string.IsNullOrEmpty(selected))
        {
            return;
        }

        await CopyTextAsync(selected);
        var caret = box.SelectionStart;
        box.Text = (box.Text ?? string.Empty).Remove(caret, selected.Length);
        box.CaretIndex = caret;
    }

    /// <summary>Copies the selection (or all text headless) to the clipboard.</summary>
    [RelayCommand]
    private async Task CopyAsync()
    {
        var box = EditorBoxProvider?.Invoke();
        var selected = box?.SelectedText;
        await CopyTextAsync(string.IsNullOrEmpty(selected) ? box?.Text ?? Text : selected);
    }

    /// <summary>Pastes clipboard text at the caret, replacing any selection.</summary>
    [RelayCommand]
    private async Task PasteAsync()
    {
        var text = await ReadClipboardTextAsync();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var box = EditorBoxProvider?.Invoke();
        if (box is null)
        {
            Text += text;
            return;
        }

        var caret = box.CaretIndex;
        var current = box.Text ?? string.Empty;

        if (!string.IsNullOrEmpty(box.SelectedText))
        {
            current = current.Remove(box.SelectionStart, box.SelectedText.Length);
            caret = box.SelectionStart;
        }

        box.Text = current.Insert(caret, text);
        box.CaretIndex = caret + text.Length;
    }

    /// <summary>Deletes the selection, or the character after the caret.</summary>
    [RelayCommand]
    private async Task DeleteAsync()
    {
        var box = EditorBoxProvider?.Invoke();
        if (box is null)
        {
            if (ConfirmAsync is not null
                && !await ConfirmAsync("Delete all text in the buffer?", "Delete"))
            {
                return;
            }

            Text = string.Empty;
            return;
        }

        if (!string.IsNullOrEmpty(box.SelectedText))
        {
            var caret = box.SelectionStart;
            box.Text = (box.Text ?? string.Empty).Remove(caret, box.SelectedText.Length);
            box.CaretIndex = caret;
            return;
        }

        var current = box.Text ?? string.Empty;
        if (box.CaretIndex < current.Length)
        {
            box.Text = current.Remove(box.CaretIndex, 1);
        }
    }

    /// <summary>Selects all text in the editor.</summary>
    [RelayCommand]
    private void SelectAll()
    {
        EditorBoxProvider?.Invoke()?.SelectAll();
    }

    private static async Task CopyTextAsync(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var clipboard = ClipboardProvider.Current;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    private static async Task<string?> ReadClipboardTextAsync()
    {
        var clipboard = ClipboardProvider.Current;
        if (clipboard is null)
        {
            return null;
        }

        try
        {
#pragma warning disable CS0618 // IClipboard.GetTextAsync is obsolete but the replacement
                                // lives in Avalonia.Controls; the deprecated member still
                                // works on every supported Avalonia version.
            return await clipboard.GetTextAsync();
#pragma warning restore CS0618
        }
        catch (Exception)
        {
            return null;
        }
    }
}