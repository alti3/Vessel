using System.Globalization;
using System.Text.Json;
using Vessel.Application.Auditing;
using Vessel.Application.Jobs;
using Vessel.Application.Notifications;
using Vessel.Application.Persistence;
using Vessel.Domain.Auditing;
using Vessel.Domain.Notifications;
using Vessel.Domain.Users;

namespace Vessel.Application.Auth;

/// <summary>
///     IAM-04 remainder (Coolify <c>ApiTokenExpirationWarningJob</c>): warns the token's team 7 days before an API token
///     expires, or 24 hours before for tokens that live 7 days or less. Each token is warned once (editing its expiry
///     re-arms the warning). Tokens whose owner left the team are skipped. The notification names the token, never
///     its secret. Publishing goes through <see cref="INotificationEventPublisher" />, which never throws into the job.
/// </summary>
public sealed class TokenExpiryWarningService(
    IVesselDbContext dbContext,
    INotificationEventPublisher notifications,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
{
    public const int BatchSize = 200;

    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTimeOffset horizon = now.Add(PersonalAccessToken.ExpirationWarningWindow);
        PersonalAccessToken[] candidates = dbContext.PersonalAccessTokens
            .Where(token => token.ExpiresAt != null && token.ExpiresAt > now && token.ExpiresAt <= horizon
                            && token.ExpirationWarningSentAt == null && token.RevokedAt == null)
            .OrderBy(token => token.ExpiresAt)
            .Take(BatchSize)
            .ToArray();

        var warned = 0;
        foreach (PersonalAccessToken token in candidates)
        {
            if (!token.IsExpirationWarningDue(now)) continue;
            if (!dbContext.TeamMemberships.Any(membership =>
                    membership.TeamId == token.TeamId && membership.UserId == token.UserId))
                continue;

            // Mark first and commit with the audit row, so a crash after publishing cannot warn twice.
            token.MarkExpirationWarningSent(now);
            await auditWriter.StageAsync(new AuditEntry(token.TeamId, null, AuditActions.TokenExpiryWarningSent,
                new AuditTarget("personal_access_token", token.Id.ToString()))
            {
                Source = AuditSources.Job,
                ResourceName = token.Name,
                Metadata = new Dictionary<string, object?> { ["expiresAt"] = token.ExpiresAt }
            }, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await notifications.PublishAsync(token.TeamId, Expiring(token), cancellationToken);
            warned++;
        }

        return warned;
    }

    public static RaiseNotificationRequest Expiring(PersonalAccessToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        var expiresAt = token.ExpiresAt!.Value.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        return new RaiseNotificationRequest(
            NotificationEventTypes.ApiTokenExpiring,
            NotificationSeverity.Warning,
            NotificationTargetType.Team,
            null,
            "API token expiring soon",
            $"API token '{token.Name}' expires on {expiresAt}. Roll or replace it before it expires to avoid API outages.",
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["tokenId"] = token.Id.Value,
                ["expiresAt"] = token.ExpiresAt
            }),
            "/tokens",
            null);
    }
}

/// <summary>Thin Hangfire entry point.</summary>
public sealed class TokenExpiryWarningJob(TokenExpiryWarningService service)
{
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        using IDisposable scope = AuditContextScope.Begin(AuditContext.Job);
        return await service.RunAsync(cancellationToken);
    }
}

/// <summary>Deletes revoked and expired session rows (IAM-09; Coolify v4.7 prunes expired sessions).</summary>
public sealed class SessionPruneJob(Sessions.UserSessionService sessions, Microsoft.Extensions.Options.IOptions<AuthOptions> options)
{
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        var total = 0;
        for (var batch = 0; batch < 50; batch++)
        {
            var removed = await sessions.PruneAsync(TimeSpan.FromDays(Math.Max(1, options.Value.SessionRetentionDays)),
                500, cancellationToken);
            total += removed;
            if (removed < 500) break;
        }

        return total;
    }
}

public static class AuthRecurringJobs
{
    public const string TokenExpiryWarningJobId = "auth.token-expiry-warnings";
    public const string TokenExpiryWarningCron = "17 * * * *";
    public const string SessionPruneJobId = "auth.sessions.prune";
    public const string SessionPruneCron = "37 3 * * *";

    public static void Register(IRecurringJobScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        scheduler.AddOrUpdate<TokenExpiryWarningJob>(TokenExpiryWarningJobId,
            job => job.RunAsync(CancellationToken.None), TokenExpiryWarningCron);
        scheduler.AddOrUpdate<SessionPruneJob>(SessionPruneJobId, job => job.RunAsync(CancellationToken.None),
            SessionPruneCron);
    }
}
