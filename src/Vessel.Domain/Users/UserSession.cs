using Vessel.Domain.Common;

namespace Vessel.Domain.Users;

/// <summary>
///     A server-side interactive session (IAM-09). The session cookie only carries the session id and identity claims;
///     this row is the source of truth for whether the session is still valid and which team is active, so sessions
///     can be listed and revoked, a password reset can end every session, and a stale request can never write an old
///     active team back (Coolify <c>KeepConcurrentTeamSwitch</c>). Columns are updated independently (no optimistic
///     concurrency token), so activity tracking never overwrites a revocation or a team switch.
/// </summary>
public sealed class UserSession : Entity<UserSessionId>
{
    public const int AuthenticationMethodMaxLength = 64;
    public const int IpAddressMaxLength = 64;
    public const int UserAgentMaxLength = 200;
    public const int RevocationReasonMaxLength = 64;

    private UserSession()
    {
    }

    private UserSession(
        UserSessionId id,
        UserId userId,
        TeamId activeTeamId,
        string authenticationMethod,
        string? ipAddress,
        string? userAgent,
        DateTimeOffset expiresAt,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        UserId = userId;
        ActiveTeamId = activeTeamId;
        AuthenticationMethod = authenticationMethod;
        IpAddress = ipAddress;
        UserAgent = userAgent;
        LastSeenAt = createdAt;
        ExpiresAt = expiresAt;
    }

    public UserId UserId { get; private set; }

    public TeamId ActiveTeamId { get; private set; }

    /// <summary><c>password</c>, <c>password+2fa</c>, <c>oauth:&lt;provider&gt;</c>, ...</summary>
    public string AuthenticationMethod { get; private set; } = string.Empty;

    public string? IpAddress { get; private set; }

    public string? UserAgent { get; private set; }

    public DateTimeOffset LastSeenAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public string? RevocationReason { get; private set; }

    public bool IsRevoked => RevokedAt.HasValue;

    public static UserSession Start(
        UserId userId,
        TeamId activeTeamId,
        string authenticationMethod,
        string? ipAddress,
        string? userAgent,
        TimeSpan lifetime,
        DateTimeOffset now)
    {
        if (lifetime <= TimeSpan.Zero) throw new DomainException("Session lifetime must be positive.");

        return new UserSession(
            UserSessionId.New(),
            userId,
            activeTeamId,
            DomainValidation.Required(authenticationMethod, nameof(AuthenticationMethod), AuthenticationMethodMaxLength),
            Truncate(ipAddress, IpAddressMaxLength),
            Truncate(userAgent, UserAgentMaxLength),
            now.Add(lifetime),
            now);
    }

    public bool IsActive(DateTimeOffset now)
    {
        return !IsRevoked && ExpiresAt > now;
    }

    /// <summary>Records activity and extends the sliding expiry. Revoked or expired sessions are not revived.</summary>
    public bool RecordActivity(TimeSpan lifetime, DateTimeOffset now)
    {
        if (!IsActive(now)) return false;
        LastSeenAt = now;
        DateTimeOffset extended = now.Add(lifetime);
        if (extended > ExpiresAt) ExpiresAt = extended;
        return true;
    }

    public void SwitchTeam(TeamId teamId, DateTimeOffset now)
    {
        if (IsRevoked) throw new DomainException("A revoked session cannot switch teams.");
        ActiveTeamId = teamId;
        LastSeenAt = now;
    }

    public void Revoke(string reason, DateTimeOffset now)
    {
        if (IsRevoked) return;
        RevokedAt = now;
        RevocationReason = DomainValidation.Required(reason, nameof(RevocationReason), RevocationReasonMaxLength);
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
