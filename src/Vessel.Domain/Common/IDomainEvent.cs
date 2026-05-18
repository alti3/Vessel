namespace Vessel.Domain.Common;

public interface IDomainEvent
{
    DateTimeOffset OccurredAt { get; }
}
