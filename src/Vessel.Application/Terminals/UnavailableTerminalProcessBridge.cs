namespace Vessel.Application.Terminals;

using Vessel.Domain;

public sealed class UnavailableTerminalProcessBridge : ITerminalProcessBridge
{
    public Task OpenAsync(
        TerminalBridgeOpenRequest request,
        Func<TerminalOutputChunk, CancellationToken, Task> onOutput,
        Func<TerminalSessionId, string?, CancellationToken, Task> onExit,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("Terminal process bridge is unavailable.");
    }

    public Task SendInputAsync(TerminalSessionId sessionId, string data, CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("Terminal process bridge is unavailable.");
    }

    public Task ResizeAsync(TerminalBridgeResizeRequest request, CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("Terminal process bridge is unavailable.");
    }

    public Task CloseAsync(TerminalSessionId sessionId, CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("Terminal process bridge is unavailable.");
    }
}
