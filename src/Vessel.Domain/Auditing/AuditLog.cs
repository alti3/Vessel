using Vessel.Domain.Common;

namespace Vessel.Domain.Auditing;

public sealed class AuditLog : Entity<AuditLogId>
{
    private AuditLog()
    {
    }

    private AuditLog(
        AuditLogId id,
        TeamId? teamId,
        UserId? actorUserId,
        string action,
        AuditTarget target,
        string? correlationId,
        string redactedMetadataJson,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        TeamId = teamId;
        ActorUserId = actorUserId;
        Action = action;
        Target = target;
        CorrelationId = correlationId;
        RedactedMetadataJson = redactedMetadataJson;
    }

    public TeamId? TeamId { get; private set; }

    public UserId? ActorUserId { get; private set; }

    public string Action { get; private set; } = string.Empty;

    public AuditTarget Target { get; private set; }

    public string? CorrelationId { get; private set; }

    public string RedactedMetadataJson { get; private set; } = "{}";

    public static AuditLog Record(
        TeamId? teamId,
        UserId? actorUserId,
        string action,
        AuditTarget target,
        string? correlationId,
        string redactedMetadataJson,
        DateTimeOffset now)
    {
        return new AuditLog(
            AuditLogId.New(),
            teamId,
            actorUserId,
            DomainValidation.Required(action, nameof(Action), 160),
            target,
            DomainValidation.Optional(correlationId, nameof(CorrelationId), 120),
            string.IsNullOrWhiteSpace(redactedMetadataJson) ? "{}" : redactedMetadataJson,
            now);
    }
}
