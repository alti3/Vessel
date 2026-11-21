using Vessel.Application.Servers;
using Vessel.Domain;

namespace Vessel.Application.Ssh;

/// <summary>
///     Resolves the SSH connection for a server: the team-owned private key (materialized as an owner-only file for the
///     lifetime of the lease), the Vessel-managed known_hosts file with the pinned host key, and the host key checking
///     mode. Every remote operation (runtime commands, file delivery, terminals, validation) goes through this factory,
///     so fixed connection options and host key pinning apply everywhere.
/// </summary>
public interface IServerSshConnectionFactory
{
    /// <summary>
    ///     Resolves a connection lease. A <c>keyOverride</c> uses that team key instead of the server's key (key rotation
    ///     verification).
    /// </summary>
    Task<SshConnectionLease> AcquireAsync(
        ServerExecutionTarget target,
        SshKeyId? keyOverride = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Inspects the standard error of a finished SSH process. When OpenSSH reports a changed host key for this server,
    ///     the presented key is recorded as pending and the team is notified. Never throws.
    /// </summary>
    Task ObserveAsync(ServerExecutionTarget target, string? standardError, CancellationToken cancellationToken = default);
}

/// <summary>Connection options valid until the lease is disposed (which removes the private key file).</summary>
public sealed class SshConnectionLease(SshConnectionOptions options, Func<ValueTask>? release = null) : IAsyncDisposable
{
    private Func<ValueTask>? _release = release;

    public SshConnectionOptions Options { get; } = options;

    public ValueTask DisposeAsync()
    {
        Func<ValueTask>? release = Interlocked.Exchange(ref _release, null);
        return release?.Invoke() ?? ValueTask.CompletedTask;
    }
}

/// <summary>Raised when a server's SSH host key cannot be verified (enforce mode without a readable key).</summary>
public sealed class SshHostKeyUnavailableException(string message) : InvalidOperationException(message);
