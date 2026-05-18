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
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
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

    public DateTimeOffset? LastUsedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public bool IsRevoked => RevokedAt.HasValue;

    public static PersonalAccessToken Create(
        UserId userId,
        TeamId teamId,
        string name,
        string tokenHash,
        string scopes,
        DateTimeOffset? expiresAt,
        DateTimeOffset now)
    {
        return new PersonalAccessToken(
            PersonalAccessTokenId.New(),
            userId,
            teamId,
            DomainValidation.Required(name, nameof(Name), 255),
            DomainValidation.Required(tokenHash, nameof(TokenHash), 128),
            DomainValidation.Required(scopes, nameof(Scopes), 512),
            expiresAt,
            now);
    }

    public bool IsActive(DateTimeOffset now)
    {
        return !IsRevoked && (!ExpiresAt.HasValue || ExpiresAt.Value > now);
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
