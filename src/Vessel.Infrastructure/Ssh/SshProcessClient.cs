using System.Runtime.CompilerServices;
using Vessel.Application.Processes;
using Vessel.Application.Ssh;

namespace Vessel.Infrastructure.Ssh;

/// <summary>
///     OpenSSH client through <see cref="IProcessRunner" />. Argument vectors come from <see cref="SshArguments" />, so
///     every connection uses the fixed hardened options and Vessel-managed host key verification.
/// </summary>
public sealed class SshProcessClient(IProcessRunner processRunner) : ISshClient
{
    public async Task<bool> TestConnectionAsync(SshConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        ProcessResult result = await processRunner.RunTextAsync(new ProcessCommand(
            "ssh",
            SshArguments.ForSsh(options, "true"),
            Timeout: (options.ConnectTimeout ?? TimeSpan.FromSeconds(15)) + TimeSpan.FromSeconds(5),
            Redaction: Redaction(options)), cancellationToken);
        return result.Succeeded;
    }

    public async Task<SshCommandResult> RunCommandAsync(SshCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        ProcessResult result = await processRunner.RunTextAsync(new ProcessCommand(
            "ssh",
            SshArguments.ForSsh(request.Connection, request.Command),
            Timeout: request.Timeout ?? TimeSpan.FromMinutes(5),
            Redaction: new ProcessRedactionProfile(request.SecretValues ?? [], [])), cancellationToken);
        return new SshCommandResult(ExitCode(result), result.StandardOutput, result.StandardError);
    }

    public async Task<SshCommandResult> RunCommandWithInputAsync(
        SshCommandRequest request,
        Stream standardInput,
        CancellationToken cancellationToken = default)
    {
        ProcessResult result = await processRunner.RunTextWithInputAsync(new ProcessCommand(
            "ssh",
            SshArguments.ForSsh(request.Connection, request.Command),
            Timeout: request.Timeout ?? TimeSpan.FromMinutes(5),
            Redaction: new ProcessRedactionProfile(request.SecretValues ?? [], [])), standardInput, cancellationToken);
        return new SshCommandResult(ExitCode(result), result.StandardOutput, result.StandardError);
    }

    public async IAsyncEnumerable<ProcessOutputLine> StreamCommandAsync(
        SshCommandRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (ProcessOutputLine line in processRunner.StreamLinesAsync(new ProcessCommand(
                               "ssh",
                               SshArguments.ForSsh(request.Connection, request.Command),
                               Timeout: request.Timeout ?? TimeSpan.FromMinutes(30),
                               OutputMode: ProcessOutputMode.Lines,
                               Redaction: new ProcessRedactionProfile(request.SecretValues ?? [], [])),
                           cancellationToken))
            yield return line;
    }

    public async Task UploadAsync(SshFileTransferRequest request, CancellationToken cancellationToken = default)
    {
        ProcessResult result = await processRunner.RunTextAsync(new ProcessCommand(
            "scp",
            SshArguments.ForScp(request.Connection, request.LocalPath,
                SshArguments.RemotePath(request.Connection, request.RemotePath)),
            Timeout: TimeSpan.FromMinutes(10),
            Redaction: Redaction(request.Connection)), cancellationToken);
        if (!result.Succeeded) throw new ProcessExecutionException(result);
    }

    public async Task DownloadAsync(SshFileTransferRequest request, CancellationToken cancellationToken = default)
    {
        ProcessResult result = await processRunner.RunTextAsync(new ProcessCommand(
            "scp",
            SshArguments.ForScp(request.Connection,
                SshArguments.RemotePath(request.Connection, request.RemotePath), request.LocalPath),
            Timeout: TimeSpan.FromMinutes(10),
            Redaction: Redaction(request.Connection)), cancellationToken);
        if (!result.Succeeded) throw new ProcessExecutionException(result);
    }

    /// <summary>A canceled or timed-out ssh process never reports success, even if it exited with 0.</summary>
    private static int ExitCode(ProcessResult result)
    {
        if (result.Succeeded) return 0;
        return result.ExitInfo.ExitCode == 0 ? -1 : result.ExitInfo.ExitCode;
    }

    private static ProcessRedactionProfile Redaction(SshConnectionOptions options)
    {
        return new ProcessRedactionProfile(
            string.IsNullOrEmpty(options.PasswordSecretReference) ? [] : [options.PasswordSecretReference], []);
    }
}
