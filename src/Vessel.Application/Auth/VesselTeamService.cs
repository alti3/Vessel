using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Persistence;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Access;
using Vessel.Domain.Auditing;
using Vessel.Domain.Teams;
using Vessel.Domain.Users;
using Vessel.Domain.ValueObjects;

namespace Vessel.Application.Auth;

/// <summary>
///     Team membership management (IAM-03). Role changes follow <see cref="TeamRolePolicy" />: only owners and admins
///     manage members, nobody acts on or grants a role above their own (so admins never create, promote to, demote or
///     remove owners), demotions and removals revoke the member's team tokens in the same save, and promotions keep
///     them. Members' fine-grained access (IAM-05) is edited here as well.
/// </summary>
public sealed class VesselTeamService
{
    private readonly IAccessResolver _access;
    private readonly IAuditWriter _auditWriter;
    private readonly AuthEmailService? _authEmails;
    private readonly ITokenGenerator _tokenGenerator;
    private readonly IVesselDbContext _unitOfWork;

    public VesselTeamService(
        IVesselDbContext unitOfWork,
        ITokenGenerator tokenGenerator,
        IAuditWriter auditWriter,
        AuthEmailService? authEmails = null)
        : this(unitOfWork, tokenGenerator, auditWriter,
            new AccessResolver(unitOfWork, NoApiTokenAccessContext.Instance), authEmails)
    {
    }

    public VesselTeamService(
        IVesselDbContext unitOfWork,
        ITokenGenerator tokenGenerator,
        IAuditWriter auditWriter,
        IAccessResolver access,
        AuthEmailService? authEmails = null)
    {
        _authEmails = authEmails;
        _unitOfWork = unitOfWork;
        _tokenGenerator = tokenGenerator;
        _auditWriter = auditWriter;
        _access = access;
    }

