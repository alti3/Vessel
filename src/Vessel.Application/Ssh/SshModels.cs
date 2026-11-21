namespace Vessel.Application.Ssh;

public enum SshHostIdentityPolicy
{
    /// <summary><c>StrictHostKeyChecking=yes</c>: the host key must already be pinned (enforce).</summary>
    RequireKnownHost,

    /// <summary><c>StrictHostKeyChecking=accept-new</c>: used only by the host key scan that pins a first key.</summary>
    TrustOnFirstUse,

    /// <summary><c>StrictHostKeyChecking=no</c> with no known hosts (host key checking off).</summary>
    InsecureSkipValidation,

    /// <summary>
    ///     <c>StrictHostKeyChecking=no</c> against the pinned key: connects when the key changed, and OpenSSH prints the
    ///     "REMOTE HOST IDENTIFICATION HAS CHANGED" warning so Vessel can record and report the change (warn).
    /// </summary>
    WarnOnMismatch
}

/// <summary>
///     Options of one SSH connection. <c>KnownHostsPath</c> is the Vessel-managed known_hosts file of the server: when set,
///     OpenSSH reads only this file (the global and user known_hosts files are ignored), with <c>HostKeyAlias</c> as the
///     host name.
/// </summary>
public sealed record SshConnectionOptions(
    string Host,
    int Port,
    string UserName,
    string? PrivateKeyPath = null,
    string? PasswordSecretReference = null,
    SshHostIdentityPolicy HostIdentityPolicy = SshHostIdentityPolicy.RequireKnownHost,
    TimeSpan? ConnectTimeout = null,
    string? KnownHostsPath = null,
    string? HostKeyAlias = null,
    TimeSpan? ServerAliveInterval = null);

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
