namespace Vessel.Application.Ssh;

public enum SshHostIdentityPolicy
{
    RequireKnownHost,
    TrustOnFirstUse,
    InsecureSkipValidation
}

public sealed record SshConnectionOptions(
    string Host,
    int Port,
    string UserName,
    string? PrivateKeyPath = null,
    string? PasswordSecretReference = null,
    SshHostIdentityPolicy HostIdentityPolicy = SshHostIdentityPolicy.RequireKnownHost,
    TimeSpan? ConnectTimeout = null);

public sealed record SshCommandRequest(
    SshConnectionOptions Connection,
    string Command,
    TimeSpan? Timeout = null,
    IReadOnlyList<string>? SecretValues = null);

public sealed record SshFileTransferRequest(
    SshConnectionOptions Connection,
    string LocalPath,
    string RemotePath);

public sealed record SshCommandResult(int ExitCode, string StandardOutput, string StandardError);
