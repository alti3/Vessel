using Vessel.Domain.Servers;

namespace Vessel.Application.Servers;

/// <summary>Server details for the API. Host keys are public data (fingerprints); no private key material is ever included.</summary>
public sealed record ServerDetails(
    Guid Id,
    string Name,
    string? Description,
    string Host,
    int Port,
    string? User,
    ServerConnectionType ConnectionType,
    ContainerRuntimeKind Runtime,
    ServerStatus Status,
    string Labels,
    Guid? SshKeyId,
    SshHostKeyCheckingMode HostKeyChecking,
    SshHostKeyCheckingMode EffectiveHostKeyChecking,
    IReadOnlyList<string> PinnedHostKeyFingerprints,
    IReadOnlyList<string> PendingHostKeyFingerprints,
    DateTimeOffset? HostKeyTrustedAt,
    DateTimeOffset? HostKeyChangedAt,
    bool TerminalAccessEnabled,
    ServerValidationDetails? Validation,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ServerValidationDetails(
    bool Succeeded,
    string? OsId,
    string? OsIdLike,
    string? OsVersion,
    string? PackageManager,
    string? RuntimeVersion,
    string? ComposeVersion,
    bool IsRootUser,
    bool HasPasswordlessSudo,
    string? Message,
    DateTimeOffset ValidatedAt);

/// <summary>
///     Partial server update (PATCH). Omitted (null) fields keep their value. Changing host or port forgets the pinned
///     host key (it belonged to the old address). <see cref="ClearSshKey" /> removes the key assignment.
/// </summary>
public sealed record PatchServerRequest(
    string? Name = null,
    string? Description = null,
    string? Host = null,
    int? Port = null,
    string? User = null,
    string? Labels = null,
    Guid? SshKeyId = null,
    bool ClearSshKey = false,
    SshHostKeyCheckingMode? HostKeyChecking = null,
    bool? TerminalAccessEnabled = null);

public sealed record ValidateServerRequest(bool InstallDocker = false);

public sealed record ServerValidationAccepted(Guid ServerId, string? JobId, bool InstallDocker);

/// <summary>Raised when a server cannot be deleted because resources still run on it (HTTP 409).</summary>
public sealed class ServerInUseException(string message) : InvalidOperationException(message);
