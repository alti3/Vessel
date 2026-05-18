using Vessel.Domain;
using Vessel.Domain.Auditing;

namespace Vessel.Application.Auditing;

public interface IAuditWriter
{
    Task RecordAsync(
        TeamId? teamId,
        UserId? actorUserId,
        string action,
        AuditTarget target,
        string? correlationId,
        IReadOnlyDictionary<string, object?> metadata,
        CancellationToken cancellationToken = default);
}
