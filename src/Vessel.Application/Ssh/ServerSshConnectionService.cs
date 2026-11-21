using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Vessel.Application.Auditing;
using Vessel.Application.Notifications;
using Vessel.Application.Persistence;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Servers;
using Vessel.Domain.Ssh;

namespace Vessel.Application.Ssh;

/// <summary>Everything needed to open an SSH connection to a server, except the private key plaintext.</summary>
public sealed record PreparedSshConnection(
    ServerId ServerId,
    TeamId TeamId,
    string Host,
    int Port,
    string User,
    SshHostKeyCheckingMode Mode,
    string HostKeyAlias,
    string? PinnedHostKeys,
    SshKeyId? KeyId,
    string? KeyFingerprint,
    SecretReferenceId? KeyReferenceId);

/// <summary>
///     Host key pinning and key resolution for SSH servers (SEC-12). The first connection pins the host key the server
///     presents (trust on first use); later connections verify it against a Vessel-managed known_hosts file. A changed
///     key is recorded as pending, audited, and the team is notified through <see cref="INotificationEventPublisher" />.
/// </summary>
public sealed class ServerSshConnectionService(
    IVesselDbContext dbContext,
    ISecretVault secretVault,
    ISshHostKeyScanner scanner,
    SshHostKeyScanThrottle throttle,
    INotificationEventPublisher notifications,
    IAuditWriter auditWriter,
    IOptions<SshOptions> options,
    TimeProvider timeProvider)
{
    public const string DefaultUser = "root";

    public static string HostKeyAlias(ServerId serverId)
    {
        return $"vessel-server-{serverId.Value:N}";
    }

    public async Task<PreparedSshConnection> PrepareAsync(
        ServerId serverId,
        SshKeyId? keyOverride = null,
        CancellationToken cancellationToken = default)
    {
        Server server = dbContext.Servers.SingleOrDefault(candidate => candidate.Id == serverId)
                        ?? throw new InvalidOperationException("Server was not found.");
        if (server.ConnectionType != ServerConnectionType.Ssh)
            throw new InvalidOperationException("Only SSH servers have SSH connections.");
        var user = string.IsNullOrWhiteSpace(server.Address.User) ? DefaultUser : server.Address.User;
        if (!SshTargetValidator.IsSafeHost(server.Address.Host) || !SshTargetValidator.IsSafeUser(user))
            throw new InvalidOperationException("Server address or user contains characters that are not allowed for SSH.");

        SshHostKeyCheckingMode mode = SshOptions.Effective(options.Value.ResolveInstanceMode(), server.HostKeyChecking);
        var alias = HostKeyAlias(server.Id);
        var pinned = server.PinnedHostKey;
        if (mode != SshHostKeyCheckingMode.Off && string.IsNullOrWhiteSpace(pinned))
        {
            string? error = await LearnHostKeyAsync(server, user, alias, cancellationToken);
            pinned = server.PinnedHostKey;
            if (string.IsNullOrWhiteSpace(pinned) && mode == SshHostKeyCheckingMode.Enforce)
                throw new SshHostKeyUnavailableException(
                    "SSH connection refused: Vessel could not read the SSH host key of the server" +
                    (string.IsNullOrWhiteSpace(error) ? "." : $": {error}"));
        }

        SshKeyId? keyId = keyOverride ?? server.SshKeyId;
        SshKey? key = null;
        if (keyId.HasValue)
        {
            key = dbContext.SshKeys.SingleOrDefault(candidate => candidate.Id == keyId.Value)
                  ?? throw new InvalidOperationException("The SSH key of the server was not found.");
            // A server may only ever log in with a key of its own team.
            if (key.TeamId != server.TeamId)
                throw new UnauthorizedAccessException("The SSH key belongs to another team.");
        }

        return new PreparedSshConnection(server.Id, server.TeamId, server.Address.Host, server.Address.Port.Value,
            user, mode, alias, string.IsNullOrWhiteSpace(pinned) ? null : pinned, key?.Id, key?.Fingerprint,
            key?.PrivateKeyReferenceId);
    }

    /// <summary>Decrypts a team key for an SSH connection (audited by the vault).</summary>
    public Task<string> RevealPrivateKeyAsync(PreparedSshConnection connection,
        CancellationToken cancellationToken = default)
    {
        if (connection.KeyReferenceId is null)
            throw new InvalidOperationException("The connection has no SSH key.");
        return secretVault.RevealForDeploymentAsync(connection.TeamId, connection.KeyReferenceId.Value,
            cancellationToken);
    }

    /// <summary>
    ///     Records a host key change reported by OpenSSH for this server. Returns true when a new pending key was stored
    ///     (and the team notified).
    /// </summary>
    public async Task<bool> RecordHostKeyMismatchAsync(
        ServerId serverId,
        string? standardError,
        CancellationToken cancellationToken = default)
    {
        if (!SshKeyFormats.IsHostKeyMismatch(standardError)) return false;
        // The message must name this server's alias: a program on the server (git in a build) can print the same text
        // for its own SSH connection.
        if (!standardError!.Contains(HostKeyAlias(serverId), StringComparison.Ordinal) &&
            !standardError.Contains(SshRuntimeNames.KnownHostsPrefix(serverId), StringComparison.Ordinal))
            return false;

        Server? server = dbContext.Servers.SingleOrDefault(candidate => candidate.Id == serverId);
        if (server is null || string.IsNullOrWhiteSpace(server.PinnedHostKey)) return false;
        // Each scan is a failed login on the server (fail2ban), so a server is scanned at most once per interval.
        if (!throttle.TryBegin(serverId, "mismatch", timeProvider.GetUtcNow(), ScanInterval)) return false;

        var user = string.IsNullOrWhiteSpace(server.Address.User) ? DefaultUser : server.Address.User;
        SshHostKeyScanResult scan = await scanner.ScanAsync(server.Address.Host, server.Address.Port.Value, user,
            HostKeyAlias(serverId), cancellationToken);
        if (scan.HostKeys is null) return false;

        DateTimeOffset now = timeProvider.GetUtcNow();
        if (!server.RecordHostKeyChange(scan.HostKeys, now)) return false;
        await dbContext.SaveChangesAsync(cancellationToken);

        IReadOnlyList<string> fingerprints = SshKeyFormats.HostKeyFingerprints(scan.HostKeys);
        SshHostKeyCheckingMode mode = SshOptions.Effective(options.Value.ResolveInstanceMode(), server.HostKeyChecking);
        await auditWriter.RecordAsync(server.TeamId, null, AuditActions.ServerHostKeyChanged,
            new AuditTarget("server", server.Id.Value.ToString("D")), null,
            new Dictionary<string, object?> { ["fingerprints"] = fingerprints, ["mode"] = mode.ToString() },
            cancellationToken);
        await notifications.PublishAsync(server.TeamId,
            OperationalNotifications.ServerHostKeyChanged(server.Id.Value, server.Name.Value, fingerprints,
                mode == SshHostKeyCheckingMode.Enforce),
            CancellationToken.None);
        return true;
    }

    private TimeSpan ScanInterval => TimeSpan.FromSeconds(Math.Max(10, options.Value.HostKeyScanIntervalSeconds));

    private async Task<string?> LearnHostKeyAsync(Server server, string user, string alias,
        CancellationToken cancellationToken)
    {
        if (!throttle.TryBegin(server.Id, "learn", timeProvider.GetUtcNow(), TimeSpan.FromSeconds(30)))
            return throttle.LastError(server.Id);

        SshHostKeyScanResult scan =
            await scanner.ScanAsync(server.Address.Host, server.Address.Port.Value, user, alias, cancellationToken);
        if (scan.HostKeys is null)
        {
            throttle.RecordError(server.Id, scan.Error);
            return scan.Error;
        }

        if (!server.PinHostKey(scan.HostKeys, timeProvider.GetUtcNow())) return null;
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(server.TeamId, null, AuditActions.ServerHostKeyPinned,
            new AuditTarget("server", server.Id.Value.ToString("D")), null,
            new Dictionary<string, object?> { ["fingerprints"] = SshKeyFormats.HostKeyFingerprints(scan.HostKeys) },
            cancellationToken);
        return null;
    }
}

