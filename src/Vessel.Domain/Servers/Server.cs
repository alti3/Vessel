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
