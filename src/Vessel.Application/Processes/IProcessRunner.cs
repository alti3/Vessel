namespace Vessel.Application.Processes;

public interface IProcessRunner
{
    Task<ProcessResult> RunTextAsync(ProcessCommand command, CancellationToken cancellationToken = default);

    Task<ProcessExitInfo> RunAsync(ProcessCommand command, CancellationToken cancellationToken = default);

    Task<ProcessBinaryResult> RunBinaryAsync(ProcessCommand command, CancellationToken cancellationToken = default);

    IAsyncEnumerable<ProcessOutputLine> StreamLinesAsync(
        ProcessCommand command,
        CancellationToken cancellationToken = default);
}
