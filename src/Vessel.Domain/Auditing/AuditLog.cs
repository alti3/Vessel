using Vessel.Domain.Common;

namespace Vessel.Domain.Auditing;

/// <summary>
///     One audit event (IAM-08, Coolify <c>audit_events</c> parity). Besides the action and target it records where the
///     change came from (<see cref="Source" />: ui, api, webhook, job, system), who made it (<see cref="ActorType" />,
///     user, API token), the client address and user agent, a level, and an optional before/after
///     <see cref="ChangesJson" /> diff. Metadata and changes are redacted before they reach the entity; secrets and
///     personal data never belong here.
/// </summary>
public sealed class AuditLog : Entity<AuditLogId>
{
    public const int UserAgentMaxLength = 200;
    public const int IpAddressMaxLength = 64;
    public const int ResourceNameMaxLength = 255;
    public const int DescriptionMaxLength = 500;
    public const string DeletedUserName = "[deleted user]";

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
        ActorType = actorUserId.HasValue ? AuditActorTypes.User : AuditActorTypes.System;
    }

    public TeamId? TeamId { get; private set; }

    public UserId? ActorUserId { get; private set; }

    public string Action { get; private set; } = string.Empty;

    public AuditTarget Target { get; private set; }

    public string? CorrelationId { get; private set; }

    public string RedactedMetadataJson { get; private set; } = "{}";

    /// <summary>Where the change came from: <c>ui</c>, <c>api</c>, <c>webhook</c>, <c>job</c> or <c>system</c>.</summary>
    public string Source { get; private set; } = AuditSources.System;

    public AuditLevel Level { get; private set; } = AuditLevel.Info;

    /// <summary>Who acted: <c>user</c>, <c>api_token</c>, <c>webhook</c>, <c>job</c> or <c>system</c>.</summary>
    public string ActorType { get; private set; } = AuditActorTypes.System;

    /// <summary>Display name of the actor at the time of the event; replaced when the user is deleted.</summary>
    public string? ActorName { get; private set; }

    /// <summary>The API token that authenticated the request (never the secret).</summary>
    public PersonalAccessTokenId? ActorTokenId { get; private set; }

    public string? ActorTokenName { get; private set; }

    /// <summary>Trusted client address (rewritten from <c>X-Forwarded-For</c> only for configured proxies).</summary>
    public string? IpAddress { get; private set; }

    public string? UserAgent { get; private set; }

    public string? ResourceName { get; private set; }

    public string? Description { get; private set; }

    /// <summary>Redacted <c>{ "field": { "before": …, "after": … } }</c> diff, or <c>null</c>.</summary>
    public string? ChangesJson { get; private set; }

    public DateTimeOffset? AnonymizedAt { get; private set; }

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

    /// <summary>Attaches the request context (source, actor, client) and classification of the event.</summary>
    public AuditLog WithContext(
        string source,
        string actorType,
        string? actorName,
        PersonalAccessTokenId? actorTokenId,
        string? actorTokenName,
        string? ipAddress,
        string? userAgent,
        AuditLevel level)
    {
        Source = AuditSources.Normalize(source);
        ActorType = AuditActorTypes.Normalize(actorType);
        ActorName = Truncate(actorName, 120);
        ActorTokenId = actorTokenId;
        ActorTokenName = Truncate(actorTokenName, 255);
        IpAddress = Truncate(ipAddress, IpAddressMaxLength);
        UserAgent = Truncate(userAgent, UserAgentMaxLength);
        Level = level;
        return this;
    }

    /// <summary>Attaches the human-readable resource name, description and redacted change set.</summary>
    public AuditLog WithDetails(string? resourceName, string? description, string? redactedChangesJson)
    {
        ResourceName = Truncate(resourceName, ResourceNameMaxLength);
        Description = Truncate(description, DescriptionMaxLength);
        ChangesJson = string.IsNullOrWhiteSpace(redactedChangesJson) ? null : redactedChangesJson;
        return this;
    }

    /// <summary>
    ///     Removes the personal data of a deleted user from an event they performed (Coolify
    ///     <c>AuditEvent::anonymizeUser</c>): the actor name, client address and user agent. Metadata that names the
    ///     user is replaced by the caller with <paramref name="redactedMetadataJson" />.
    /// </summary>
    public void AnonymizeActor(string? redactedMetadataJson, DateTimeOffset now)
    {
        ActorName = DeletedUserName;
        IpAddress = null;
        UserAgent = null;
        AnonymizedAt = now;
        if (!string.IsNullOrWhiteSpace(redactedMetadataJson)) RedactedMetadataJson = redactedMetadataJson;
    }

    /// <summary>Replaces personal data in an event about the deleted user (they are the resource, not the actor).</summary>
    public void AnonymizeSubject(string? redactedMetadataJson, DateTimeOffset now)
    {
        ResourceName = DeletedUserName;
        AnonymizedAt = now;
        if (!string.IsNullOrWhiteSpace(redactedMetadataJson)) RedactedMetadataJson = redactedMetadataJson;
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}

public enum AuditLevel
{
    Info = 0,
    Warning = 1,
    Error = 2
}

public static class AuditSources
{
    public const string Ui = "ui";
    public const string Api = "api";
    public const string Webhook = "webhook";
    public const string Job = "job";
    public const string System = "system";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Ui, Api, Webhook, Job, System };

    public static string Normalize(string? source)
    {
        var value = source?.Trim().ToLowerInvariant();
        return value is not null && All.Contains(value) ? value : System;
    }
}

public static class AuditActorTypes
{
    public const string User = "user";
    public const string ApiToken = "api_token";
    public const string Webhook = "webhook";
    public const string Job = "job";
    public const string System = "system";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { User, ApiToken, Webhook, Job, System };

    public static string Normalize(string? actorType)
    {
        var value = actorType?.Trim().ToLowerInvariant();
        return value is not null && All.Contains(value) ? value : System;
    }
}
