using Vessel.Application.Processes;
using Vessel.Application.Ssh;

namespace Vessel.Infrastructure.Ssh;

public sealed class SshProcessClient(IProcessRunner processRunner) : ISshClient
{
    public async Task<bool> TestConnectionAsync(SshConnectionOptions options, CancellationToken cancellationToken = default)
    {
        ProcessResult result = await processRunner.RunTextAsync(new ProcessCommand(
            "ssh",
            BuildSshArgs(options, "true"),
            Timeout: options.ConnectTimeout ?? TimeSpan.FromSeconds(15),
            Redaction: Redaction(options)), cancellationToken);
        return result.Succeeded;
    }

    public async Task<SshCommandResult> RunCommandAsync(SshCommandRequest request, CancellationToken cancellationToken = default)
    {
        ProcessResult result = await processRunner.RunTextAsync(new ProcessCommand(
            "ssh",
            BuildSshArgs(request.Connection, request.Command),
            Timeout: request.Timeout ?? TimeSpan.FromMinutes(5),
            Redaction: new ProcessRedactionProfile(request.SecretValues ?? [], [])), cancellationToken);
        return new SshCommandResult(result.ExitInfo.ExitCode, result.StandardOutput, result.StandardError);
    }

    public async Task UploadAsync(SshFileTransferRequest request, CancellationToken cancellationToken = default)
    {
        ProcessResult result = await processRunner.RunTextAsync(new ProcessCommand(
            "scp",
            BuildScpArgs(request.Connection, request.LocalPath, $"{request.Connection.UserName}@{request.Connection.Host}:{request.RemotePath}"),
            Timeout: TimeSpan.FromMinutes(10),
            Redaction: Redaction(request.Connection)), cancellationToken);
        if (!result.Succeeded) throw new ProcessExecutionException(result);
    }

    public async Task DownloadAsync(SshFileTransferRequest request, CancellationToken cancellationToken = default)
    {
        ProcessResult result = await processRunner.RunTextAsync(new ProcessCommand(
            "scp",
            BuildScpArgs(request.Connection, $"{request.Connection.UserName}@{request.Connection.Host}:{request.RemotePath}", request.LocalPath),
            Timeout: TimeSpan.FromMinutes(10),
            Redaction: Redaction(request.Connection)), cancellationToken);
        if (!result.Succeeded) throw new ProcessExecutionException(result);
    }

    private static List<string> BuildSshArgs(SshConnectionOptions options, string command)
    {
        var args = BaseArgs(options);
        args.Add($"{options.UserName}@{options.Host}");
        args.Add(command);
        return args;
    }

    private static List<string> BuildScpArgs(SshConnectionOptions options, string source, string destination)
    {
        var args = BaseArgs(options);
        args.AddRange([source, destination]);
        return args;
    }

    private static List<string> BaseArgs(SshConnectionOptions options)
    {
        var args = new List<string>
        {
            "-p",
            options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-o",
            HostKeyOption(options.HostIdentityPolicy),
            "-o",
            "BatchMode=yes"
        };
        if (!string.IsNullOrWhiteSpace(options.PrivateKeyPath))
            args.AddRange(["-i", options.PrivateKeyPath]);
        return args;
    }

    private static string HostKeyOption(SshHostIdentityPolicy policy) => policy switch
    {
        SshHostIdentityPolicy.InsecureSkipValidation => "StrictHostKeyChecking=no",
        SshHostIdentityPolicy.TrustOnFirstUse => "StrictHostKeyChecking=accept-new",
        _ => "StrictHostKeyChecking=yes"
    };

    private static ProcessRedactionProfile Redaction(SshConnectionOptions options) =>
        new([options.PasswordSecretReference ?? string.Empty], []);
}
