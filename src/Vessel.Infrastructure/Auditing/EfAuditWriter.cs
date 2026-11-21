using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vessel.Application.Auditing;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.ValueObjects;
using Vessel.Infrastructure.Persistence;

namespace Vessel.Infrastructure.Auditing;

/// <summary>
///     EF Core audit writer (IAM-08). <see cref="StageAsync" /> only adds the row to the scoped
///     <see cref="VesselDbContext" />, so it is written by the same <c>SaveChanges</c> (one transaction) as the change it
///     records; the <c>RecordAsync</c> overloads stage and save. The request context (source, client address, user
///     agent, API token) comes from <see cref="IAuditContextAccessor" />; metadata and changes are redacted by
///     <see cref="AuditRedaction" />.
/// </summary>
internal sealed class EfAuditWriter : IAuditWriter
{
    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly IAuditContextAccessor _context;
    private readonly VesselDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public EfAuditWriter(VesselDbContext dbContext, IAuditContextAccessor context, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _context = context;
        _timeProvider = timeProvider;
    }

    public Task RecordAsync(
        TeamId? teamId,
        UserId? actorUserId,
        string action,
        AuditTarget target,
        string? correlationId,
        IReadOnlyDictionary<string, object?> metadata,
        CancellationToken cancellationToken = default)
    {
        return RecordAsync(
            new AuditEntry(teamId, actorUserId, action, target) { CorrelationId = correlationId, Metadata = metadata },
            cancellationToken);
    }

    public async Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        await StageAsync(entry, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task StageAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        AuditContext context = _context.Current;
        var source = entry.Source ?? context.Source;

        var metadataJson = JsonSerializer.Serialize(AuditRedaction.RedactMetadata(entry.Metadata), JsonSerializerOptions);
        Dictionary<string, object?>? changes = AuditRedaction.RedactChanges(entry.Changes);
        var changesJson = changes is null ? null : JsonSerializer.Serialize(changes, JsonSerializerOptions);

        string? actorName = null;
        if (entry.ActorUserId is { } actorUserId)
            actorName = _dbContext.UserSet.Local.FirstOrDefault(user => user.Id == actorUserId)?.Name.Value
                        ?? (await _dbContext.UserSet.AsNoTracking()
                            .Where(user => user.Id == actorUserId)
                            .Select(user => (DisplayName?)user.Name)
                            .FirstOrDefaultAsync(cancellationToken))?.Value;

        string? tokenName = null;
        if (context.TokenId is { } tokenId)
            tokenName = await _dbContext.PersonalAccessTokenSet.AsNoTracking()
                .Where(token => token.Id == tokenId)
                .Select(token => token.Name)
                .FirstOrDefaultAsync(cancellationToken);

        var actorType = context.TokenId.HasValue ? AuditActorTypes.ApiToken
            : entry.ActorUserId.HasValue ? AuditActorTypes.User
            : source switch
            {
                AuditSources.Webhook => AuditActorTypes.Webhook,
                AuditSources.Job => AuditActorTypes.Job,
                _ => AuditActorTypes.System
            };

        AuditLog audit = AuditLog.Record(entry.TeamId, entry.ActorUserId, entry.Action, entry.Target,
                entry.CorrelationId, metadataJson, _timeProvider.GetUtcNow())
            .WithContext(source, actorType, actorName, context.TokenId, tokenName, context.IpAddress,
                context.UserAgent, entry.Level ?? AuditLevels.Classify(entry.Action))
            .WithDetails(entry.ResourceName is null ? null : AuditRedaction.RedactText(entry.ResourceName),
                entry.Description is null ? null : AuditRedaction.RedactText(entry.Description), changesJson);

        await _dbContext.AuditLogSet.AddAsync(audit, cancellationToken);
    }
}
