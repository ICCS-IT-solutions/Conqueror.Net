using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Conqueror.Net.Core.Terminal;

/// <summary>
/// Runs the platform shell with its standard streams redirected and echoed to
/// <see cref="OutputReceived"/>. Portable, dependency-free, and explicitly not a TTY.
/// </summary>
public sealed class PipedProcessBackend : ITerminalBackend
{
    private readonly string? _initialWorkingDirectory;
    private string? _shellOverride;

    private Process? _process;
    private string? _currentWorkingDirectory;

    /// <summary>Shell actually launched, so the label reflects reality rather than intent.</summary>
    private string? _resolvedShell;
    private string? _resolvedArgs;

    public PipedProcessBackend(string? workingDirectory = null, string? shell = null)
    {
        _initialWorkingDirectory = workingDirectory;
        _currentWorkingDirectory = workingDirectory;
        _shellOverride = shell;
    }
    public event Action<string>? OutputReceived;
    public event Action<int>? Exited;

    /// <summary>Raised when the working directory changes (e.g., via cd command).</summary>
    public event Action<string>? WorkingDirectoryChanged;

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>
    /// The shell in use. Until the process starts this is the preference-order default, which
    /// is what the tab label shows; <see cref="StartAsync"/> refines it to whatever was found.
    /// </summary>
    public string ShellName => _resolvedShell ?? TerminalShellRegistry.GetSelectedShell().DisplayName;

    /// <summary>Current working directory of the shell process.</summary>
    public string? CurrentWorkingDirectory => _currentWorkingDirectory;

    /// <summary>The prompt format string for the selected shell (with {0} = working directory).</summary>
    public string? PromptFormat => TerminalShellRegistry.GetSelectedShell().PromptFormat;

    /// <summary>Formats the prompt string for the given working directory.</summary>
    public string FormatPrompt(string workingDirectory)
    {
        var format = TerminalShellRegistry.GetSelectedShell().PromptFormat;
        return string.IsNullOrEmpty(format) ? string.Empty : string.Format(format, workingDirectory);
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_process is not null)
        {
            throw new InvalidOperationException("The terminal process has already been started.");
        }

        (_resolvedShell, _resolvedArgs) = TerminalShellRegistry.ResolveShell(_shellOverride);

        var info = new ProcessStartInfo
        {
            FileName = _resolvedShell,
            Arguments = _resolvedArgs,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // Start in the folder the user is looking at, so relative paths behave as they would
        // in a terminal opened there. Guarded because the folder may have been deleted.
        if (!string.IsNullOrWhiteSpace(_initialWorkingDirectory) && Directory.Exists(_initialWorkingDirectory))
        {
            info.WorkingDirectory = _initialWorkingDirectory;
            _currentWorkingDirectory = _initialWorkingDirectory;
        }

        _process = new Process { StartInfo = info, EnableRaisingEvents = true };

        _process.OutputDataReceived += OnDataReceived;
        _process.ErrorDataReceived += OnDataReceived;
        _process.Exited += OnProcessExited;

        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        return Task.CompletedTask;
    }

    private bool TryExtractDirectoryChange(string line, out string newDir)
    {
        newDir = string.Empty;
        var trimmed = line.Trim();

        // Windows: cd, chdir
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (trimmed.StartsWith("cd ", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("chdir ", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("pushd ", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("cd", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("chdir", StringComparison.OrdinalIgnoreCase))
            {
                var parts = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1)
                {
                    newDir = parts[1].Trim('"');
                    return true;
                }
            }
            if (trimmed.StartsWith("popd", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        else
        {
            // Unix: cd, pushd, popd
            if (trimmed.StartsWith("cd ", StringComparison.Ordinal) ||
                trimmed.StartsWith("pushd ", StringComparison.Ordinal) ||
                trimmed.Equals("cd", StringComparison.Ordinal) ||
                trimmed.Equals("pushd", StringComparison.Ordinal))
            {
                var parts = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1)
                {
                    newDir = parts[1].Trim('\'', '"');
                    return true;
                }
            }
            if (trimmed.StartsWith("popd", StringComparison.Ordinal))
            {
                return false;
            }
        }

        return false;
    }

    private void UpdateWorkingDirectory(string relativeOrAbsolutePath)
    {
        if (string.IsNullOrWhiteSpace(_currentWorkingDirectory))
        {
            return;
        }

        try
        {
            var newPath = Path.IsPathRooted(relativeOrAbsolutePath)
                ? relativeOrAbsolutePath
                : Path.GetFullPath(Path.Combine(_currentWorkingDirectory, relativeOrAbsolutePath));

            if (Directory.Exists(newPath) &&
                !string.Equals(newPath, _currentWorkingDirectory, StringComparison.OrdinalIgnoreCase))
            {
                _currentWorkingDirectory = newPath;
                WorkingDirectoryChanged?.Invoke(newPath);
            }
        }
        catch (Exception)
        {
            // Ignore path resolution errors
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_process is not { HasExited: false })
        {
            return;
        }

        try
        {
            // Close stdin first: a shell reading a script exits at EOF, which is a clean stop.
            _process.StandardInput.Close();
        }
        catch (IOException)
        {
            // Already closed, which is the state we wanted anyway.
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));

        try
        {
            await _process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            Kill();
        }
    }

    public async Task SendLineAsync(string line, CancellationToken cancellationToken = default)
    {
        if (_process is not { HasExited: false })
        {
            throw new InvalidOperationException("The terminal process is not running.");
        }

        // Track directory changes from cd/chdir/pushd/popd commands
        if (TryExtractDirectoryChange(line, out var newDir))
        {
            UpdateWorkingDirectory(newDir);
        }

        await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken);
        await _process.StandardInput.FlushAsync(cancellationToken);
    }

    private void OnDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is null)
        {
            return;
        }

        OutputReceived?.Invoke(e.Data + Environment.NewLine);
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        var code = 0;
        try
        {
            code = _process?.ExitCode ?? 0;
        }
        catch (InvalidOperationException)
        {
        }

        Exited?.Invoke(code);
    }

    private void Kill()
    {
        try
        {
            _process?.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
            when (ex
                is InvalidOperationException
                    or NotSupportedException
                    or System.ComponentModel.Win32Exception
            )
        {
            // Exited between the check and the kill, or the OS refused. Nothing left to do.
        }
    }

    public void Dispose()
    {
        if (_process is null)
        {
            return;
        }

        _process.OutputDataReceived -= OnDataReceived;
        _process.ErrorDataReceived -= OnDataReceived;
        _process.Exited -= OnProcessExited;

        // A terminal outlives its tab often enough that a live shell must not survive the view.
        Kill();

        _process.Dispose();
        _process = null;
    }
}


