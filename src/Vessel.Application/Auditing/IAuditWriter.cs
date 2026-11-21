using Vessel.Domain;
using Vessel.Domain.Auditing;

namespace Vessel.Application.Auditing;

/// <summary>
///     Records audit events (IAM-08). The writer fills the request context (source, client address, user agent, API
///     token) from <see cref="IAuditContextAccessor" /> and redacts metadata and changes with
///     <see cref="AuditRedaction" />.
///     <para>
///         Transactions: <see cref="StageAsync" /> adds the event to the current unit of work without saving, so the
///         caller's next <c>SaveChangesAsync</c> commits the business change and its audit row atomically (and a
///         rolled-back change leaves no audit row). The <c>RecordAsync</c> overloads stage and save at once; they are
///         for events with no business change of their own (failed logins, denials, reads). Any change already staged in
///         the unit of work is committed in the same transaction.
///     </para>
/// </summary>
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

    /// <summary>Stages and saves a rich audit entry.</summary>
    Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return RecordAsync(entry.TeamId, entry.ActorUserId, entry.Action, entry.Target, entry.CorrelationId,
            entry.Metadata ?? AuditEntry.EmptyMetadata, cancellationToken);
    }

    /// <summary>
    ///     Stages the entry in the current unit of work; the caller's next save commits it together with the change it
    ///     records. Writers without a unit of work record immediately.
    /// </summary>
    Task StageAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        return RecordAsync(entry, cancellationToken);
    }
}

/// <summary>One audit event. Only <see cref="TeamId" />, actor, action and target are required.</summary>
public sealed record AuditEntry(TeamId? TeamId, UserId? ActorUserId, string Action, AuditTarget Target)
{
    public static readonly IReadOnlyDictionary<string, object?> EmptyMetadata =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    public string? CorrelationId { get; init; }

    /// <summary>Free-form context; redacted by key and by value before it is stored.</summary>
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }

    /// <summary>Before/after values of the changed fields; sensitive fields are recorded as changed without values.</summary>
    public IReadOnlyList<AuditFieldChange>? Changes { get; init; }

    /// <summary>Explicit level; defaults to <see cref="AuditLevels.Classify" /> of the action.</summary>
    public AuditLevel? Level { get; init; }

    public string? ResourceName { get; init; }

    public string? Description { get; init; }

    /// <summary>Overrides the source from the request context (for example <c>job</c> for scheduled work).</summary>
    public string? Source { get; init; }
}

/// <summary>A field that changed.</summary>
public sealed record AuditFieldChange(string Field, object? Before, object? After);

/// <summary>Default levels: failures, denials, rejections and throttling are warnings (Coolify parity).</summary>
public static class AuditLevels
{
    private static readonly string[] WarningMarkers =
        ["failed", "denied", "rejected", "throttled", "locked", "unverified"];

    public static AuditLevel Classify(string action)
    {
        if (string.IsNullOrWhiteSpace(action)) return AuditLevel.Info;
        if (action.EndsWith("_error", StringComparison.Ordinal) || action.EndsWith(".error", StringComparison.Ordinal))
            return AuditLevel.Error;
        return WarningMarkers.Any(marker => action.Contains(marker, StringComparison.Ordinal))
            ? AuditLevel.Warning
            : AuditLevel.Info;
    }
}

/// <summary>Builds change sets from before/after snapshots of the same fields.</summary>
public static class AuditChanges
{
    /// <summary>The fields whose values differ between <paramref name="before" /> and <paramref name="after" />.</summary>
    public static IReadOnlyList<AuditFieldChange> Between(
        IReadOnlyDictionary<string, object?> before,
        IReadOnlyDictionary<string, object?> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        return before.Keys.Union(after.Keys, StringComparer.Ordinal)
            .Select(field => new AuditFieldChange(field,
                before.TryGetValue(field, out var previous) ? previous : null,
                after.TryGetValue(field, out var current) ? current : null))
            .Where(change => !Equals(Normalize(change.Before), Normalize(change.After)))
            .OrderBy(change => change.Field, StringComparer.Ordinal)
            .ToArray();
    }

    private static object? Normalize(object? value)
    {
        return value switch
        {
            null => null,
            string text => text,
            IEnumerable<string> items => string.Join('\u001f', items),
            _ => value
        };
    }
}
