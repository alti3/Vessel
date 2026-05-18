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
