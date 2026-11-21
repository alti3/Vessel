using Vessel.Domain.Access;
using Vessel.Domain.Common;
using Vessel.Domain.ValueObjects;

namespace Vessel.Domain.Teams;

public sealed class TeamInvitation : Entity<TeamInvitationId>
{
    private TeamInvitation()
    {
    }

    private TeamInvitation(
        TeamInvitationId id,
        TeamId teamId,
        EmailAddress email,
        TeamRole role,
        string tokenHash,
        DateTimeOffset expiresAt,
        DateTimeOffset createdAt,
        IReadOnlyList<AccessGrant>? accessGrants)
        : base(id, createdAt)
    {
        TeamId = teamId;
        Email = email;
        Role = role;
        AccessGrants = role == TeamRole.Member ? accessGrants : null;
        TokenHash = tokenHash;
        ExpiresAt = expiresAt;
    }

    public TeamId TeamId { get; private set; }

    public EmailAddress Email { get; private set; }

    public TeamRole Role { get; private set; }

    public string TokenHash { get; private set; } = string.Empty;

    /// <summary>Grants the invited member receives on acceptance (members only; <c>null</c> = team-wide read).</summary>
    public IReadOnlyList<AccessGrant>? AccessGrants { get; private set; }

    public DateTimeOffset ExpiresAt { get; }

    public DateTimeOffset? AcceptedAt { get; private set; }

    public bool IsAccepted => AcceptedAt.HasValue;

    public static TeamInvitation Create(
        TeamId teamId,
        EmailAddress email,
        TeamRole role,
        string tokenHash,
        DateTimeOffset expiresAt,
        DateTimeOffset now,
        IReadOnlyList<AccessGrant>? accessGrants = null)
    {
        return new TeamInvitation(
            TeamInvitationId.New(),
            teamId,
            email,
            role,
            DomainValidation.Required(tokenHash, nameof(TokenHash), 128),
            expiresAt,
            now,
            AccessGrant.Normalize(accessGrants));
    }

    public bool IsValid(DateTimeOffset now)
    {
        return !IsAccepted && ExpiresAt > now;
    }

    public void Accept(DateTimeOffset now)
    {
        AcceptedAt = now;
        Touch(now);
    }
}
