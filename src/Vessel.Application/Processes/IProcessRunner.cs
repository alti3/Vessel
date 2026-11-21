namespace Vessel.Application.Processes;

public interface IProcessRunner
{
    Task<ProcessResult> RunTextAsync(ProcessCommand command, CancellationToken cancellationToken = default);

    Task<ProcessExitInfo> RunAsync(ProcessCommand command, CancellationToken cancellationToken = default);

    Task<ProcessBinaryResult> RunBinaryAsync(ProcessCommand command, CancellationToken cancellationToken = default);

    Task<ProcessResult> RunTextWithInputAsync(
        ProcessCommand command,
        Stream standardInput,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a command with <see cref="ProcessOutputMode.Stream" />, piping standard input and/or standard output
    /// through caller-owned streams so large payloads (database dumps, restores) are never buffered in memory.
    /// Standard error (and standard output when no output stream is supplied) is captured as redacted text and
    /// truncated, not rejected, at <see cref="ProcessCommand.MaxOutputBytes" />. Cancellation and
    /// <see cref="ProcessCommand.Timeout" /> terminate the process and are reported through
    /// <see cref="ProcessExitInfo" />.
    /// </summary>
    Task<ProcessResult> RunStreamingAsync(
        ProcessCommand command,
        ProcessStreams streams,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException($"{GetType().Name} does not support streaming process execution.");
    }

    IAsyncEnumerable<ProcessOutputLine> StreamLinesAsync(
        ProcessCommand command,
        CancellationToken cancellationToken = default);

    Task<IInteractiveProcess> StartInteractiveAsync(
        ProcessCommand command,
        CancellationToken cancellationToken = default);
}

public interface IInteractiveProcess : IAsyncDisposable
{
    int Id { get; }

    bool HasExited { get; }

    int ExitCode { get; }

    StreamWriter StandardInput { get; }

    StreamReader StandardOutput { get; }

    StreamReader StandardError { get; }

    Task WaitForExitAsync(CancellationToken cancellationToken = default);

    void Kill(bool entireProcessTree);
}
