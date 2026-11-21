using Microsoft.Extensions.Options;
using Vessel.Application.Auditing;
using Vessel.Application.Persistence;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Teams;
using Vessel.Domain.Users;
using Vessel.Domain.ValueObjects;

namespace Vessel.Application.Auth.Sessions;

/// <summary>Client details recorded with a session (trusted client address, truncated user agent).</summary>
public sealed record SessionClient(string? IpAddress, string? UserAgent)
{
    public static readonly SessionClient Unknown = new(null, null);
}

/// <summary>The outcome of re-validating a session cookie.</summary>
/// <param name="IsActive">False when the session is unknown, revoked, expired or belongs to someone else.</param>
/// <param name="ActiveTeamId">The team the session works in now (membership re-checked on every validation).</param>
public sealed record SessionValidationResult(bool IsActive, TeamId ActiveTeamId)
{
    public static readonly SessionValidationResult Inactive = new(false, default);
}

public sealed record UserSessionSummary(
    UserSessionId Id,
    string AuthenticationMethod,
    string? IpAddress,
    string? UserAgent,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset ExpiresAt,
    bool IsCurrent);

/// <summary>
///     Server-side sessions and the active team (IAM-09).
///     <list type="bullet">
///         <item>Every interactive sign-in creates a <see cref="UserSession" />; the cookie carries its id.</item>
///         <item>
///             Each request re-validates the session (revocation, expiry) and the membership of its active team; a team
///             the user was removed from is replaced by their home team, so a stale cookie never authorizes in it.
///         </item>
///         <item>
///             The session row stores the active team, so a request that started before a team switch cannot write
///             the old team back (Coolify <c>KeepConcurrentTeamSwitch</c>). Switching also remembers the team on the
///             user for the next login.
///         </item>
///         <item>Users list and revoke their sessions; password resets and second-factor changes revoke them too.</item>
///     </list>
/// </summary>
public sealed class UserSessionService(
    IVesselDbContext dbContext,
    IAuditWriter auditWriter,
    IOptions<AuthOptions> options,
    TimeProvider timeProvider)
{
    public const string ReasonSignedOut = "signed_out";
    public const string ReasonRevokedByUser = "revoked_by_user";
    public const string ReasonPasswordReset = "password_reset";
    public const string ReasonPasswordChanged = "password_changed";
    public const string ReasonTwoFactorChanged = "two_factor_changed";

    /// <summary>Activity is written at most this often per session, to keep validation read-mostly.</summary>
    public static readonly TimeSpan ActivityWriteInterval = TimeSpan.FromMinutes(5);

    private TimeSpan Lifetime => TimeSpan.FromDays(Math.Max(1, options.Value.SessionIdleDays));

    public async Task<UserSessionId> StartAsync(
        UserId userId,
        TeamId teamId,
        string authenticationMethod,
        SessionClient client,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (!IsMember(userId, teamId))
            throw new UnauthorizedAccessException("A session can only start in a team the user is a member of.");

        var session = UserSession.Start(userId, teamId, authenticationMethod, client.IpAddress, client.UserAgent,
            Lifetime, timeProvider.GetUtcNow());
        await dbContext.UserSessionRepository.AddAsync(session, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return session.Id;
    }

    public async Task<SessionValidationResult> ValidateAsync(
        UserSessionId sessionId,
        UserId userId,
        CancellationToken cancellationToken = default)
    {
        UserSession? session = await dbContext.UserSessionRepository.GetByIdAsync(sessionId, cancellationToken);
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (session is null || session.UserId != userId || !session.IsActive(now))
            return SessionValidationResult.Inactive;

        var changed = false;
        if (!IsMember(userId, session.ActiveTeamId))
        {
            User? user = await dbContext.UserRepository.GetByIdAsync(userId, cancellationToken);
            TeamId? home = user is null ? null : ResolveHomeTeam(dbContext, user);
            if (home is not { } homeTeam)
            {
                session.Revoke("no_team", now);
                await dbContext.SaveChangesAsync(cancellationToken);
                return SessionValidationResult.Inactive;
            }

            user!.ClearCurrentTeam(session.ActiveTeamId);
            session.SwitchTeam(homeTeam, now);
            changed = true;
        }

        if (now - session.LastSeenAt >= ActivityWriteInterval) changed |= session.RecordActivity(Lifetime, now);
        if (changed) await dbContext.SaveChangesAsync(cancellationToken);
        return new SessionValidationResult(true, session.ActiveTeamId);
    }

    /// <summary>
    ///     Switches the session's active team and remembers it on the user. Returns <c>false</c> when the user is not a
    ///     member of the team or the session is not active.
    /// </summary>
    public async Task<bool> SwitchTeamAsync(
        UserSessionId sessionId,
        UserId userId,
        TeamId teamId,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        if (!IsMember(userId, teamId)) return false;
        UserSession? session = await dbContext.UserSessionRepository.GetByIdAsync(sessionId, cancellationToken);
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (session is null || session.UserId != userId || !session.IsActive(now)) return false;
        User? user = await dbContext.UserRepository.GetByIdAsync(userId, cancellationToken);
        if (user is null) return false;

        TeamId previous = session.ActiveTeamId;
        session.SwitchTeam(teamId, now);
        user.SetCurrentTeam(teamId);
        await auditWriter.StageAsync(new AuditEntry(teamId, userId, AuditActions.TeamSwitched,
            new AuditTarget("team", teamId.ToString()))
        {
            CorrelationId = correlationId,
            Changes = [new AuditFieldChange("activeTeamId", previous.Value, teamId.Value)]
        }, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public IReadOnlyList<UserSessionSummary> List(UserId userId, UserSessionId? currentSessionId)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        return dbContext.UserSessions
            .Where(session => session.UserId == userId && session.RevokedAt == null && session.ExpiresAt > now)
            .OrderByDescending(session => session.LastSeenAt)
            .AsEnumerable()
            .Select(session => new UserSessionSummary(session.Id, session.AuthenticationMethod, session.IpAddress,
                session.UserAgent, session.CreatedAt, session.LastSeenAt, session.ExpiresAt,
                session.Id == currentSessionId))
            .ToArray();
    }

    /// <summary>Revokes one of the user's own sessions. Returns <c>false</c> when it is not theirs or not active.</summary>
    public async Task<bool> RevokeAsync(
        UserId userId,
        UserSessionId sessionId,
        string reason,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        UserSession? session = await dbContext.UserSessionRepository.GetByIdAsync(sessionId, cancellationToken);
        if (session is null || session.UserId != userId || session.IsRevoked) return false;

        session.Revoke(reason, timeProvider.GetUtcNow());
        await auditWriter.StageAsync(new AuditEntry(null, userId, AuditActions.SessionRevoked,
            new AuditTarget("session", sessionId.ToString()))
        {
            CorrelationId = correlationId,
            Metadata = new Dictionary<string, object?> { ["reason"] = reason }
        }, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Revokes every active session of the user except <paramref name="keep" />; the caller saves.</summary>
    public async Task<int> StageRevokeAllAsync(
        UserId userId,
        UserSessionId? keep,
        string reason,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        UserSession[] sessions = dbContext.UserSessions
            .Where(session => session.UserId == userId && session.RevokedAt == null)
            .ToArray()
            .Where(session => session.Id != keep)
            .ToArray();
        foreach (UserSession session in sessions) session.Revoke(reason, now);
        if (sessions.Length > 0)
            await auditWriter.StageAsync(new AuditEntry(null, userId, AuditActions.SessionsRevoked,
                new AuditTarget("user", userId.ToString()))
            {
                CorrelationId = correlationId,
                Metadata = new Dictionary<string, object?>
                {
                    ["reason"] = reason,
                    ["sessionCount"] = sessions.Length,
                    ["keptCurrentSession"] = keep.HasValue
                }
            }, cancellationToken);
        return sessions.Length;
    }

    /// <summary>"Sign out everywhere else": revokes the user's other sessions and saves.</summary>
    public async Task<int> RevokeOthersAsync(
        UserId userId,
        UserSessionId currentSessionId,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        var revoked = await StageRevokeAllAsync(userId, currentSessionId, ReasonRevokedByUser, correlationId,
            cancellationToken);
        if (revoked > 0) await dbContext.SaveChangesAsync(cancellationToken);
        return revoked;
    }

    /// <summary>Deletes sessions that expired or were revoked more than <paramref name="olderThan" /> ago.</summary>
    public async Task<int> PruneAsync(TimeSpan olderThan, int batchSize = 500,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset cutoff = timeProvider.GetUtcNow().Subtract(olderThan);
        UserSession[] stale = dbContext.UserSessions
            .Where(session => session.ExpiresAt < cutoff || (session.RevokedAt != null && session.RevokedAt < cutoff))
            .OrderBy(session => session.ExpiresAt)
            .Take(Math.Max(1, batchSize))
            .ToArray();
        foreach (UserSession session in stale) dbContext.UserSessionRepository.Remove(session);
        if (stale.Length > 0) await dbContext.SaveChangesAsync(cancellationToken);
        return stale.Length;
    }

    /// <summary>
    ///     The team a user lands in (IAM-09): the remembered team while still a member, otherwise the personal team,
    ///     otherwise the first team they belong to. Invitations never count — a pending invitation is accepted only on
    ///     the invitation page. Returns <c>null</c> when the user belongs to no team.
    /// </summary>
    public static TeamId? ResolveHomeTeam(IVesselDbContext dbContext, User user)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(user);
        TeamId[] memberOf = dbContext.TeamMemberships
            .Where(membership => membership.UserId == user.Id)
            .Select(membership => membership.TeamId)
            .ToArray();
        if (memberOf.Length == 0) return null;
        if (user.CurrentTeamId is { } current && memberOf.Contains(current)) return current;

        var teams = dbContext.Teams
            .Where(team => memberOf.Contains(team.Id))
            .Select(team => new { team.Id, team.IsPersonal, team.CreatedAt })
            .ToArray();
        return teams
            .OrderByDescending(team => team.IsPersonal)
            .ThenBy(team => team.CreatedAt)
            .Select(team => (TeamId?)team.Id)
            .FirstOrDefault();
    }

    /// <summary>The home team, creating a personal team for a user who belongs to none (staged, caller saves).</summary>
    public static async Task<TeamId> ResolveOrCreateHomeTeamAsync(IVesselDbContext dbContext, User user,
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (ResolveHomeTeam(dbContext, user) is { } existing) return existing;
        var team = Team.Create(new DisplayName($"{user.Name.Value}'s Team"), user.Id, true, now);
        await dbContext.TeamRepository.AddAsync(team, cancellationToken);
        return team.Id;
    }

    private bool IsMember(UserId userId, TeamId teamId)
    {
        return dbContext.TeamMemberships.Any(membership =>
            membership.UserId == userId && membership.TeamId == teamId);
    }
}
