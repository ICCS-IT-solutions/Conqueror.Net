using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Conqueror.Net.Core.Tabs;
using Conqueror.Net.Core.Terminal;

namespace Conqueror.Net.FileBrowserUi.ViewModels;

/// <summary>
/// A terminal tab. Owns an <see cref="ITerminalBackend"/> and exposes its output as a bounded
/// list of lines, which is what the view renders.
/// </summary>
/// <remarks>
/// Output arrives from the process's redirected streams on a thread-pool thread, so it is
/// marshalled through <see cref="PostTo"/> before touching any bound collection.
/// </remarks>
public sealed partial class TerminalViewModel : ObservableObject, ITabViewModel, IDisposable
{
    /// <summary>
    /// Ceiling on retained output lines. A long build would otherwise grow the list without
    /// bound; the oldest lines are dropped once this is reached.
    /// </summary>
    private const int MaxLines = 5000;

    private readonly ITerminalBackend _backend;

    private CancellationTokenSource? _cts;

    private int _exitCode;

    public TerminalViewModel(string? workingDirectory = null, ITerminalBackend? backend = null)
    {
        // Defaulting here rather than in the view keeps the backend swappable: a future PTY
        // implementation is chosen by passing one in, with no change to this class.
        _backend = backend ?? new PipedProcessBackend(workingDirectory);

        _backend.OutputReceived += OnOutputReceived;
        _backend.Exited += OnExited;

        Title = _backend.ShellName;
    }

    //Capabilities: cut, copy, paste, delete, select all.
    public bool CanNewFile => false;
    public bool CanNewFolder => false;
    public bool CanCut => true;
    public bool CanCopy => true;
    public bool CanPaste => true;
    public bool CanDelete => true;
    public bool CanRename => false;
    public bool CanProperties => false;
    public bool CanSelectAll => true;

    /// <summary>Rendered output, oldest first.</summary>
    public ObservableCollection<string> Lines { get; } = [];

    /// <summary>The buffered input line. Cleared once its contents have been sent.</summary>
    [ObservableProperty]
    private string _input = string.Empty;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isExited;

    /// <summary>
    /// Tab caption. Settable because <c>ITabViewModel</c> declares it as a property; the
    /// terminal renames itself once the shell it actually launched is known.
    /// </summary>
    [ObservableProperty]
    private string _title;

    /// <summary>
    /// Present only to satisfy <c>ITabViewModel</c>. A terminal has no search box of its own -
    /// the address bar sends text here as a command instead.
    /// </summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Exit code of the finished process, or null while it is still alive.</summary>
    public int? ExitCode => _exitCode;

    /// <summary>The black command-prompt window artwork.</summary>
    public string IconKey => "Icon.Xp.CommandPrompt";

    public bool IsFileBrowser => false;

    public string Location => IsRunning ? _backend.ShellName : $"exited ({_exitCode})";

    public bool CanGoBack => false;

    public bool CanGoForward => false;

    public bool IsBusy => false;

    public double? LoadProgress => null;

    public string StatusText =>
        IsRunning
            ? $"Running {_backend.ShellName} - input is buffered below"
            : $"Process exited with code {_exitCode}";

    // ---- ITabViewModel -------------------------------------------------------

    /// <summary>
    /// Runs typed text as a shell command. Fire-and-forget because the tab interface cannot
    /// await; the view-model marshals its own state changes back to the UI thread.
    /// </summary>
    void ITabViewModel.Navigate(string location) => _ = SendInputAsync(location);

    void ITabViewModel.GoBack()
    {
        // No history: the shell owns that.
    }

    void ITabViewModel.GoForward()
    {
        // No history: the shell owns that.
    }

    /// <summary>
    /// Restarts the shell. Fire-and-forget from the interface's void <c>Reload</c>: the
    /// interface cannot await, and a restart that outlives the call is exactly what is wanted.
    /// </summary>
    void ITabViewModel.Reload() => _ = RestartAsync();

    void ITabViewModel.Stop() => _cts?.Cancel();

    /// <summary>
    /// Starts the shell. Called by the view once it is attached, because a tab is constructed
    /// before its view and a process must not outlive that gap.
    /// </summary>
    public async Task StartAsync()
    {
        if (IsRunning)
        {
            return;
        }

        _cts = new CancellationTokenSource();

        try
        {
            await _backend.StartAsync(_cts.Token);
            PostTo(() =>
            {
                IsRunning = true;
                IsExited = false;
                _exitCode = 0;
            });
        }
        catch (Exception ex)
        {
            // A missing or unlaunchable shell must show up as text in the tab, not as an
            // exception thrown on a background thread with nothing left to display it.
            Append($"Could not start the shell: {ex.Message}");
            PostTo(() =>
            {
                IsRunning = false;
                IsExited = true;
            });
        }
    }

    /// <summary>Echoes the given line into the transcript and hands it to the shell.</summary>
    [RelayCommand]
    private async Task SendInputAsync(string? text = null)
    {
        var line = text ?? Input;

        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        Input = string.Empty;

        // Echo locally: a piped shell does not echo what it is fed, so without this the
        // transcript would not show what the user typed.
        Append($"{_backend.ShellName}> {line}");

        if (!IsRunning)
        {
            return;
        }

        try
        {
            await _backend.SendLineAsync(line, _cts?.Token ?? CancellationToken.None);
        }
        catch (Exception ex)
            when (ex is InvalidOperationException or IOException or ObjectDisposedException)
        {
            Append($"Input was not delivered: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task RestartAsync()
    {
        await StopAsync();
        Lines.Clear();
        await StartAsync();
    }

    public async Task StopAsync()
    {
        if (!IsRunning)
        {
            return;
        }

        try
        {
            await _backend.StopAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            Append($"Could not stop the shell cleanly: {ex.Message}");
        }
    }

    private void OnOutputReceived(string text) => PostTo(() => Append(text));

    private void OnExited(int code)
    {
        PostTo(() =>
        {
            _exitCode = code;
            IsRunning = false;
            IsExited = true;
            OnPropertyChanged(nameof(ExitCode));
            OnPropertyChanged(nameof(Location));
            OnPropertyChanged(nameof(StatusText));
        });
    }

    /// <summary>Appends text, splitting on newlines because the view renders whole lines.</summary>
    private void Append(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');

            // A trailing newline produces one empty final element; only skip genuine blanks.
            if (trimmed.Length == 0 && Lines.Count > 0)
            {
                continue;
            }

            Lines.Add(trimmed);
        }

        // Drop from the front once the cap is passed, so a runaway build cannot exhaust memory.
        while (Lines.Count > MaxLines)
        {
            Lines.RemoveAt(0);
        }
    }

    /// <summary>
    /// Marshals to the UI thread when there is one. The process events fire on pool threads,
    /// and touching a bound collection from there is not allowed.
    /// </summary>
    private static void PostTo(Action action)
    {
        var dispatcher = Avalonia.Threading.Dispatcher.UIThread;

        if (dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Post(action);
    }

    public void Dispose()
    {
        _backend.OutputReceived -= OnOutputReceived;
        _backend.Exited -= OnExited;
        _cts?.Dispose();
        _backend.Dispose();
    }
}