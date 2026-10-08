using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Conqueror.Net.Core.Terminal;

/// <summary>
/// Linux-specific terminal backend using pseudo-terminal (PTY) for proper TTY emulation.
/// Uses only libc (always present) via posix_openpt/grantpt/unlockpt/ptsname + fork/execvp.
/// No libutil.so dependency.
/// </summary>
public sealed class PtyProcessBackend : ITerminalBackend
{
    private const int MaxBufferSize = 4096;
    private readonly string? _initialWorkingDirectory;
    private string? _currentWorkingDirectory;
    private string? _resolvedShell;
    private string? _resolvedArgs;

    private int _masterFd = -1;
    private Thread? _outputThread;
    private volatile bool _running;

    public event Action<string>? OutputReceived;
    public event Action<int>? Exited;
    public event Action<string>? WorkingDirectoryChanged;

    public PtyProcessBackend(string? workingDirectory = null, string? shell = null)
    {
        _initialWorkingDirectory = workingDirectory;
        _currentWorkingDirectory = workingDirectory;

        // Resolve shell using existing registry
        (_resolvedShell, _resolvedArgs) = TerminalShellRegistry.ResolveShell(shell);
    }

    public bool IsRunning => _running;
    public string ShellName => _resolvedShell ?? TerminalShellRegistry.GetSelectedShell().DisplayName;
    public string? CurrentWorkingDirectory => _currentWorkingDirectory;
    public string? PromptFormat => TerminalShellRegistry.GetSelectedShell().PromptFormat;
    public string FormatPrompt(string workingDirectory) =>
        string.Format(TerminalShellRegistry.GetSelectedShell().PromptFormat ?? "{0}$ ", workingDirectory);

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_running) throw new InvalidOperationException("PTY process already started");

        try
        {
            // Open master PTY using posix_openpt (libc, always available)
            _masterFd = posix_openpt(O_RDWR | O_NOCTTY);
            if (_masterFd < 0)
            {
                var err = Marshal.GetLastPInvokeError();
                throw new IOException($"posix_openpt failed: errno {err}");
            }

            // Grant access to slave
            if (grantpt(_masterFd) != 0)
            {
                var err = Marshal.GetLastPInvokeError();
                close(_masterFd);
                throw new IOException($"grantpt failed: errno {err}");
            }

            // Unlock slave
            if (unlockpt(_masterFd) != 0)
            {
                var err = Marshal.GetLastPInvokeError();
                close(_masterFd);
                throw new IOException($"unlockpt failed: errno {err}");
            }

            // Get slave path
            var slavePathPtr = ptsname(_masterFd);
            if (slavePathPtr == IntPtr.Zero)
            {
                var err = Marshal.GetLastPInvokeError();
                close(_masterFd);
                throw new IOException($"ptsname failed: errno {err}");
            }
            var slavePath = Marshal.PtrToStringAnsi(slavePathPtr)!;

            // Fork child process
            var pid = fork();
            if (pid < 0)
            {
                close(_masterFd);
                throw new IOException("fork failed");
            }

            var shellPath = _resolvedShell ?? "/bin/bash";
            var shellArgs = string.IsNullOrEmpty(_resolvedArgs)
                ? new[] { "-i" }
                : _resolvedArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (pid == 0) // Child process
            {
                // Close master in child
                close(_masterFd);

                // Open slave as stdin/stdout/stderr
                var slaveFd = open(slavePath, O_RDWR);
                if (slaveFd < 0)
                {
                    Environment.Exit(1);
                }

                // Redirect stdio to slave
                dup2(slaveFd, STDIN_FILENO);
                dup2(slaveFd, STDOUT_FILENO);
                dup2(slaveFd, STDERR_FILENO);
                if (slaveFd > STDERR_FILENO)
                {
                    close(slaveFd);
                }

                // Create new session and set controlling terminal
                setsid();
                ioctl(STDIN_FILENO, TIOCSCTTY, 0);

                // Exec shell
                var argv = new List<string> { shellPath };
                argv.AddRange(shellArgs);
                argv.Add(null); // null-terminated for execvp

                var argvPtrs = argv.Select(s => s != null ? Marshal.StringToHGlobalAnsi(s) : IntPtr.Zero).ToArray();
                try
                {
                    var argvArrayPtr = Marshal.AllocHGlobal((argvPtrs.Length) * IntPtr.Size);
                    for (int i = 0; i < argvPtrs.Length; i++)
                    {
                        Marshal.WriteIntPtr(argvArrayPtr, i * IntPtr.Size, argvPtrs[i]);
                    }
                    execvp(shellPath, (IntPtr)argvArrayPtr);
                }
                finally
                {
                    foreach (var ptr in argvPtrs)
                    {
                        if (ptr != IntPtr.Zero) Marshal.FreeHGlobal(ptr);
                    }
                }

                Environment.Exit(1); // execvp failed
            }
            else // Parent process
            {
                _running = true;
                _outputThread = new Thread(ReadOutputLoop) { IsBackground = true };
                _outputThread.Start();

                // Send initial working directory
                if (!string.IsNullOrWhiteSpace(_initialWorkingDirectory) &&
                    Directory.Exists(_initialWorkingDirectory))
                {
                    _currentWorkingDirectory = _initialWorkingDirectory;
                    WorkingDirectoryChanged?.Invoke(_currentWorkingDirectory);
                }
            }
        }
        catch (Exception ex)
        {
            // If PTY fails, we will let the caller handle it
            System.Diagnostics.Debug.WriteLine($"PTY backend failed to start: {ex.Message}");
            throw;
        }

        return Task.CompletedTask;
    }

    public unsafe Task SendLineAsync(string line, CancellationToken cancellationToken = default)
    {
        if (!IsRunning) throw new InvalidOperationException("PTY process not running");

        var lineWithNewline = line + "\n";
        var bytes = Encoding.UTF8.GetBytes(lineWithNewline);

        fixed (byte* pBytes = bytes)
        {
            var ptr = new IntPtr(pBytes);
            var remaining = bytes.Length;
            while (remaining > 0 && _running)
            {
                var written = write(_masterFd, ptr, (nuint)remaining);
                if (written < 0)
                {
                    var err = Marshal.GetLastPInvokeError();
                    if (err == Errno.EINTR) continue;
                    if (err == Errno.EIO) break; // Device/I/O error - likely child exited
                    throw new IOException($"Failed to write to PTY: errno {err}");
                }
                ptr = new IntPtr(ptr.ToInt64() + written);
                remaining -= (int)written;
            }
        }

        return Task.CompletedTask;
    }

    private void ReadOutputLoop()
    {
        var buffer = new byte[MaxBufferSize];
        while (_running && _masterFd >= 0)
        {
            try
            {
                var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    var ptr = handle.AddrOfPinnedObject();
                    var bytesRead = read(_masterFd, ptr, (nuint)MaxBufferSize);
                    if (bytesRead > 0)
                    {
                        var text = Encoding.UTF8.GetString(buffer, 0, (int)bytesRead);
                        OutputReceived?.Invoke(text);
                    }
                    else if (bytesRead == 0)
                    {
                        break; // EOF
                    }
                    else
                    {
                        var err = Marshal.GetLastPInvokeError();
                        if (err != Errno.EINTR)
                        {
                            break; // Real error
                        }
                    }
                }
                finally
                {
                    handle.Free();
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                break;
            }
            catch
            {
                break;
            }

            Thread.Sleep(1);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_running) return;

        _running = false;

        try
        {
            // Close master fd to signal EOF to child
            if (_masterFd >= 0)
            {
                close(_masterFd);
                _masterFd = -1;
            }

            // Wait for child to exit (with timeout)
            var waited = 0;
            while (_running && waited < 30) // 3 second timeout
            {
                await Task.Delay(100, cancellationToken);
                waited++;
            }

            // Force cleanup if still running
            if (_running)
            {
                _running = false;
            }
        }
        finally
        {
            Cleanup();
            Exited?.Invoke(0);
        }
    }

    public void Dispose()
    {
        StopAsync(default).GetAwaiter().GetResult();
        Cleanup();
    }

    private void Cleanup()
    {
        if (_masterFd >= 0) { close(_masterFd); _masterFd = -1; }
        _running = false;
        _outputThread = null;
    }

    // Constants
    private const int STDIN_FILENO = 0;
    private const int STDOUT_FILENO = 1;
    private const int STDERR_FILENO = 2;
    private const int TIOCSCTTY = 0x540E;
    private const int O_RDWR = 0x2;
    private const int O_NOCTTY = 0x100;

    private static class Errno
    {
        public const int EINTR = 4;
        public const int EIO = 5;
    }

    // P/Invoke declarations (libc only - always available)
    [DllImport("c", EntryPoint = "posix_openpt", SetLastError = true)]
    private static extern int posix_openpt(int flags);

    [DllImport("c", EntryPoint = "grantpt", SetLastError = true)]
    private static extern int grantpt(int fd);

    [DllImport("c", EntryPoint = "unlockpt", SetLastError = true)]
    private static extern int unlockpt(int fd);

    [DllImport("c", EntryPoint = "ptsname", SetLastError = true)]
    private static extern IntPtr ptsname(int fd);

    [DllImport("c", EntryPoint = "fork", SetLastError = true)]
    private static extern int fork();

    [DllImport("c", EntryPoint = "open", SetLastError = true)]
    private static extern int open(string path, int flags);

    [DllImport("c", EntryPoint = "close", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("c", EntryPoint = "dup2", SetLastError = true)]
    private static extern int dup2(int oldfd, int newfd);

    [DllImport("c", EntryPoint = "setsid", SetLastError = true)]
    private static extern int setsid();

    [DllImport("c", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int ioctl(int fd, int request, int arg);

    [DllImport("c", EntryPoint = "execvp", SetLastError = true)]
    private static extern int execvp(string path, nint argv);

    [DllImport("c", EntryPoint = "read", SetLastError = true)]
    private static extern nint read(int fd, IntPtr buf, nuint count);

    [DllImport("c", EntryPoint = "write", SetLastError = true)]
    private static extern nint write(int fd, IntPtr buf, nuint count);
}
