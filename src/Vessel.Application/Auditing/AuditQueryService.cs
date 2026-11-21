using System.Text.Json;
using Vessel.Application.Authorization;
using Vessel.Application.Persistence;
using Vessel.Domain;
using Vessel.Domain.Auditing;

namespace Vessel.Application.Auditing;

/// <summary>Filters of the team audit log. Pages are newest first.</summary>
public sealed record AuditEventQuery(
    string? Search = null,
    string? Action = null,
    string? Source = null,
    AuditLevel? Level = null,
    UserId? ActorUserId = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Page = 1,
    int PerPage = AuditQueryService.DefaultPerPage);

/// <summary>
///     One audit event. The sensitive fields (token, client address, user agent, metadata, changes, correlation id)
///     are <c>null</c> unless the caller may read sensitive data (Coolify returns them only for
///     <c>read:sensitive</c>/<c>root</c> tokens).
/// </summary>
public sealed record AuditEventSummary(
    Guid Id,
    Guid? TeamId,
    string Action,
    string Source,
    string Level,
    string ActorType,
    Guid? ActorUserId,
    string? ActorName,
    string TargetType,
    string TargetId,
    string? ResourceName,
    string? Description,
    DateTimeOffset CreatedAt,
    Guid? ActorTokenId,
    string? ActorTokenName,
    string? IpAddress,
    string? UserAgent,
    JsonElement? Metadata,
    JsonElement? Changes,
    string? CorrelationId);

public sealed record AuditEventPage(
    IReadOnlyList<AuditEventSummary> Items,
    int Page,
    int PerPage,
    int Total,
    int LastPage,
    bool IncludesSensitiveFields);

/// <summary>
///     Team audit log query (IAM-08, Coolify <c>GET /api/v1/audit-events</c>). Only owners and admins of the active
///     team may read it (<see cref="VesselPermissions.AuditRead" />); a token additionally needs an ability that maps
///     to it. Sensitive fields are included only when the caller may read sensitive data in the team.
/// </summary>
public sealed class AuditQueryService(IVesselDbContext dbContext, VesselAuthorizationService authorization)
{
    public const int DefaultPerPage = 25;
    public const int MaxPerPage = 100;
    public const int MaxSearchLength = 255;

    public AuditEventPage Query(UserId actorUserId, TeamId teamId, AuditEventQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!authorization.HasPermission(actorUserId, teamId, VesselPermissions.AuditRead))
            throw new UnauthorizedAccessException("Only team owners and admins can view the audit log.");
        if (query.PerPage is < 1 or > MaxPerPage)
            throw new ArgumentException($"per_page must be between 1 and {MaxPerPage}.", nameof(query));
        if (query.Page < 1) throw new ArgumentException("page must be at least 1.", nameof(query));
        if (query.Search is { Length: > MaxSearchLength })
            throw new ArgumentException($"search must be at most {MaxSearchLength} characters.", nameof(query));
        if (!string.IsNullOrWhiteSpace(query.Source) && query.Source != "all"
                                                     && !AuditSources.All.Contains(query.Source))
            throw new ArgumentException("Unknown source.", nameof(query));

        var includeSensitive = authorization.CanReadSensitive(actorUserId, teamId);
        TeamId? scopedTeam = teamId;
        IQueryable<AuditLog> events = dbContext.AuditLogs.Where(audit => audit.TeamId == scopedTeam);

        if (!string.IsNullOrWhiteSpace(query.Action) && query.Action != "all")
        {
            var action = query.Action.Trim();
            events = events.Where(audit => audit.Action == action);
        }

        if (!string.IsNullOrWhiteSpace(query.Source) && query.Source != "all")
            events = events.Where(audit => audit.Source == query.Source);
        if (query.Level is { } level) events = events.Where(audit => audit.Level == level);
        if (query.ActorUserId is { } actor)
        {
            UserId? actorFilter = actor;
            events = events.Where(audit => audit.ActorUserId == actorFilter);
        }

        if (query.From is { } from) events = events.Where(audit => audit.CreatedAt >= from);
        if (query.To is { } to) events = events.Where(audit => audit.CreatedAt < to);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLowerInvariant();
#pragma warning disable CA1862, CA1304, CA1311 // Translated to SQL lower(...) LIKE; StringComparison overloads do not translate.
            events = events.Where(audit =>
                audit.Action.ToLower().Contains(term)
                || (audit.ResourceName != null && audit.ResourceName.ToLower().Contains(term))
                || (audit.Description != null && audit.Description.ToLower().Contains(term))
                || (audit.ActorName != null && audit.ActorName.ToLower().Contains(term)));
#pragma warning restore CA1862, CA1304, CA1311
        }

        var total = events.Count();
        AuditLog[] rows = events
            .OrderByDescending(audit => audit.CreatedAt)
            .Skip((query.Page - 1) * query.PerPage)
            .Take(query.PerPage)
            .ToArray();

        var lastPage = Math.Max(1, (int)Math.Ceiling(total / (double)query.PerPage));
        return new AuditEventPage(rows.Select(row => ToSummary(row, includeSensitive)).ToArray(), query.Page,
            query.PerPage, total, lastPage, includeSensitive);
    }

    private static AuditEventSummary ToSummary(AuditLog audit, bool includeSensitive)
    {
        return new AuditEventSummary(
            audit.Id.Value,
            audit.TeamId?.Value,
            audit.Action,
            audit.Source,
            audit.Level.ToString().ToLowerInvariant(),
            audit.ActorType,
            audit.ActorUserId?.Value,
            audit.ActorName,
            audit.Target.Type,
            audit.Target.Id,
            audit.ResourceName,
            audit.Description,
            audit.CreatedAt,
            includeSensitive ? audit.ActorTokenId?.Value : null,
            includeSensitive ? audit.ActorTokenName : null,
            includeSensitive ? audit.IpAddress : null,
            includeSensitive ? audit.UserAgent : null,
            includeSensitive ? Parse(audit.RedactedMetadataJson) : null,
            includeSensitive ? Parse(audit.ChangesJson) : null,
            includeSensitive ? audit.CorrelationId : null);
    }

    private static JsonElement? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
