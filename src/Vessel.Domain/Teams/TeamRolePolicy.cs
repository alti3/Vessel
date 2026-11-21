namespace Vessel.Domain.Teams;

/// <summary>
///     Team role hierarchy (Coolify parity): Owner &gt; Admin &gt; Member. Only owners and admins manage members;
///     nobody can act on a member whose current role is above their own or grant a role above their own, so admins
///     can never create, promote to, demote or remove owners, and self-promotion is impossible.
/// </summary>
public static class TeamRolePolicy
{
    public static int Rank(TeamRole role)
    {
        return role switch
        {
            TeamRole.Owner => 3,
            TeamRole.Admin => 2,
            TeamRole.Member => 1,
            _ => 0
        };
    }

    public static bool CanManageMembers(TeamRole actorRole)
    {
        return actorRole is TeamRole.Owner or TeamRole.Admin;
    }

    /// <summary>Inviting with <paramref name="invitedRole" /> requires a rank at least that high.</summary>
    public static bool CanInvite(TeamRole actorRole, TeamRole invitedRole)
    {
        return CanManageMembers(actorRole) && Rank(invitedRole) <= Rank(actorRole);
    }

    /// <summary>
    ///     Changing a member's role requires a rank at least as high as both the member's current and new role.
    /// </summary>
    public static bool CanChangeRole(TeamRole actorRole, TeamRole currentRole, TeamRole newRole)
    {
        return CanManageMembers(actorRole)
               && Rank(currentRole) <= Rank(actorRole)
               && Rank(newRole) <= Rank(actorRole);
    }

    public static bool CanRemove(TeamRole actorRole, TeamRole memberRole)
    {
        return CanManageMembers(actorRole) && Rank(memberRole) <= Rank(actorRole);
    }

    /// <summary>Editing access grants is limited to Member rows (owners and admins are unrestricted).</summary>
    public static bool CanEditAccess(TeamRole actorRole, TeamRole memberRole)
    {
        return CanManageMembers(actorRole) && memberRole == TeamRole.Member;
    }

    public static bool IsDemotion(TeamRole currentRole, TeamRole newRole)
    {
        return Rank(newRole) < Rank(currentRole);
    }
}
