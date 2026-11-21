using Vessel.Application.Processes;

namespace Vessel.Application.Ssh;

public interface ISshClient
{
    Task<bool> TestConnectionAsync(SshConnectionOptions options, CancellationToken cancellationToken = default);

    Task<SshCommandResult> RunCommandAsync(SshCommandRequest request, CancellationToken cancellationToken = default);

    /// <summary>Runs <paramref name="request" /> with <paramref name="standardInput" /> streamed to the remote command.</summary>
    Task<SshCommandResult> RunCommandWithInputAsync(
        SshCommandRequest request,
        Stream standardInput,
        CancellationToken cancellationToken = default);

    /// <summary>Runs <paramref name="request" /> and yields its output line by line (deployment logs, builds).</summary>
    IAsyncEnumerable<ProcessOutputLine> StreamCommandAsync(
        SshCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException($"{GetType().Name} does not support streaming SSH commands.");
    }

    Task UploadAsync(SshFileTransferRequest request, CancellationToken cancellationToken = default);

    Task DownloadAsync(SshFileTransferRequest request, CancellationToken cancellationToken = default);
}
