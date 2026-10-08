using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Conqueror.Net.FileBrowserUi.Models;

namespace Conqueror.Net.Core.Terminal;

/// <summary>
/// Linux-specific terminal backend using pseudo-terminal (PTY) for proper TTY emulation.
/// </summary>
public sealed class PtyProcessBackend : ITerminalBackend
{
    private const int MaxBufferSize = 4096;
    private readonly string? _initialWorkingDirectory;
    private string? _currentWorkingDirectory;
    private string? _resolvedShell;
    private string? _resolvedArgs;
    
    private int _masterFd = -1;
    private int _slaveFd = -1;
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

    private int ForkPty(out int master, out int slave, out string shellPath, out string[] shellArgs)
    {
        if (Openpty(out master, out slave, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) != 0)
            throw new IOException("Failed to open PTY");
            
        var pid = fork();
        if (pid < 0)
        {
            close(master);
            close(slave);
            throw new IOException("Fork failed");
        }
        
        shellPath = _resolvedShell ?? "/bin/bash";
        shellArgs = string.IsNullOrEmpty(_resolvedArgs) 
            ? new[] { "-i" } 
            : _resolvedArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            
        return pid;
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
            // Fork and setup PTY (Linux-specific)
            var pid = ForkPty(out _masterFd, out _slaveFd, out var shellPath, out var shellArgs);
            
            if (pid == 0) // Child process
            {
                // Setup child process
                SetupChildProcess(shellPath, shellArgs);
                // Child never returns from execvp
                Environment.Exit(1);
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
            // If PTY fails, we'll let the caller handle it
            System.Diagnostics.Debug.WriteLine($"PTY backend failed to start: {ex.Message}");
            throw;
        }
        
        return Task.CompletedTask;
    }

    [DllImport("libutil", EntryPoint = "openpty", SetLastError = true)]
    private static extern int Openpty(out int amaster, out int aslave, 
                                      IntPtr name, IntPtr termp, IntPtr winp);
                                      
    [DllImport("libc", SetLastError = true)]
    private static extern int fork();
                                      
    [DllImport("libc", SetLastError = true)]
    private static extern int execvp(string file, string[] argv);
                                      
    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);
                                      
    [DllImport("libc", SetLastError = true)]
    private static extern int read(int fd, IntPtr buf, int count);
                                      
    [DllImport("libc", SetLastError = true)]
    private static extern int write(int fd, IntPtr buf, int count);

    private void SetupChildProcess(string shellPath, string[] shellArgs)
    {
        // Close master fd in child
        close(_masterFd);
        
        // Make slave the controlling terminal
        if (setsid() < 0) Environment.Exit(1);
        if (ioctl(_slaveFd, TIOCSCTTY, 0) < 0) Environment.Exit(1);
        
        // Redirect stdio to slave
        dup2(_slaveFd, STDIN_FILENO);
        dup2(_slaveFd, STDOUT_FILENO);
        dup2(_slaveFd, STDERR_FILENO);
        
        if (_slaveFd > STDERR_FILENO) close(_slaveFd);
        
        // Set up environment
        var env = new List<string>(Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .Select(kvp => $"{kvp.Key}={kvp.Value}"));
        env.Add("TERM=xterm-256color");
        env.Add("COLORTERM=truecolor");
        env.Add("FORCE_COLOR=1");
        
        // Execute shell
        var argv = new List<string> { shellPath };
        argv.AddRange(shellArgs);
        execvp(shellPath, argv.ToArray());
        
        // If we get here, exec failed
        Environment.Exit(1);
    }

    private void ReadOutputLoop()
    {
        var buffer = new byte[MaxBufferSize];
        while (_running && _masterFd >= 0)
        {
            try
            {
                var bytesRead = read(_masterFd, Marshal.UnsafeAddrOfPinnedArrayElement(buffer, 0), MaxBufferSize);
                if (bytesRead > 0)
                {
                    var text = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    OutputReceived?.Invoke(text);
                }
                else if (bytesRead == 0)
                {
                    break; // EOF
                }
                else if (Marshal.GetLastWin32Error() != Errno.EINTR)
                {
                    break; // Real error
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

    public unsafe Task SendLineAsync(string line, CancellationToken cancellationToken = default)
    {
        if (!IsRunning) throw new InvalidOperationException("PTY process not running");
        
        var lineWithNewline = line + Environment.NewLine;
        var bytes = Encoding.UTF8.GetBytes(lineWithNewline);
        
        fixed (byte* pBytes = bytes)
        {
            var ptr = new IntPtr(pBytes);
            var remaining = bytes.Length;
            while (remaining > 0 && _running)
            {
                var written = write(_masterFd, ptr, remaining);
                if (written < 0)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == Errno.EINTR) continue;
                    if (err == Errno.EIO) break; // Device/I/O error - likely child exited
                    throw new IOException($"Failed to write to PTY: error {err}");
                }
                ptr = new IntPtr(ptr.ToInt64() + written);
                remaining -= written;
            }
        }
        
        return Task.CompletedTask;
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
                // Child should have exited by now
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
        if (_slaveFd >= 0) { close(_slaveFd); _slaveFd = -1; }
        _running = false;
        _outputThread = null;
    }
    
    // Constants and additional P/Invoke would go here...
    private const int STDIN_FILENO = 0;
    private const int STDOUT_FILENO = 1;
    private const int STDERR_FILENO = 2;
    private const int TIOCSCTTY = 0x540E;
    private static class Errno { public const int EINTR = 4; public const int EIO = 5; }
    [DllImport("libc")] private static extern int setsid();
    [DllImport("libc")] private static extern int ioctl(int fd, int request, int arg);
    [DllImport("libc")] private static extern int dup2(int oldfd, int newfd);
}