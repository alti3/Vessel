using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text;
using Vessel.Application.Processes;
using Vessel.Application.Ssh;
using Vessel.Application.Terminals;
using Vessel.Domain;
using Vessel.Domain.Servers;
using Vessel.Domain.Terminals;
using Vessel.Infrastructure.Processes.Pty;
using Vessel.Infrastructure.Servers;
using Vessel.Infrastructure.Ssh;

namespace Vessel.Infrastructure.Processes;

/// <summary>
///     Terminal bridge with a real pseudo-terminal (SRV-06). The PTY pair is created here; the child is started through
///     <see cref="IProcessRunner" /> as <c>sh -c 'exec setsid -c -w "$@" &lt;pts &gt;pts 2&gt;&amp;1'</c>, so it runs in
///     a new session with the PTY slave as its controlling terminal. Resizing sets the window size on the master
///     (<c>TIOCSWINSZ</c>), which delivers <c>SIGWINCH</c> to the foreground program — also to <c>ssh -tt</c>, which
///     forwards it to the remote PTY. Commands are structured: local targets run the argument vector directly, SSH
///     targets run it as the POSIX-quoted remote command of <c>ssh -tt</c> with the server's hardened connection
///     (team key, pinned host key). The child environment is the minimal allowlist of
///     <see cref="InteractiveTerminalProcessBridge.BuildEnvironment" />.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class PtyTerminalBridge(IProcessRunner processRunner, IServerSshConnectionFactory? connections = null)
    : ITerminalProcessBridge, IAsyncDisposable
{
    /// <summary>Runs <c>"$@"</c> as a session leader whose stdio and controlling terminal is the PTY slave <c>$0</c>.</summary>
    public const string SessionScript = "exec setsid -c -w \"$@\" <\"$0\" >\"$0\" 2>&1";

    private static readonly TimeSpan StopGracePeriod = TimeSpan.FromMilliseconds(500);

    private readonly ConcurrentDictionary<TerminalSessionId, PtySession> _sessions = new();

    public bool SupportsRemoteAndContainerSessions => true;

    public bool IsOpen(TerminalSessionId sessionId)
    {
        return _sessions.ContainsKey(sessionId);
    }

    public async Task OpenAsync(
        TerminalBridgeOpenRequest request,
        Func<TerminalOutputChunk, CancellationToken, Task> onOutput,
        Func<TerminalSessionId, string?, CancellationToken, Task> onExit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!NativePty.IsSupported)
            throw new InvalidOperationException("PTY terminals require Linux on x64 or arm64.");
        if (_sessions.ContainsKey(request.SessionId))
            throw new InvalidOperationException("Terminal session is already open.");

        SshConnectionLease? lease = null;
        List<string> program;
        if (request.TargetConnection == ServerConnectionType.Ssh)
        {
            if (request.Remote is null || connections is null)
                throw new InvalidOperationException(TerminalTargetPolicy.RemoteNotSupportedMessage);
            lease = await connections.AcquireAsync(request.Remote, null, cancellationToken);
            string? remoteCommand = request.Arguments is null
                ? null
                : PosixShell.Join([request.Command, .. request.Arguments]);
            program = ["ssh", .. SshArguments.ForSsh(lease.Options, remoteCommand, allocateTty: true)];
        }
        else if (Server.IsHostLocalConnection(request.TargetConnection))
        {
            program = [request.Command, .. request.Arguments ?? []];
        }
        else
        {
            throw new InvalidOperationException(TerminalTargetPolicy.RemoteNotSupportedMessage);
        }

        int master = -1;
        int slave = -1;
        IInteractiveProcess? process = null;
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        try
        {
            (master, var slavePath) = NativePty.Open(request.Columns, request.Rows);
            slave = NativePty.OpenSlave(slavePath);
            process = await processRunner.StartInteractiveAsync(new ProcessCommand(
                "sh",
                ["-c", SessionScript, slavePath, .. program],
                Environment: InteractiveTerminalProcessBridge.BuildEnvironment(),
                Timeout: request.MaxLifetime,
                OutputMode: ProcessOutputMode.None,
                TerminationPolicy: new ProcessTerminationPolicy(TimeSpan.FromMilliseconds(100), true, true),
                InheritEnvironment: false), cancellationToken);
        }
        catch
        {
            NativePty.Close(slave);
            NativePty.Close(master);
            lifetime.Dispose();
            if (lease is not null) await lease.DisposeAsync();
            throw;
        }

        lifetime.CancelAfter(request.MaxLifetime);
        var session = new PtySession(request.SessionId, master, slave, process, lifetime, lease);
        if (!_sessions.TryAdd(request.SessionId, session))
        {
            await session.StopAsync();
            session.ReleaseMaster();
            throw new InvalidOperationException("Terminal session is already open.");
        }

        session.Reader = Task.Factory.StartNew(
            () => PumpAsync(session, onOutput, onExit),
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default).Unwrap();
    }

    public Task SendInputAsync(TerminalSessionId sessionId, string data, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out PtySession? session))
            throw new InvalidOperationException("Terminal session is not open.");
        if (!string.IsNullOrEmpty(data))
            session.Write(Encoding.UTF8.GetBytes(data));

        return Task.CompletedTask;
    }

    public Task ResizeAsync(TerminalBridgeResizeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_sessions.TryGetValue(request.SessionId, out PtySession? session))
            throw new InvalidOperationException("Terminal session is not open.");
        session.Resize(request.Columns, request.Rows);
        return Task.CompletedTask;
    }

    public async Task CloseAsync(TerminalSessionId sessionId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out PtySession? session)) return;
        session.Closing = true;
        await session.StopAsync();
        if (session.Reader is not null)
            await session.Reader.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
                .ContinueWith(_ => { }, TaskScheduler.Default);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (TerminalSessionId id in _sessions.Keys.ToArray())
            await CloseAsync(id);
    }

    private async Task PumpAsync(
        PtySession session,
        Func<TerminalOutputChunk, CancellationToken, Task> onOutput,
        Func<TerminalSessionId, string?, CancellationToken, Task> onExit)
    {
        var buffer = new byte[4096];
        var characters = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        Decoder decoder = Encoding.UTF8.GetDecoder();
        string? reason = null;
        try
        {
            while (!session.Lifetime.IsCancellationRequested)
            {
                var read = NativePty.Read(session.Master, buffer, 200);
                if (read > 0)
                {
                    var count = decoder.GetChars(buffer, 0, read, characters, 0, false);
                    if (count > 0)
                        await onOutput(new TerminalOutputChunk(session.Id.Value, "stdout",
                            new string(characters, 0, count), DateTimeOffset.UtcNow), CancellationToken.None);
                    continue;
                }

                if (read == 0 || session.Process.HasExited)
                    break;
            }

            if (session.Lifetime.IsCancellationRequested && !session.Closing)
                reason = "Terminal session reached its maximum lifetime or was canceled.";
            else if (!session.Closing && session.Process.HasExited && session.Process.ExitCode != 0)
                reason = $"Terminal process exited with code {session.Process.ExitCode}.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            reason = session.Closing ? null : "Terminal output stream failed.";
        }
        finally
        {
            _sessions.TryRemove(session.Id, out _);
            await session.StopAsync();
            session.ReleaseMaster();
            await onExit(session.Id, reason, CancellationToken.None);
        }
    }

    private sealed class PtySession(
        TerminalSessionId id,
        int master,
        int slave,
        IInteractiveProcess process,
        CancellationTokenSource lifetime,
        SshConnectionLease? lease)
    {
        private int _stopped;

        public TerminalSessionId Id { get; } = id;

        public int Master { get; } = master;

        public IInteractiveProcess Process { get; } = process;

        public CancellationTokenSource Lifetime { get; } = lifetime;

        private readonly Lock _gate = new();
        private bool _masterClosed;

        public Task? Reader { get; set; }

        public volatile bool Closing;

        public async Task StopAsync()
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 1) return;
            try
            {
                if (!Process.HasExited)
                {
                    Process.Kill(entireProcessTree: true);
                    using var wait = new CancellationTokenSource(StopGracePeriod);
                    try
                    {
                        await Process.WaitForExitAsync(wait.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // Killed; the descriptors are released below regardless.
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
            finally
            {
                // The slave first (ours), then the master: the reader has stopped or sees EIO/hang-up next.
                NativePty.Close(slave);
                await Process.DisposeAsync();
                if (lease is not null) await lease.DisposeAsync();
                Lifetime.Dispose();
            }
        }

        /// <summary>Writes input; never after the master is closed (a reused descriptor must not receive input).</summary>
        public void Write(byte[] data)
        {
            lock (_gate)
            {
                if (_masterClosed) throw new InvalidOperationException("Terminal session is not open.");
                NativePty.Write(Master, data);
            }
        }

        public void Resize(int columns, int rows)
        {
            lock (_gate)
            {
                if (_masterClosed) throw new InvalidOperationException("Terminal session is not open.");
                NativePty.Resize(Master, columns, rows);
            }
        }

        public void ReleaseMaster()
        {
            lock (_gate)
            {
                if (_masterClosed) return;
                _masterClosed = true;
                NativePty.Close(Master);
            }
        }
    }
}
