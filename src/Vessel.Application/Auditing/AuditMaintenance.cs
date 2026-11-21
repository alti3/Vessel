using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Vessel.Application.Jobs;
using Vessel.Application.Persistence;
using Vessel.Domain;
using Vessel.Domain.Auditing;

namespace Vessel.Application.Auditing;

/// <summary>Audit retention settings (<c>Vessel:Audit</c>).</summary>
public sealed class AuditOptions
{
    public const string SectionName = "Vessel:Audit";

    /// <summary>Events older than this are deleted by the daily retention job (Coolify: 90 days). 0 keeps them forever.</summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>Rows deleted per batch; the job runs batches until nothing is left (bounded per run).</summary>
    public int PruneBatchSize { get; set; } = 500;
}

/// <summary>Retention (IAM-08): deletes audit events older than <see cref="AuditOptions.RetentionDays" />.</summary>
public sealed class AuditRetentionService(
    IVesselDbContext dbContext,
    IAuditWriter auditWriter,
    IOptions<AuditOptions> options,
    TimeProvider timeProvider)
{
    private const int MaxBatchesPerRun = 200;

    public async Task<int> PruneAsync(CancellationToken cancellationToken = default)
    {
        AuditOptions settings = options.Value;
        if (settings.RetentionDays <= 0) return 0;

        DateTimeOffset cutoff = timeProvider.GetUtcNow().AddDays(-settings.RetentionDays);
        var batchSize = Math.Clamp(settings.PruneBatchSize, 1, 10_000);
        var deleted = 0;
        for (var batch = 0; batch < MaxBatchesPerRun; batch++)
        {
            AuditLog[] expired = dbContext.AuditLogs
                .Where(audit => audit.CreatedAt < cutoff)
                .OrderBy(audit => audit.CreatedAt)
                .Take(batchSize)
                .ToArray();
            if (expired.Length == 0) break;

            foreach (AuditLog audit in expired) dbContext.AuditLogRepository.Remove(audit);
            await dbContext.SaveChangesAsync(cancellationToken);
            deleted += expired.Length;
            if (expired.Length < batchSize) break;
        }

        if (deleted > 0)
            await auditWriter.RecordAsync(new AuditEntry(null, null, AuditActions.AuditEventsPruned,
                new AuditTarget("system", "audit_events"))
            {
                Source = AuditSources.Job,
                Metadata = new Dictionary<string, object?>
                {
                    ["cutoff"] = cutoff,
                    ["deletedCount"] = deleted,
                    ["retentionDays"] = settings.RetentionDays
                }
            }, cancellationToken);
        return deleted;
    }
}

/// <summary>
///     Removes a deleted user's personal data from the audit log (IAM-08, Coolify <c>AuditEvent::anonymizeUser</c>):
///     events they performed lose the actor name, client address and user agent; events about them lose the name; and
///     metadata values equal to their name or (masked) email are replaced. Account deletion (IAM-13) calls this before
///     removing the user (the actor id is then cleared by the foreign key).
/// </summary>
public sealed class AuditAnonymizationService(IVesselDbContext dbContext, TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<int> AnonymizeUserAsync(
        UserId userId,
        string? name,
        string? email,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        var personal = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(name)) personal.Add(name.Trim());
        if (!string.IsNullOrWhiteSpace(email))
        {
            personal.Add(email.Trim());
            personal.Add(AuditRedaction.MaskEmail(email.Trim()));
        }

        UserId? actor = userId;
        var subject = new AuditTarget("user", userId.ToString());
        AuditLog[] performed = dbContext.AuditLogs.Where(audit => audit.ActorUserId == actor).ToArray();
        AuditLog[] about = dbContext.AuditLogs.Where(audit => audit.Target == subject).ToArray();

        foreach (AuditLog audit in performed) audit.AnonymizeActor(Scrub(audit.RedactedMetadataJson, personal), now);
        foreach (AuditLog audit in about) audit.AnonymizeSubject(Scrub(audit.RedactedMetadataJson, personal), now);

        var changed = performed.Union(about).Count();
        if (changed > 0)
        {
            await dbContext.AuditLogRepository.AddAsync(AuditLog.Record(null, null, AuditActions.AuditUserAnonymized,
                    new AuditTarget("user", userId.ToString()), null,
                    JsonSerializer.Serialize(new Dictionary<string, object?> { ["eventCount"] = changed }, JsonOptions),
                    now)
                .WithContext(AuditSources.System, AuditActorTypes.System, null, null, null, null, null,
                    AuditLevel.Info), cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return changed;
    }

    private static string? Scrub(string json, HashSet<string> personal)
    {
        if (personal.Count == 0 || string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            JsonNode? node = JsonNode.Parse(json);
            return node is null ? null : Replace(node, personal) is { } replaced ? replaced.ToJsonString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonNode? Replace(JsonNode node, HashSet<string> personal)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(pair => pair.Key).ToArray())
                    if (obj[key] is { } child)
                        obj[key] = Replace(child.DeepClone(), personal);
                return obj;
            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                    if (array[index] is { } item)
                        array[index] = Replace(item.DeepClone(), personal);
                return array;
            case JsonValue value when value.TryGetValue(out string? text) && text is not null
                                      && personal.Contains(text.Trim()):
                return JsonValue.Create(AuditLog.DeletedUserName);
            default:
                return node;
        }
    }
}

/// <summary>Thin Hangfire entry point for the audit retention sweep.</summary>
public sealed class AuditRetentionJob(AuditRetentionService retention)
{
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        using IDisposable scope = AuditContextScope.Begin(AuditContext.Job);
        return await retention.PruneAsync(cancellationToken);
    }
}

public static class AuditRecurringJobs
{
    public const string RetentionJobId = "audit.retention";
    public const string RetentionCronExpression = "23 4 * * *";

    public static void Register(IRecurringJobScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        scheduler.AddOrUpdate<AuditRetentionJob>(RetentionJobId, job => job.RunAsync(CancellationToken.None),
            RetentionCronExpression);
    }
}
