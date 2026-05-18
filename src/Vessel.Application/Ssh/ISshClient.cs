namespace Vessel.Application.Ssh;

public interface ISshClient
{
    Task<bool> TestConnectionAsync(SshConnectionOptions options, CancellationToken cancellationToken = default);

    Task<SshCommandResult> RunCommandAsync(SshCommandRequest request, CancellationToken cancellationToken = default);

    Task UploadAsync(SshFileTransferRequest request, CancellationToken cancellationToken = default);

    Task DownloadAsync(SshFileTransferRequest request, CancellationToken cancellationToken = default);
}
