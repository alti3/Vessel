using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Vessel.Application.Notifications;
using Vessel.Application.Persistence;
using Vessel.Domain;
using Vessel.Domain.Notifications;
using Vessel.Infrastructure.Persistence;

namespace Vessel.Infrastructure.Notifications;

/// <summary>
///     PostgreSQL implementation of <see cref="INotificationThrottle" /> (NTF-04), following upstream
///     <c>NotificationThrottle::claim</c>: renew with a conditional <c>UPDATE … WHERE sent_at &lt; cutoff</c>, otherwise
///     <c>INSERT … ON CONFLICT DO NOTHING</c> against the unique (team, event type, subject) index. Both statements are
///     atomic, so concurrent workers can never both claim the same window. Statements bypass the change tracker, so
///     the caller's pending entities are never affected. When database persistence is disabled every claim succeeds.
/// </summary>
public sealed class EfNotificationThrottle(IVesselDbContext dbContext, TimeProvider timeProvider) : INotificationThrottle
{
    public async Task<bool> TryClaimAsync(TeamId teamId, string eventType, string subjectKey, TimeSpan? interval,
        CancellationToken cancellationToken = default)
    {
        if (dbContext is not VesselDbContext context)
            return true;

        var now = timeProvider.GetUtcNow();
        var teamGuid = teamId.Value;
        if (interval is { } window)
        {
            var cutoff = now - window;
            var renewed = await context.Set<NotificationThrottle>()
                .Where(throttle => throttle.TeamId == teamId
                                   && throttle.EventType == eventType
                                   && throttle.SubjectKey == subjectKey
                                   && throttle.SentAt < cutoff)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(throttle => throttle.SentAt, now)
                    .SetProperty(throttle => throttle.UpdatedAt, now), cancellationToken);
            if (renewed == 1)
                return true;
        }

        var table = Table(context);
        var sql = $$"""
                   INSERT INTO {{table.Name}} ({{table.Id}}, {{table.TeamId}}, {{table.EventType}}, {{table.SubjectKey}}, {{table.SentAt}}, {{table.CreatedAt}}, {{table.UpdatedAt}}, {{table.ConcurrencyStamp}})
                   VALUES ({0}, {1}, {2}, {3}, {4}, {4}, {4}, {5})
                   ON CONFLICT ({{table.TeamId}}, {{table.EventType}}, {{table.SubjectKey}}) DO NOTHING
                   """;
#pragma warning disable EF1002 // Identifiers come from the EF model; all values are parameters.
        var inserted = await context.Database.ExecuteSqlRawAsync(sql,
            [Guid.NewGuid(), teamGuid, eventType, subjectKey, now, Guid.NewGuid()], cancellationToken);
#pragma warning restore EF1002
        return inserted == 1;
    }

    public async Task<bool> ReleaseAsync(TeamId teamId, string eventType, string subjectKey,
        CancellationToken cancellationToken = default)
    {
        if (dbContext is not VesselDbContext context)
            return false;

        return await context.Set<NotificationThrottle>()
            .Where(throttle => throttle.TeamId == teamId
                               && throttle.EventType == eventType
                               && throttle.SubjectKey == subjectKey)
            .ExecuteDeleteAsync(cancellationToken) > 0;
    }

    public async Task<int> PurgeAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default)
    {
        if (dbContext is not VesselDbContext context)
            return 0;

        return await context.Set<NotificationThrottle>()
            .Where(throttle => throttle.SentAt < olderThan)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private static ThrottleTable Table(DbContext context)
    {
        IEntityType entity = context.Model.FindEntityType(typeof(NotificationThrottle))
                             ?? throw new InvalidOperationException("Notification throttle is not mapped.");
        var schema = entity.GetSchema() ?? entity.GetDefaultSchema();
        var tableName = entity.GetTableName()!;
        var store = StoreObjectIdentifier.Table(tableName, schema);

        string Column(string property)
        {
            return Quote(entity.FindProperty(property)!.GetColumnName(store)!);
        }

        return new ThrottleTable(
            schema is null ? Quote(tableName) : $"{Quote(schema)}.{Quote(tableName)}",
            Column(nameof(NotificationThrottle.Id)),
            Column(nameof(NotificationThrottle.TeamId)),
            Column(nameof(NotificationThrottle.EventType)),
            Column(nameof(NotificationThrottle.SubjectKey)),
            Column(nameof(NotificationThrottle.SentAt)),
            Column(nameof(NotificationThrottle.CreatedAt)),
            Column(nameof(NotificationThrottle.UpdatedAt)),
            Column(nameof(NotificationThrottle.ConcurrencyStamp)));
    }

    private static string Quote(string identifier)
    {
        return $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private sealed record ThrottleTable(
        string Name,
        string Id,
        string TeamId,
        string EventType,
        string SubjectKey,
        string SentAt,
        string CreatedAt,
        string UpdatedAt,
        string ConcurrencyStamp);
}
