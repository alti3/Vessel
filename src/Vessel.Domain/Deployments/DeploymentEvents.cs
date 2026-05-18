using Vessel.Domain.Common;

namespace Vessel.Domain.Deployments;

public sealed record DeploymentStatusChangedEvent(
    DeploymentId DeploymentId,
    DeploymentStatus PreviousStatus,
    DeploymentStatus CurrentStatus,
    DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);
