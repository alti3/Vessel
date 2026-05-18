using Vessel.Domain.Common;

namespace Vessel.Domain.Servers;

public sealed record ServerCreatedEvent(ServerId ServerId, TeamId TeamId, DateTimeOffset OccurredAt)
    : DomainEvent(OccurredAt);

public sealed record ServerStatusChangedEvent(
    ServerId ServerId,
    ServerStatus PreviousStatus,
    ServerStatus CurrentStatus,
    DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);
