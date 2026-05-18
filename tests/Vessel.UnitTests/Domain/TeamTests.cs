using Vessel.Domain;
using Vessel.Domain.Common;
using Vessel.Domain.Teams;
using Vessel.Domain.ValueObjects;

namespace Vessel.UnitTests.Domain;

public sealed class TeamTests
{
    [Fact]
    public void RemoveMember_RejectsRemovingLastOwner()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var ownerId = UserId.New();
        var team = Team.Create(new DisplayName("Platform"), ownerId, false, now);

        Assert.Throws<DomainException>(() => team.RemoveMember(ownerId, now.AddMinutes(1)));
    }

    [Fact]
    public void ChangeMemberRole_RejectsDemotingLastOwner()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var ownerId = UserId.New();
        var team = Team.Create(new DisplayName("Platform"), ownerId, false, now);

        Assert.Throws<DomainException>(() => team.ChangeMemberRole(ownerId, TeamRole.Member, now.AddMinutes(1)));
    }
}
