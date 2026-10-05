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
    /// <summary>Shell candidates in preference order for the running OS.</summary>
    private static readonly string[] WindowsShells = ["powershell.exe", "cmd.exe"];

    private static readonly string[] UnixShells = ["/bin/sh", "/bin/bash", "/usr/bin/sh"];

    private readonly string? _workingDirectory;
    private readonly string? _shellOverride;

    private Process? _process;

    /// <summary>Shell actually launched, so the label reflects reality rather than intent.</summary>
    private string? _resolvedShell;

    public PipedProcessBackend(string? workingDirectory = null, string? shell = null)
    {
        _workingDirectory = workingDirectory;
        _shellOverride = shell;
    }

    public event Action<string>? OutputReceived;

    public event Action<int>? Exited;

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>
    /// The shell in use. Until the process starts this is the preference-order default, which
    /// is what the tab label shows; <see cref="StartAsync"/> refines it to whatever was found.
    /// </summary>
    public string ShellName => _resolvedShell ?? DescribeDefaultShell();

    /// <summary>
    /// The volume label this backend would try first. Public so the view-model can show the user
    /// which one they got, which matters when it is not the one they expected.
    /// </summary>
    public static string DescribeDefaultShell() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? WindowsShells[0] : UnixShells[0];

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_process is not null)
        {
            throw new InvalidOperationException("The terminal process has already been started.");
        }

        var (fileName, arguments) = ResolveShell();
        _resolvedShell = fileName;

        var info = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
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
        if (!string.IsNullOrWhiteSpace(_workingDirectory) && Directory.Exists(_workingDirectory))
        {
            info.WorkingDirectory = _workingDirectory;
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

    /// <summary>
    /// Picks the shell. An explicit override wins; otherwise the first candidate that actually
    /// exists, so a machine without PowerShell falls back to cmd rather than failing.
    /// </summary>
    private (string FileName, string Arguments) ResolveShell()
    {
        if (!string.IsNullOrWhiteSpace(_shellOverride))
        {
            return (_shellOverride, string.Empty);
        }

        var candidates = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? WindowsShells
            : UnixShells;

        foreach (var candidate in candidates)
        {
            if (ExistsOnPath(candidate))
            {
                // Interactive flag: without it the shell reads a script and exits immediately.
                var arguments = candidate.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase)
                    ? "-NoLogo -NoProfile -Command -"
                    : string.Empty;

                return (candidate, arguments);
            }
        }

        // Nothing found: let Process.Start report the failure with its own message.
        return (candidates[0], string.Empty);
    }

    private static bool ExistsOnPath(string fileName)
    {
        // An absolute or rooted path is used as given.
        if (Path.IsPathRooted(fileName))
        {
            return File.Exists(fileName);
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var separator = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';

        foreach (var directory in path.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(directory.Trim(), fileName)))
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is not this method's problem to report.
            }
        }

        return false;
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
        // The exit code is unreadable until the process has actually exited, and EnableRaising
        // events can fire a shade early, so it is read defensively.
        var code = 0;
        try
        {
            code = _process?.ExitCode ?? 0;
        }
        catch (InvalidOperationException)
        {
            // Still shutting down; zero is a reasonable "did not exit cleanly" signal.
        }

        Exited?.Invoke(code);
    }

    public async Task SendLineAsync(string line, CancellationToken cancellationToken = default)
    {
        if (_process is not { HasExited: false })
        {
            throw new InvalidOperationException("The terminal process is not running.");
        }

        await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken);
        await _process.StandardInput.FlushAsync(cancellationToken);
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