using System.Collections.Concurrent;
using Vessel.Application.Processes;
using Vessel.Application.Terminals;
using Vessel.Domain;
using Vessel.Domain.Terminals;

namespace Vessel.Infrastructure.Processes;

public sealed class InteractiveTerminalProcessBridge(IProcessRunner processRunner) : ITerminalProcessBridge, IAsyncDisposable
{
    private readonly ConcurrentDictionary<TerminalSessionId, TerminalProcessHandle> _sessions = new();

    public async Task OpenAsync(
        TerminalBridgeOpenRequest request,
        Func<TerminalOutputChunk, CancellationToken, Task> onOutput,
        Func<TerminalSessionId, string?, CancellationToken, Task> onExit,
        CancellationToken cancellationToken = default)
    {
        if (request.TargetType == TerminalTargetType.Container)
            throw new InvalidOperationException("Container terminal sessions require a reviewed docker exec PTY bridge.");

        List<string> arguments = [];
        if (OperatingSystem.IsWindows() && request.Command.EndsWith("pwsh", StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add("-NoLogo");
            arguments.Add("-NoExit");
            arguments.Add("-Command");
            arguments.Add("-");
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(request.MaxLifetime);

        var command = new ProcessCommand(
            request.Command,
            arguments,
            Timeout: request.MaxLifetime,
            OutputMode: ProcessOutputMode.None,
            TerminationPolicy: new ProcessTerminationPolicy(TimeSpan.FromMilliseconds(100), true, true));

        IInteractiveProcess process;
        try
        {
            process = await processRunner.StartInteractiveAsync(command, cancellationToken);
        }
        catch
        {
            cts.Dispose();
            throw;
        }

        var handle = new TerminalProcessHandle(process, cts);
        if (!_sessions.TryAdd(request.SessionId, handle))
        {
            await process.DisposeAsync();
            cts.Dispose();
            throw new InvalidOperationException("Terminal session is already open.");
        }

        _ = PumpAsync(request.SessionId, process.StandardOutput, "stdout", onOutput, cts.Token);
        _ = PumpAsync(request.SessionId, process.StandardError, "stderr", onOutput, cts.Token);
        _ = WaitForExitAsync(request.SessionId, process, onExit, cts);
    }

    public async Task SendInputAsync(
        TerminalSessionId sessionId,
        string data,
        CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out TerminalProcessHandle? handle))
            throw new InvalidOperationException("Terminal session is not open.");

        await handle.Process.StandardInput.WriteAsync(data.AsMemory(), cancellationToken);
        await handle.Process.StandardInput.FlushAsync(cancellationToken);
    }

    public Task ResizeAsync(TerminalBridgeResizeRequest request, CancellationToken cancellationToken = default)
    {
        if (!_sessions.ContainsKey(request.SessionId))
            throw new InvalidOperationException("Terminal session is not open.");

        return Task.CompletedTask;
    }

    public async Task CloseAsync(TerminalSessionId sessionId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryRemove(sessionId, out TerminalProcessHandle? handle)) return;

        await StopAsync(handle, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (TerminalProcessHandle handle in _sessions.Values)
            await StopAsync(handle, CancellationToken.None);

        _sessions.Clear();
    }

    private static async Task PumpAsync(
        TerminalSessionId sessionId,
        StreamReader reader,
        string stream,
        Func<TerminalOutputChunk, CancellationToken, Task> onOutput,
        CancellationToken cancellationToken)
    {
        var buffer = new char[2048];
        while (!cancellationToken.IsCancellationRequested)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read <= 0) break;

            await onOutput(
                new TerminalOutputChunk(sessionId.Value, stream, new string(buffer, 0, read),
                    DateTimeOffset.UtcNow),
                cancellationToken);
        }
    }

    private async Task WaitForExitAsync(
        TerminalSessionId sessionId,
        IInteractiveProcess process,
        Func<TerminalSessionId, string?, CancellationToken, Task> onExit,
        CancellationTokenSource cts)
    {
        string? reason = null;
        try
        {
            await process.WaitForExitAsync(cts.Token);
            if (process.ExitCode != 0)
                reason = $"Terminal process exited with code {process.ExitCode}.";
        }
        catch (OperationCanceledException)
        {
            reason = "Terminal session reached its maximum lifetime or was canceled.";
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        finally
        {
            _sessions.TryRemove(sessionId, out _);
            await onExit(sessionId, reason, CancellationToken.None);
            await process.DisposeAsync();
            cts.Dispose();
        }
    }

    private static async Task StopAsync(TerminalProcessHandle handle, CancellationToken cancellationToken)
    {
        try
        {
            handle.Cancellation.Cancel();
            if (!handle.Process.HasExited)
            {
                handle.Process.StandardInput.Close();
                await Task.Delay(TimeSpan.FromMilliseconds(100), CancellationToken.None);
            }

            if (!handle.Process.HasExited)
                handle.Process.Kill(entireProcessTree: true);
        }
        finally
        {
            await handle.Process.DisposeAsync();
            handle.Cancellation.Dispose();
        }
    }

    private sealed record TerminalProcessHandle(IInteractiveProcess Process, CancellationTokenSource Cancellation);
}
