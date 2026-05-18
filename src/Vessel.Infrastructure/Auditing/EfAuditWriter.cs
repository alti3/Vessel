using System.Text.Json;
using Vessel.Application.Auditing;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Infrastructure.Persistence;

namespace Vessel.Infrastructure.Auditing;

internal sealed class EfAuditWriter : IAuditWriter
{
    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly VesselDbContext _dbContext;

    public EfAuditWriter(VesselDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task RecordAsync(
        TeamId? teamId,
        UserId? actorUserId,
        string action,
        AuditTarget target,
        string? correlationId,
        IReadOnlyDictionary<string, object?> metadata,
        CancellationToken cancellationToken = default)
    {
        string redactedMetadataJson = JsonSerializer.Serialize(Redact(metadata), JsonSerializerOptions);
        await _dbContext.AuditLogSet.AddAsync(
            AuditLog.Record(teamId, actorUserId, action, target, correlationId, redactedMetadataJson, DateTimeOffset.UtcNow),
            cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static Dictionary<string, object?> Redact(IReadOnlyDictionary<string, object?> metadata)
    {
        return metadata.ToDictionary(
            item => item.Key,
            item => IsSensitive(item.Key) ? "[redacted]" : item.Value,
            StringComparer.Ordinal);
    }

    private static bool IsSensitive(string key)
    {
        return key.Contains("password", StringComparison.OrdinalIgnoreCase)
               || key.Contains("token", StringComparison.OrdinalIgnoreCase)
               || key.Contains("secret", StringComparison.OrdinalIgnoreCase);
    }
}
