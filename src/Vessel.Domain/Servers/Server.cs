using Vessel.Domain.Common;
using Vessel.Domain.ValueObjects;

namespace Vessel.Domain.Servers;

public sealed class Server : Entity<ServerId>
{
    private Server()
    {
    }

    private Server(
        ServerId id,
        TeamId teamId,
        ResourceName name,
        Description? description,
        ServerAddress address,
        ServerConnectionType connectionType,
        ContainerRuntimeKind runtime,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        TeamId = teamId;
        Name = name;
        Description = description;
        Address = address;
        ConnectionType = connectionType;
        Runtime = runtime;
        Status = ServerStatus.PendingValidation;
    }

    public TeamId TeamId { get; private set; }

    public ResourceName Name { get; private set; }

    public Description? Description { get; private set; }

    public ServerAddress Address { get; private set; }

    public ServerConnectionType ConnectionType { get; private set; }

    public ContainerRuntimeKind Runtime { get; private set; }

    public ServerCapability Capabilities { get; private set; } = ServerCapability.None;

    public ServerStatus Status { get; private set; }

    public DateTimeOffset? LastReachableAt { get; private set; }

    public DateTimeOffset? LastUnreachableAt { get; private set; }

    public string Labels { get; private set; } = string.Empty;

    /// <summary>Team-owned SSH key used to log in to an SSH server. Null uses the Vessel service user's identity.</summary>
    public SshKeyId? SshKeyId { get; private set; }

    /// <summary>Host key verification mode for SSH connections (default <see cref="SshHostKeyCheckingMode.Enforce" />).</summary>
    public SshHostKeyCheckingMode HostKeyChecking { get; private set; } = SshHostKeyCheckingMode.Enforce;

    /// <summary>Pinned host key lines (<c>type base64</c>, newline separated), learned on first connection.</summary>
    public string? PinnedHostKey { get; private set; }

    /// <summary>A host key the server presented that differs from <see cref="PinnedHostKey" />; awaits operator trust.</summary>
    public string? PendingHostKey { get; private set; }

    public DateTimeOffset? HostKeyTrustedAt { get; private set; }

    public DateTimeOffset? HostKeyChangedAt { get; private set; }

    /// <summary>Per-server switch for interactive terminals (server shell and container exec/attach).</summary>
    public bool TerminalAccessEnabled { get; private set; } = true;

    /// <summary>Last validation result (OS, runtime and Compose versions, privileges).</summary>
    public ServerValidation? Validation { get; private set; }

    public const int DefaultConcurrentBuilds = 2;
    public const int DefaultDeploymentQueueLimit = 25;
    public const int MaxConcurrentBuilds = 100;
    public const int MaxDeploymentQueueLimit = 1000;

    /// <summary>
    ///     How many deployments may build/run on this server at once (Coolify <c>concurrent_builds</c>, default 2).
    ///     Further admitted deployments wait in the queue.
    /// </summary>
    public int ConcurrentBuilds { get; private set; } = DefaultConcurrentBuilds;

    /// <summary>
    ///     How many deployments may wait in this server's queue (Coolify <c>deployment_queue_limit</c>, default 25).
    ///     Admission rejects new deployments once the queue is full.
    /// </summary>
    public int DeploymentQueueLimit { get; private set; } = DefaultDeploymentQueueLimit;

    /// <summary>
    /// True when operations against this server execute on the Vessel control-plane host itself
    /// (local runtime or the host Docker socket). Host-local execution is host-root-equivalent.
    /// </summary>
    public bool IsHostLocal => IsHostLocalConnection(ConnectionType);

    public static bool IsHostLocalConnection(ServerConnectionType connectionType)
    {
        return connectionType is ServerConnectionType.Local or ServerConnectionType.DockerSocket;
    }

    public static Server Create(
        TeamId teamId,
        ResourceName name,
        ServerAddress address,
        ServerConnectionType connectionType,
        ContainerRuntimeKind runtime,
        DateTimeOffset now,
        Description? description = null)
    {
        var server = new Server(ServerId.New(), teamId, name, description, address, connectionType, runtime, now);
        server.AddDomainEvent(new ServerCreatedEvent(server.Id, teamId, now));

        return server;
    }

    public void UpdateCapabilities(ServerCapability capabilities, DateTimeOffset now)
    {
        Capabilities = capabilities;
        Touch(now);
    }

    public void UpdateSettings(
        ResourceName name,
        Description? description,
        ServerAddress address,
        ServerConnectionType connectionType,
        ContainerRuntimeKind runtime,
        string? labels,
        DateTimeOffset now)
    {
        Name = name;
        Description = description;
        Address = address;
        ConnectionType = connectionType;
        Runtime = runtime;
        Labels = DomainValidation.Optional(labels, nameof(Labels), 1000) ?? string.Empty;
        Touch(now);
    }

    public const int MaxHostKeyLength = 16 * 1024;

    public void AssignSshKey(SshKeyId? sshKeyId, DateTimeOffset now)
    {
        if (SshKeyId == sshKeyId) return;
        SshKeyId = sshKeyId;
        Touch(now);
    }

