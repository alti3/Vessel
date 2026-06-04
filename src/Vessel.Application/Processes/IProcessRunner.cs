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