    public IReadOnlyList<TeamMemberSummary> ListMembers(UserId actorUserId, TeamId teamId)
    {
        if (!IsTeamMember(actorUserId, teamId))
            throw new UnauthorizedAccessException("User is not a member of this team.");

        var memberships = _unitOfWork.TeamMemberships
            .Where(membership => membership.TeamId == teamId)
            .Select(membership => new { membership.UserId, membership.Role, membership.AccessGrants })
            .ToArray();
        UserId[] userIds = memberships.Select(membership => membership.UserId).ToArray();
        var users = _unitOfWork.Users
            .Where(user => userIds.Contains(user.Id))
            .Select(user => new { user.Id, user.Name, user.Email })
            .ToArray()
            .ToDictionary(user => user.Id);

        return memberships
            .Where(membership => users.ContainsKey(membership.UserId))
            .Select(membership => new TeamMemberSummary(
                membership.UserId,
                users[membership.UserId].Name.Value,
                users[membership.UserId].Email.Value,
                membership.Role,
                membership.AccessGrants))
            .OrderByDescending(member => TeamRolePolicy.Rank(member.Role))
            .ThenBy(member => member.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<TeamSummary> ListTeams(UserId userId)
    {
        return _unitOfWork.TeamMemberships
            .Where(membership => membership.UserId == userId)
            .Join(
                _unitOfWork.Teams,
                membership => membership.TeamId,
                team => team.Id,
                (membership, team) => new TeamSummary(team.Id, team.Name.Value, team.IsPersonal, membership.Role))
            .OrderBy(team => team.Name)
            .ToArray();
    }

    public Task<TeamInvitationResult> InviteAsync(
        UserId actorUserId,
        TeamId teamId,
        string email,
        TeamRole role,
        DateTimeOffset expiresAt,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        return InviteAsync(actorUserId, teamId, email, role, null, expiresAt, correlationId, cancellationToken);
    }

    /// <summary>Invites a member; <paramref name="accessGrants" /> (members only) apply on acceptance.</summary>
    public async Task<TeamInvitationResult> InviteAsync(
        UserId actorUserId,
        TeamId teamId,
        string email,
        TeamRole role,
        IReadOnlyList<AccessGrant>? accessGrants,
        DateTimeOffset expiresAt,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        TeamRole actorRole = RequireManager(actorUserId, teamId);
        if (!TeamRolePolicy.CanInvite(actorRole, role))
            throw new UnauthorizedAccessException($"Your role cannot invite a member with the {role} role.");
        if (role != TeamRole.Member && accessGrants is not null)
            throw new InvalidOperationException(
                "Access grants apply to members only; owners and admins are unrestricted.");
        EnsureGrantTargetsExist(teamId, accessGrants);

        var plainTextToken = _tokenGenerator.GenerateUrlSafeToken();
        var invitation = TeamInvitation.Create(
            teamId,
            new EmailAddress(email),
            role,
            TokenHashing.Sha256(plainTextToken),
            expiresAt,
            DateTimeOffset.UtcNow,
            accessGrants);

        await _unitOfWork.TeamInvitationRepository.AddAsync(invitation, cancellationToken);
        // IAM-08: staged so the invitation and its audit row commit together; the address is masked on write.
        await _auditWriter.StageAsync(new AuditEntry(teamId, actorUserId, AuditActions.TeamInvitationCreated,
            new AuditTarget("team_invitation", invitation.Id.ToString()))
        {
            CorrelationId = correlationId,
            Metadata = new Dictionary<string, object?>
            {
                ["email"] = invitation.Email.Value,
                ["role"] = invitation.Role.ToString(),
                ["access"] = DescribeAccess(invitation.AccessGrants)
            }
        }, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        var presentedToken = $"{invitation.Id.Value:N}|{plainTextToken}";
        if (_authEmails is not null)
        {
            // IAM-10: send the invitation link through the instance mailer (link from the configured instance URL).
            Team? team = await _unitOfWork.TeamRepository.GetByIdAsync(teamId, cancellationToken);
            _authEmails.QueueInvitation(invitation.Email.Value, team?.Name.Value ?? "a team", presentedToken,
                invitation.ExpiresAt);
        }

        return new TeamInvitationResult(invitation.Id, presentedToken, invitation.ExpiresAt);
    }

    public async Task<bool> AcceptInvitationAsync(
        UserId userId,
        string presentedToken,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        var parts = presentedToken.Split('|', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out Guid invitationGuid)) return false;

        TeamInvitation? invitation = await _unitOfWork.TeamInvitationRepository.GetByIdAsync(
            new TeamInvitationId(invitationGuid),
            cancellationToken);
        User? user = await _unitOfWork.UserRepository.GetByIdAsync(userId, cancellationToken);
        if (invitation is null
            || user is null
            || !invitation.IsValid(DateTimeOffset.UtcNow)
            || invitation.Email != user.Email
            || !string.Equals(invitation.TokenHash, TokenHashing.Sha256(parts[1]), StringComparison.Ordinal))
            return false;

        Team team = await _unitOfWork.TeamRepository.GetByIdAsync(invitation.TeamId, cancellationToken)
                    ?? throw new InvalidOperationException("Invitation team was not found.");
        if (!team.Memberships.Any(membership => membership.UserId == userId))
            team.AddMember(userId, invitation.Role, DateTimeOffset.UtcNow, invitation.AccessGrants);
        invitation.Accept(DateTimeOffset.UtcNow);
        await _auditWriter.StageAsync(new AuditEntry(team.Id, userId, AuditActions.TeamInvitationAccepted,
            new AuditTarget("team_invitation", invitation.Id.ToString())) { CorrelationId = correlationId },
            cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task ChangeRoleAsync(
        UserId actorUserId,
        TeamId teamId,
        UserId memberUserId,
        TeamRole role,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        TeamRole actorRole = RequireManager(actorUserId, teamId);
        Team team = await GetTeamAsync(teamId, cancellationToken);
        TeamRole currentRole = team.GetMemberRole(memberUserId);
        if (!TeamRolePolicy.CanChangeRole(actorRole, currentRole, role))
            throw new UnauthorizedAccessException("Your role cannot change this member to that role.");

        DateTimeOffset now = DateTimeOffset.UtcNow;
        team.ChangeMemberRole(memberUserId, role, now);
        // A demotion revokes the member's tokens for this team in the same save; a promotion keeps them (the API
        // checks abilities against the current role on every request).
        var revoked = TeamRolePolicy.IsDemotion(currentRole, role) ? RevokeTeamTokens(memberUserId, teamId, now) : 0;
        await _auditWriter.StageAsync(new AuditEntry(teamId, actorUserId, AuditActions.TeamMemberRoleChanged,
            new AuditTarget("user", memberUserId.ToString()))
        {
            CorrelationId = correlationId,
            Changes = [new AuditFieldChange("role", currentRole.ToString(), role.ToString())],
            Metadata = new Dictionary<string, object?>
            {
                ["role"] = role.ToString(),
                ["previousRole"] = currentRole.ToString(),
                ["revokedApiCredentials"] = revoked
            }
        }, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveMemberAsync(
        UserId actorUserId,
        TeamId teamId,
        UserId memberUserId,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        TeamRole actorRole = RequireManager(actorUserId, teamId);
        Team team = await GetTeamAsync(teamId, cancellationToken);
        if (!TeamRolePolicy.CanRemove(actorRole, team.GetMemberRole(memberUserId)))
            throw new UnauthorizedAccessException("Your role cannot remove this member.");

        DateTimeOffset now = DateTimeOffset.UtcNow;
        team.RemoveMember(memberUserId, now);

        // A removed member's tokens for this team are revoked so they cannot be revived by a later re-invitation.
        // Token validation also re-checks membership on every request.
        var revoked = RevokeTeamTokens(memberUserId, teamId, now);
        // IAM-09: a removed member's remembered team is cleared; their sessions in this team move to their home team
        // on the next request (session validation re-checks membership).
        User? member = await _unitOfWork.UserRepository.GetByIdAsync(memberUserId, cancellationToken);
        member?.ClearCurrentTeam(teamId);
        await _auditWriter.StageAsync(new AuditEntry(teamId, actorUserId, AuditActions.TeamMemberRemoved,
            new AuditTarget("user", memberUserId.ToString()))
        {
            CorrelationId = correlationId,
            ResourceName = member?.Name.Value,
            Metadata = new Dictionary<string, object?> { ["revokedApiCredentials"] = revoked }
        }, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public bool IsTeamMember(UserId userId, TeamId teamId)
    {
        return _unitOfWork.TeamMemberships.Any(membership =>
            membership.UserId == userId && membership.TeamId == teamId);
    }

    /// <summary>
    ///     Replaces a member's fine-grained access (IAM-05). <c>null</c> restores the default team-wide read. Only owners
    ///     and admins edit access, and only on rows with the Member role.
    /// </summary>
    public async Task SetMemberAccessAsync(
        UserId actorUserId,
        TeamId teamId,
        UserId memberUserId,
        IReadOnlyList<AccessGrant>? accessGrants,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        TeamRole actorRole = RequireManager(actorUserId, teamId);
        Team team = await GetTeamAsync(teamId, cancellationToken);
        if (!TeamRolePolicy.CanEditAccess(actorRole, team.GetMemberRole(memberUserId)))
            throw new UnauthorizedAccessException("Access grants can only be edited for members.");
        EnsureGrantTargetsExist(teamId, accessGrants);

        var previousAccess = DescribeAccess(team.Memberships.Single(membership => membership.UserId == memberUserId)
            .AccessGrants);
        team.SetMemberAccess(memberUserId, accessGrants, DateTimeOffset.UtcNow);
        await _auditWriter.StageAsync(new AuditEntry(teamId, actorUserId, AuditActions.TeamMemberAccessChanged,
            new AuditTarget("user", memberUserId.ToString()))
        {
            CorrelationId = correlationId,
            Changes = [new AuditFieldChange("access", previousAccess, DescribeAccess(accessGrants))],
            Metadata = new Dictionary<string, object?> { ["access"] = DescribeAccess(accessGrants) }
        }, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    internal static string DescribeAccess(IReadOnlyList<AccessGrant>? grants)
    {
        if (grants is null) return "default (team-wide read)";
        if (grants.Count == 0) return "none";
        return string.Join("; ", grants.Select(grant =>
            grant.Scope.ToString().ToLowerInvariant()
            + (grant.TargetId is { } id ? ":" + id.ToString("D") : string.Empty)
            + "=" + string.Join(',', grant.Levels.Select(level => level.ToAbility()))));
    }

    private TeamRole RequireManager(UserId userId, TeamId teamId)
    {
        TeamRole? role = _unitOfWork.TeamMemberships
            .Where(membership => membership.UserId == userId && membership.TeamId == teamId)
            .Select(membership => (TeamRole?)membership.Role)
            .SingleOrDefault();

        if (role is not { } actorRole || !TeamRolePolicy.CanManageMembers(actorRole))
            throw new UnauthorizedAccessException("User cannot manage this team.");
        return actorRole;
    }

    private void EnsureGrantTargetsExist(TeamId teamId, IReadOnlyList<AccessGrant>? grants)
    {
        if (grants is null) return;
        if (grants.Count > AccessGrant.MaxGrantsPerActor)
            throw new InvalidOperationException($"At most {AccessGrant.MaxGrantsPerActor} access grants are allowed.");
        if (grants.Any(grant => grant.Levels.Count == 0))
            throw new InvalidOperationException("Every access grant needs at least one level.");
        if (grants.Any(grant => _access.LocateGrant(teamId, grant) is null))
            throw new InvalidOperationException(
                "Every access grant must point at this team or one of its projects, environments or resources.");
    }

    private int RevokeTeamTokens(UserId userId, TeamId teamId, DateTimeOffset now)
    {
        PersonalAccessToken[] tokens = _unitOfWork.PersonalAccessTokens
            .Where(token => token.UserId == userId && token.TeamId == teamId && token.RevokedAt == null)
            .ToArray();
        foreach (PersonalAccessToken token in tokens) token.Revoke(now);
        return tokens.Length;
    }

    private async Task<Team> GetTeamAsync(TeamId teamId, CancellationToken cancellationToken)
    {
        return await _unitOfWork.TeamRepository.GetByIdAsync(teamId, cancellationToken)
               ?? throw new InvalidOperationException("Team was not found.");
    }
}
