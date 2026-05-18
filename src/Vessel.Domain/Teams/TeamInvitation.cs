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
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        TeamId = teamId;
        Email = email;
        Role = role;
        TokenHash = tokenHash;
        ExpiresAt = expiresAt;
    }

    public TeamId TeamId { get; private set; }

    public EmailAddress Email { get; private set; }

    public TeamRole Role { get; private set; }

    public string TokenHash { get; private set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? AcceptedAt { get; private set; }

    public bool IsAccepted => AcceptedAt.HasValue;

    public static TeamInvitation Create(
        TeamId teamId,
        EmailAddress email,
        TeamRole role,
        string tokenHash,
        DateTimeOffset expiresAt,
        DateTimeOffset now)
    {
        return new TeamInvitation(
            TeamInvitationId.New(),
            teamId,
            email,
            role,
            DomainValidation.Required(tokenHash, nameof(TokenHash), 128),
            expiresAt,
            now);
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