/// <summary>Names of Vessel-managed SSH runtime files, shared by Application and Infrastructure.</summary>
public static class SshRuntimeNames
{
    public static string KnownHostsPrefix(ServerId serverId)
    {
        return $"known_hosts_{serverId.Value:N}_";
    }
}

/// <summary>In-memory throttle for host key scans (singleton). Scans are failed logins, so they are rate limited.</summary>
public sealed class SshHostKeyScanThrottle
{
    private readonly ConcurrentDictionary<(ServerId, string), DateTimeOffset> _lastScan = new();
    private readonly ConcurrentDictionary<ServerId, string> _lastError = new();

    public bool TryBegin(ServerId serverId, string purpose, DateTimeOffset now, TimeSpan interval)
    {
        var key = (serverId, purpose);
        while (true)
        {
            if (!_lastScan.TryGetValue(key, out DateTimeOffset previous))
            {
                if (_lastScan.TryAdd(key, now)) return true;
                continue;
            }

            if (now - previous < interval) return false;
            if (_lastScan.TryUpdate(key, now, previous)) return true;
        }
    }

    public void Reset(ServerId serverId)
    {
        foreach ((ServerId, string) key in _lastScan.Keys.Where(key => key.Item1 == serverId).ToArray())
            _lastScan.TryRemove(key, out _);
        _lastError.TryRemove(serverId, out _);
    }

    public void RecordError(ServerId serverId, string? error)
    {
        _lastError[serverId] = string.IsNullOrWhiteSpace(error) ? "The host key scan failed." : error;
    }

    public string? LastError(ServerId serverId)
    {
        return _lastError.TryGetValue(serverId, out var error) ? error : null;
    }
}
