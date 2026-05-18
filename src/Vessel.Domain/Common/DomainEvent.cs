namespace Vessel.Domain.Common;

public abstract record DomainEvent(DateTimeOffset OccurredAt) : IDomainEvent;
