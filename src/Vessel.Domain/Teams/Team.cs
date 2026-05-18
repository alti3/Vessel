using Vessel.Domain.Common;
using Vessel.Domain.ValueObjects;

namespace Vessel.Domain.Teams;

public sealed class Team : Entity<TeamId>
{
    private readonly List<TeamMembership> _memberships = [];

    private Team()
    {
    }

    private Team(
        TeamId id,
        DisplayName name,
        Description? description,
        bool isPersonal,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        Name = name;
        Description = description;
        IsPersonal = isPersonal;
    }

    public DisplayName Name { get; private set; }

    public Description? Description { get; private set; }

    public bool IsPersonal { get; private set; }

    public bool ShowOnboarding { get; private set; } = true;

    public int? CustomServerLimit { get; private set; }

    public IReadOnlyCollection<TeamMembership> Memberships => _memberships.AsReadOnly();

    public static Team Create(
        DisplayName name,
        UserId ownerId,
        bool isPersonal,
        DateTimeOffset now,
        Description? description = null)
    {
        var team = new Team(TeamId.New(), name, description, isPersonal, now);
        team._memberships.Add(new TeamMembership(team.Id, ownerId, TeamRole.Owner, now));
        team.AddDomainEvent(new TeamCreatedEvent(team.Id, now));
        team.AddDomainEvent(new TeamMemberAddedEvent(team.Id, ownerId, TeamRole.Owner, now));

        return team;
    }

    public void Rename(DisplayName name, DateTimeOffset now)
    {
        Name = name;
        Touch(now);
    }

    public void SetServerLimit(int? serverLimit, DateTimeOffset now)
    {
        if (serverLimit is < 0) throw new DomainException("Server limit cannot be negative.");

        CustomServerLimit = serverLimit;
        Touch(now);
    }

    public void AddMember(UserId userId, TeamRole role, DateTimeOffset now)
    {
        if (_memberships.Any(member => member.UserId == userId))
            throw new DomainException("User is already a member of this team.");

        _memberships.Add(new TeamMembership(Id, userId, role, now));
        Touch(now);
        AddDomainEvent(new TeamMemberAddedEvent(Id, userId, role, now));
    }

    public void ChangeMemberRole(UserId userId, TeamRole role, DateTimeOffset now)
    {
        TeamMembership membership = FindMembership(userId);
        var wouldRemoveLastOwner = membership.IsOwner
                                   && role != TeamRole.Owner
                                   && _memberships.Count(member => member.IsOwner) == 1;

        if (wouldRemoveLastOwner) throw new DomainException("A team must have at least one owner.");

        membership.ChangeRole(role, now);
        Touch(now);
        AddDomainEvent(new TeamMemberRoleChangedEvent(Id, userId, role, now));
    }

    public void RemoveMember(UserId userId, DateTimeOffset now)
    {
        TeamMembership membership = FindMembership(userId);
        if (membership.IsOwner && _memberships.Count(member => member.IsOwner) == 1)
            throw new DomainException("A team must have at least one owner.");

        _memberships.Remove(membership);
        Touch(now);
    }

    private TeamMembership FindMembership(UserId userId)
    {
        return _memberships.SingleOrDefault(member => member.UserId == userId)
               ?? throw new DomainException("User is not a member of this team.");
    }
}
