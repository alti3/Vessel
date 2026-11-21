using Vessel.Domain.Access;
using Vessel.Domain.Common;

namespace Vessel.Domain.Users;

public sealed class PersonalAccessToken : Entity<PersonalAccessTokenId>
{
    private PersonalAccessToken()
    {
    }

    private PersonalAccessToken(
        PersonalAccessTokenId id,
        UserId userId,
        TeamId teamId,
        string name,
        string tokenHash,
        string scopes,
        DateTimeOffset? expiresAt,
        DateTimeOffset createdAt,
        IReadOnlyList<AccessGrant>? accessGrants)
        : base(id, createdAt)
    {
        AccessGrants = accessGrants;
        UserId = userId;
        TeamId = teamId;
        Name = name;
        TokenHash = tokenHash;
        Scopes = scopes;
        ExpiresAt = expiresAt;
    }

    public UserId UserId { get; private set; }

    public TeamId TeamId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string TokenHash { get; private set; } = string.Empty;

    public string Scopes { get; private set; } = "read";

    public DateTimeOffset? ExpiresAt { get; private set; }

    /// <summary>
    ///     Optional scope limits: the token only works on these teams/projects/environments/resources, with its
    ///     abilities as the levels. <c>null</c> means the whole team. The token can never exceed its owner either way.
    /// </summary>
    public IReadOnlyList<AccessGrant>? AccessGrants { get; private set; }

    /// <summary>A paused token keeps its secret and metadata but cannot authenticate until resumed.</summary>
    public DateTimeOffset? PausedAt { get; private set; }

    public bool IsPaused => PausedAt.HasValue;

    public DateTimeOffset? LastUsedAt { get; private set; }

    /// <summary>When the "token expires soon" warning was sent (IAM-04); cleared when the expiry is edited.</summary>
    public DateTimeOffset? ExpirationWarningSentAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public bool IsRevoked => RevokedAt.HasValue;

    public static PersonalAccessToken Create(
        UserId userId,
        TeamId teamId,
        string name,
        string tokenHash,
        string scopes,
        DateTimeOffset? expiresAt,
        DateTimeOffset now,
        IReadOnlyList<AccessGrant>? accessGrants = null)
    {
        return new PersonalAccessToken(
            PersonalAccessTokenId.New(),
            userId,
            teamId,
            DomainValidation.Required(name, nameof(Name), 255),
            DomainValidation.Required(tokenHash, nameof(TokenHash), 128),
            DomainValidation.Required(scopes, nameof(Scopes), 512),
            expiresAt,
            now,
            AccessGrant.Normalize(accessGrants));
    }

    public bool IsActive(DateTimeOffset now)
    {
        return !IsRevoked && !IsPaused && (!ExpiresAt.HasValue || ExpiresAt.Value > now);
    }

    /// <summary>Edits the name, abilities, scope limits and expiry; the secret stays the same.</summary>
    public void Update(
        string name,
        string scopes,
        IReadOnlyList<AccessGrant>? accessGrants,
        DateTimeOffset? expiresAt,
        DateTimeOffset now)
    {
        EnsureNotRevoked();
        if (expiresAt.HasValue && expiresAt.Value <= now)
            throw new DomainException("Token expiration must be in the future.");

        Name = DomainValidation.Required(name, nameof(Name), 255);
        Scopes = DomainValidation.Required(scopes, nameof(Scopes), 512);
        AccessGrants = AccessGrant.Normalize(accessGrants);
        if (ExpiresAt != expiresAt) ExpirationWarningSentAt = null;
        ExpiresAt = expiresAt;
        Touch(now);
    }

    /// <summary>
    ///     Whether the expiry warning is due (Coolify <c>ApiTokenExpirationWarningJob</c>): 7 days before expiry, or 24
    ///     hours before for tokens that live 7 days or less, so a short-lived token is not warned right after creation.
    ///     Revoked, paused-and-expired or already-warned tokens are never due.
    /// </summary>
    public bool IsExpirationWarningDue(DateTimeOffset now)
    {
        if (IsRevoked || ExpirationWarningSentAt.HasValue || ExpiresAt is not { } expiresAt || expiresAt <= now)
            return false;

        TimeSpan warningWindow = expiresAt - CreatedAt > ExpirationWarningWindow ? ExpirationWarningWindow : TimeSpan.FromDays(1);
        return expiresAt <= now.Add(warningWindow);
    }

    public static readonly TimeSpan ExpirationWarningWindow = TimeSpan.FromDays(7);

    public void MarkExpirationWarningSent(DateTimeOffset now)
    {
        ExpirationWarningSentAt = now;
    }

    public void Pause(DateTimeOffset now)
    {
        EnsureNotRevoked();
        if (PausedAt.HasValue) return;

        PausedAt = now;
        Touch(now);
    }

    public void Resume(DateTimeOffset now)
    {
        EnsureNotRevoked();
        if (!PausedAt.HasValue) return;

        PausedAt = null;
        Touch(now);
    }

    /// <summary>Replaces the secret (new hash) and keeps every other property, so a leaked value stops working.</summary>
    public void Roll(string tokenHash, DateTimeOffset now)
    {
        EnsureNotRevoked();
        TokenHash = DomainValidation.Required(tokenHash, nameof(TokenHash), 128);
        Touch(now);
    }

    private void EnsureNotRevoked()
    {
        if (IsRevoked) throw new DomainException("A revoked token cannot be changed.");
    }

    public void MarkUsed(DateTimeOffset now)
    {
        LastUsedAt = now;
        Touch(now);
    }

    public void Revoke(DateTimeOffset now)
    {
        if (RevokedAt.HasValue) return;

        RevokedAt = now;
        Touch(now);
    }
}