    public void SetHostKeyChecking(SshHostKeyCheckingMode mode, DateTimeOffset now)
    {
        if (!Enum.IsDefined(mode)) throw new DomainException("Unknown SSH host key checking mode.");
        if (HostKeyChecking == mode) return;
        HostKeyChecking = mode;
        Touch(now);
    }

    public void SetTerminalAccess(bool enabled, DateTimeOffset now)
    {
        if (TerminalAccessEnabled == enabled) return;
        TerminalAccessEnabled = enabled;
        Touch(now);
    }

    /// <summary>Pins the host key on first use. Returns false (and changes nothing) when a key is already pinned.</summary>
    public bool PinHostKey(string hostKeys, DateTimeOffset now)
    {
        if (!string.IsNullOrWhiteSpace(PinnedHostKey)) return false;
        PinnedHostKey = DomainValidation.Required(hostKeys, nameof(PinnedHostKey), MaxHostKeyLength);
        PendingHostKey = null;
        HostKeyTrustedAt = now;
        Touch(now);
        return true;
    }

    /// <summary>
    ///     Records that the server presented a host key that differs from the pinned key. Returns true (and raises
    ///     <see cref="ServerHostKeyChangedEvent" />) only for a newly observed key, so a team is notified once per change.
    /// </summary>
    public bool RecordHostKeyChange(string presentedHostKeys, DateTimeOffset now)
    {
        var presented = DomainValidation.Required(presentedHostKeys, nameof(PendingHostKey), MaxHostKeyLength);
        if (string.IsNullOrWhiteSpace(PinnedHostKey)) return false;
        var pinnedLines = PinnedHostKey.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var presentedLines = presented.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (presentedLines.All(line => pinnedLines.Contains(line, StringComparer.Ordinal))) return false;
        if (string.Equals(PendingHostKey, presented, StringComparison.Ordinal)) return false;
        PendingHostKey = presented;
        HostKeyChangedAt = now;
        Touch(now);
        AddDomainEvent(new ServerHostKeyChangedEvent(Id, TeamId, now));
        return true;
    }

    /// <summary>Operator decision: the pending (changed) host key is legitimate and becomes the pinned key.</summary>
    public void TrustPendingHostKey(DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(PendingHostKey))
            throw new DomainException("There is no pending host key to trust.");
        PinnedHostKey = PendingHostKey;
        PendingHostKey = null;
        HostKeyTrustedAt = now;
        Touch(now);
    }

    /// <summary>Forgets the pinned and pending host keys; the next connection pins the key again (trust on first use).</summary>
    public void ResetHostKey(DateTimeOffset now)
    {
        PinnedHostKey = null;
        PendingHostKey = null;
        HostKeyTrustedAt = null;
        HostKeyChangedAt = null;
        Touch(now);
    }

    public void RecordValidation(ServerValidation validation, DateTimeOffset now)
    {
        Validation = validation ?? throw new ArgumentNullException(nameof(validation));
        Touch(now);
    }

    public void UpdateDeploymentLimits(int concurrentBuilds, int deploymentQueueLimit, DateTimeOffset now)
    {
        if (concurrentBuilds is < 1 or > MaxConcurrentBuilds)
            throw new DomainException($"Concurrent builds must be between 1 and {MaxConcurrentBuilds}.");
        if (deploymentQueueLimit is < 1 or > MaxDeploymentQueueLimit)
            throw new DomainException($"Deployment queue limit must be between 1 and {MaxDeploymentQueueLimit}.");
        ConcurrentBuilds = concurrentBuilds;
        DeploymentQueueLimit = deploymentQueueLimit;
        Touch(now);
    }

    /// <summary>
    ///     Health checks that failed in a row (upstream Coolify <c>unreachable_count</c>). The unreachable alert fires once
    ///     this reaches <see cref="UnreachableAlertThreshold" />; a successful check resets it.
    /// </summary>
    public int ConsecutiveFailedHealthChecks { get; private set; }

    /// <summary>Consecutive failed checks before the server-unreachable notification (upstream: 2).</summary>
    public const int UnreachableAlertThreshold = 2;

    /// <summary>Records one health check outcome and returns the consecutive failure count (0 after a success).</summary>
    public int RecordHealthCheck(bool reachable)
    {
        ConsecutiveFailedHealthChecks = reachable ? 0 : Math.Min(ConsecutiveFailedHealthChecks + 1, 1_000_000);
        return ConsecutiveFailedHealthChecks;
    }

    public void ChangeStatus(ServerStatus status, DateTimeOffset now)
    {
        if (Status == status) return;

        ServerStatus previous = Status;
        Status = status;
        LastReachableAt = status == ServerStatus.Reachable ? now : LastReachableAt;
        LastUnreachableAt = status == ServerStatus.Unreachable ? now : LastUnreachableAt;
        Touch(now);
        AddDomainEvent(new ServerStatusChangedEvent(Id, previous, status, now));
    }
}
