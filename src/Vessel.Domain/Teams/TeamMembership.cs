using Vessel.Domain.Access;

namespace Vessel.Domain.Teams;

public sealed class TeamMembership
{
    private TeamMembership()
    {
    }

    internal TeamMembership(
        TeamId teamId,
        UserId userId,
        TeamRole role,
        DateTimeOffset createdAt,
        IReadOnlyList<AccessGrant>? accessGrants = null)
    {
        AccessGrants = role == TeamRole.Member ? accessGrants : null;
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

    /// <summary>
    ///     Fine-grained access grants for a <see cref="TeamRole.Member" />. <c>null</c> means the default
    ///     (team-wide read); an empty list means no access. Owners and admins are unrestricted and ignore grants.
    /// </summary>
    public IReadOnlyList<AccessGrant>? AccessGrants { get; private set; }

    internal bool IsOwner => Role == TeamRole.Owner;

    internal void ChangeRole(TeamRole role, DateTimeOffset now)
    {
        if (Role == role) return;

        Role = role;
        // A role change resets grants: owners/admins are unrestricted and a (re)demoted member falls back to the
        // default team-wide read access.
        AccessGrants = null;
        UpdatedAt = now;
        ConcurrencyStamp = Guid.NewGuid();
    }

    internal void SetAccessGrants(IReadOnlyList<AccessGrant>? grants, DateTimeOffset now)
    {
        AccessGrants = grants;
        UpdatedAt = now;
        ConcurrencyStamp = Guid.NewGuid();
    }
}
