using System;
using System.Threading;
using System.Threading.Tasks;

namespace Conqueror.Net.Core.Terminal;

/// <summary>
    /// A child process the terminal tab talks to. The point of the interface is that the view and
    /// the view-model never learn which kind of shell is behind it, so a PTY-backed implementation
    /// can replace this one without either changing.
    /// </summary>
    /// <remarks>
/// The shipped <see cref="PipedProcessBackend"/> redirects standard streams, which is portable
/// but is not a real TTY: child programs see <c>isatty() == false</c> and therefore disable
/// colour, line editing and full-screen modes. That limitation is deliberate and documented
/// rather than hidden, and it is the reason this seam exists at all - a ConPTY or
/// <c>forkpty</c> backend can be added here later without the UI noticing.
/// </remarks>
public interface ITerminalBackend : IDisposable
{
    /// <summary>Display name of the shell this backend runs, shown on the tab.</summary>
    string ShellName { get; }
    /// <summary>Raised for every chunk the process writes to stdout or stderr.</summary>
    event Action<string>? OutputReceived;

    /// <summary>Raised when the process exits, carrying its exit code.</summary>
    event Action<int>? Exited;
    /// <summary>Raised when the process's working directory changes.</summary>
    event Action<string>? WorkingDirectoryChanged;

    /// <summary>True while the process is running.</summary>
    bool IsRunning { get; }

    /// <summary>Starts the process. Does not block.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Sends a line of input, appending the newline the shell expects.</summary>
    Task SendLineAsync(string line, CancellationToken cancellationToken = default);

    /// <summary>Requests termination, escalating to a kill if the shell ignores the request.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}