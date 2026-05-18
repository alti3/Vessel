using Vessel.Domain.Common;

namespace Vessel.Domain.Applications;

public sealed record ApplicationCreatedEvent(
    ApplicationId ApplicationId,
    EnvironmentId EnvironmentId,
    ServerId ServerId,
    DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);
