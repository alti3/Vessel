using Vessel.Application.Auth;
using Vessel.Application.Authorization;
using Vessel.Domain;
using Vessel.Domain.Teams;
using Vessel.Domain.Users;
using Vessel.UnitTests.Authorization;

namespace Vessel.UnitTests.Security;

/// <summary>IAM-03 role hierarchy and token revocation; IAM-04 token pause, resume, roll, edit and per-team lists.</summary>
public sealed class TeamRoleAndTokenLifecycleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    // actor, current, new, allowed
    [InlineData(TeamRole.Owner, TeamRole.Admin, TeamRole.Owner, true)]
    [InlineData(TeamRole.Owner, TeamRole.Owner, TeamRole.Member, true)]
    [InlineData(TeamRole.Admin, TeamRole.Member, TeamRole.Admin, true)]
    [InlineData(TeamRole.Admin, TeamRole.Admin, TeamRole.Member, true)]
    [InlineData(TeamRole.Admin, TeamRole.Admin, TeamRole.Owner, false)]
    [InlineData(TeamRole.Admin, TeamRole.Member, TeamRole.Owner, false)]
    [InlineData(TeamRole.Admin, TeamRole.Owner, TeamRole.Admin, false)]
    [InlineData(TeamRole.Admin, TeamRole.Owner, TeamRole.Member, false)]
    [InlineData(TeamRole.Member, TeamRole.Member, TeamRole.Admin, false)]
    [InlineData(TeamRole.Member, TeamRole.Member, TeamRole.Member, false)]
    public void ChangeRoleMatrix(TeamRole actor, TeamRole current, TeamRole requested, bool allowed)
    {
        Assert.Equal(allowed, TeamRolePolicy.CanChangeRole(actor, current, requested));
    }

    [Theory]
    [InlineData(TeamRole.Owner, TeamRole.Owner, true)]
    [InlineData(TeamRole.Owner, TeamRole.Admin, true)]
    [InlineData(TeamRole.Admin, TeamRole.Admin, true)]
    [InlineData(TeamRole.Admin, TeamRole.Member, true)]
    [InlineData(TeamRole.Admin, TeamRole.Owner, false)]
    [InlineData(TeamRole.Member, TeamRole.Member, false)]
    public void InviteAndRemoveMatrix(TeamRole actor, TeamRole target, bool allowed)
    {
        Assert.Equal(allowed, TeamRolePolicy.CanInvite(actor, target));
        Assert.Equal(allowed, TeamRolePolicy.CanRemove(actor, target));
    }

    [Fact]
    public async Task Admin_CannotCreatePromoteToDemoteOrRemoveOwners()
    {
        var fixture = AccessFixture.Create();
        VesselTeamService teams = fixture.Services.Get<VesselTeamService>();
        UserId admin = fixture.Admin.Id;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => teams.InviteAsync(admin, fixture.Team.Id,
            "new-owner@example.com", TeamRole.Owner, fixture.Now.AddDays(1), null, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            teams.ChangeRoleAsync(admin, fixture.Team.Id, admin, TeamRole.Owner, null, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            teams.ChangeRoleAsync(admin, fixture.Team.Id, fixture.Member.Id, TeamRole.Owner, null, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            teams.ChangeRoleAsync(admin, fixture.Team.Id, fixture.Owner.Id, TeamRole.Member, null, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            teams.RemoveMemberAsync(admin, fixture.Team.Id, fixture.Owner.Id, null, Ct));

        Assert.Equal(TeamRole.Owner, fixture.Team.GetMemberRole(fixture.Owner.Id));
        Assert.Equal(TeamRole.Admin, fixture.Team.GetMemberRole(admin));
    }

    [Fact]
    public async Task Members_CannotManageMembers()
    {
        var fixture = AccessFixture.Create();
        VesselTeamService teams = fixture.Services.Get<VesselTeamService>();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => teams.InviteAsync(fixture.Member.Id,
            fixture.Team.Id, "friend@example.com", TeamRole.Member, fixture.Now.AddDays(1), null, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            teams.RemoveMemberAsync(fixture.Member.Id, fixture.Team.Id, fixture.Developer.Id, null, Ct));
    }

    [Fact]
    public async Task Owner_CanPromoteToOwner_AndAdminCanManageMembersAndAdmins()
    {
        var fixture = AccessFixture.Create();
        VesselTeamService teams = fixture.Services.Get<VesselTeamService>();

        await teams.ChangeRoleAsync(fixture.Admin.Id, fixture.Team.Id, fixture.Member.Id, TeamRole.Admin, null, Ct);
        await teams.InviteAsync(fixture.Admin.Id, fixture.Team.Id, "x@example.com", TeamRole.Admin,
            fixture.Now.AddDays(1), null, Ct);
        await teams.ChangeRoleAsync(fixture.Owner.Id, fixture.Team.Id, fixture.Admin.Id, TeamRole.Owner, null, Ct);

        Assert.Equal(TeamRole.Admin, fixture.Team.GetMemberRole(fixture.Member.Id));
        Assert.Equal(TeamRole.Owner, fixture.Team.GetMemberRole(fixture.Admin.Id));
    }

    [Fact]
    public async Task Demotion_RevokesTeamTokens_PromotionKeepsThem()
    {
        var fixture = AccessFixture.Create();
        VesselTokenService tokens = fixture.Services.Get<VesselTokenService>();
        VesselTeamService teams = fixture.Services.Get<VesselTeamService>();
        CreatedToken memberToken = await tokens.CreateAsync(fixture.Member.Id, fixture.Team.Id, "m",
            [TokenScopeMapper.Read], null, null, null, Ct);
        CreatedToken adminToken = await tokens.CreateAsync(fixture.Admin.Id, fixture.Team.Id, "a",
            [TokenScopeMapper.Write], null, null, null, Ct);

        await teams.ChangeRoleAsync(fixture.Owner.Id, fixture.Team.Id, fixture.Member.Id, TeamRole.Admin, null, Ct);
        await teams.ChangeRoleAsync(fixture.Owner.Id, fixture.Team.Id, fixture.Admin.Id, TeamRole.Member, null, Ct);

        Assert.True((await tokens.ValidateAsync(memberToken.PlainTextToken, Ct)).Succeeded);
        Assert.False((await tokens.ValidateAsync(adminToken.PlainTextToken, Ct)).Succeeded);
        Assert.True(fixture.Store.Set<PersonalAccessToken>().Single(token => token.Id == adminToken.Id).IsRevoked);
    }

    [Fact]
    public async Task PausedToken_CannotAuthenticate_UntilResumed()
    {
        var fixture = AccessFixture.Create();
        VesselTokenService tokens = fixture.Services.Get<VesselTokenService>();
        CreatedToken token = await tokens.CreateAsync(fixture.Owner.Id, fixture.Team.Id, "ci",
            [TokenScopeMapper.Read], null, null, null, Ct);

        TokenSummary? paused = await tokens.PauseAsync(fixture.Owner.Id, fixture.Team.Id, token.Id, null, Ct);
        Assert.NotNull(paused?.PausedAt);
        Assert.False((await tokens.ValidateAsync(token.PlainTextToken, Ct)).Succeeded);

        await tokens.ResumeAsync(fixture.Owner.Id, fixture.Team.Id, token.Id, null, Ct);
        Assert.True((await tokens.ValidateAsync(token.PlainTextToken, Ct)).Succeeded);

        // Another user (even an admin of the team) cannot pause someone else's token.
        Assert.Null(await tokens.PauseAsync(fixture.Admin.Id, fixture.Team.Id, token.Id, null, Ct));
    }

    [Fact]
    public async Task RolledToken_GetsANewSecret_AndKeepsItsMetadata()
    {
        var fixture = AccessFixture.Create();
        VesselTokenService tokens = fixture.Services.Get<VesselTokenService>();
        CreatedToken original = await tokens.CreateAsync(fixture.Owner.Id, fixture.Team.Id, "ci",
            [TokenScopeMapper.Deploy], null, null, null, Ct);

        CreatedToken rolled = (await tokens.RollAsync(fixture.Owner.Id, fixture.Team.Id, original.Id, null, Ct))!;

        Assert.Equal(original.Id, rolled.Id);
        Assert.NotEqual(original.PlainTextToken, rolled.PlainTextToken);
        Assert.False((await tokens.ValidateAsync(original.PlainTextToken, Ct)).Succeeded);
        AuthTokenValidationResult validated = await tokens.ValidateAsync(rolled.PlainTextToken, Ct);
        Assert.True(validated.Succeeded);
        Assert.Contains(TokenScopeMapper.Deploy, validated.Scopes);
    }

    [Fact]
    public async Task EditedToken_KeepsItsSecret_AndCannotExceedTheOwner()
    {
        var fixture = AccessFixture.Create();
        VesselTokenService tokens = fixture.Services.Get<VesselTokenService>();
        CreatedToken token = await tokens.CreateAsync(fixture.Member.Id, fixture.Team.Id, "ci",
            [TokenScopeMapper.Read], null, null, null, Ct);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tokens.UpdateAsync(fixture.Member.Id,
            fixture.Team.Id, token.Id, new UpdateTokenRequest("ci", [TokenScopeMapper.Deploy], null, null), null,
            Ct));
        TokenSummary? updated = await tokens.UpdateAsync(fixture.Member.Id, fixture.Team.Id, token.Id,
            new UpdateTokenRequest("renamed", [TokenScopeMapper.Read], null, fixture.Now.AddDays(3)), null, Ct);

        Assert.Equal("renamed", updated!.Name);
        Assert.NotNull(updated.ExpiresAt);
        Assert.True((await tokens.ValidateAsync(token.PlainTextToken, Ct)).Succeeded);
    }

    [Fact]
    public async Task TokenList_ShowsOnlyTheActiveTeam()
    {
        var fixture = AccessFixture.Create();
        VesselTokenService tokens = fixture.Services.Get<VesselTokenService>();
        await tokens.CreateAsync(fixture.Owner.Id, fixture.Team.Id, "team", [TokenScopeMapper.Read], null, null,
            null, Ct);
        await tokens.CreateAsync(fixture.Owner.Id, fixture.OtherTeam.Id, "other", [TokenScopeMapper.Read], null,
            null, null, Ct);

        Assert.Equal(["team"], tokens.List(fixture.Owner.Id, fixture.Team.Id).Select(token => token.Name));
        Assert.Equal(["other"], tokens.List(fixture.Owner.Id, fixture.OtherTeam.Id).Select(token => token.Name));
    }
}
