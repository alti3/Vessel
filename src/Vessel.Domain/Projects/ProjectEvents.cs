using Vessel.Domain.Common;

namespace Vessel.Domain.Projects;

public sealed record ProjectCreatedEvent(ProjectId ProjectId, TeamId TeamId, DateTimeOffset OccurredAt)
    : DomainEvent(OccurredAt);
