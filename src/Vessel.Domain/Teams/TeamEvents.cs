using Vessel.Domain.Common;

namespace Vessel.Domain.Teams;

public sealed record TeamCreatedEvent(TeamId TeamId, DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);

public sealed record TeamMemberAddedEvent(
    TeamId TeamId,
    UserId UserId,
    TeamRole Role,
    DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);

public sealed record TeamMemberRoleChangedEvent(
    TeamId TeamId,
    UserId UserId,
    TeamRole Role,
    DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);
