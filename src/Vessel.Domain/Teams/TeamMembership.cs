namespace Vessel.Domain.Teams;

public sealed class TeamMembership
{
    private TeamMembership()
    {
    }

    internal TeamMembership(TeamId teamId, UserId userId, TeamRole role, DateTimeOffset createdAt)
    {
        TeamId = teamId;
        UserId = userId;
        Role = role;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public TeamId TeamId { get; private set; }

    public UserId UserId { get; private set; }

    public TeamRole Role { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public Guid ConcurrencyStamp { get; private set; } = Guid.NewGuid();

    internal bool IsOwner => Role == TeamRole.Owner;

    internal void ChangeRole(TeamRole role, DateTimeOffset now)
    {
        if (Role == role) return;

        Role = role;
        UpdatedAt = now;
        ConcurrencyStamp = Guid.NewGuid();
    }
}
