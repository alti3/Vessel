using Microsoft.Extensions.Options;
using Vessel.Application.Auth;
using Vessel.Application.Authorization;
using Vessel.Application.Instance;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Settings;
using Vessel.Domain.Teams;
using Vessel.Domain.Users;
using Vessel.Domain.ValueObjects;
using Vessel.UnitTests.Authorization;

namespace Vessel.UnitTests.Security;

/// <summary>IAM-06: registration policy (closed after the bootstrap account) and instance-administrator management.</summary>
public sealed class InstanceAdministrationTests
{
    private const string Password = "Correct-Horse-Battery-9";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Registration_IsOpenForTheFirstAccount_ThenClosedByDefault()
    {
        InMemoryVesselDb store = InMemoryVesselDb.Create();
        VesselAuthenticationService authentication = CreateAuthentication(store);

        await authentication.RegisterAsync("Root", "root@example.com", Password, null, Ct);
        await Assert.ThrowsAsync<RegistrationClosedException>(() =>
            authentication.RegisterAsync("Late", "late@example.com", Password, null, Ct));

        Assert.True(store.Set<User>().Single().IsInstanceAdmin);
    }

    [Fact]
    public async Task Registration_AllowsInvitedEmails_AndOpensWhenAnAdminEnablesIt()
    {
        InMemoryVesselDb store = InMemoryVesselDb.Create();
        VesselAuthenticationService authentication = CreateAuthentication(store);
        await authentication.RegisterAsync("Root", "root@example.com", Password, null, Ct);
        User root = store.Set<User>().Single();
        Team team = store.Set<Team>().Single();
        store.Set<TeamInvitation>().Add(TeamInvitation.Create(team.Id, new EmailAddress("invited@example.com"),
            TeamRole.Member, "hash", DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow));

        await authentication.RegisterAsync("Invited", "invited@example.com", Password, null, Ct);
        await Assert.ThrowsAsync<RegistrationClosedException>(() =>
            authentication.RegisterAsync("Other", "other@example.com", Password, null, Ct));

        InstanceSettingsService instance = CreateInstance(store);
        await instance.UpdateAsync(root.Id, new UpdateInstanceSettingsRequest(true), null, Ct);
        Assert.Single(store.Set<SettingEntry>());
        await authentication.RegisterAsync("Other", "other@example.com", Password, null, Ct);

        Assert.Equal(3, store.Set<User>().Count);
        Assert.Equal(1, store.Set<User>().Count(user => user.IsInstanceAdmin));
    }

    [Fact]
    public async Task InstanceSettings_RequireAnInstanceAdministrator_NotATeamOwner()
    {
        var fixture = AccessFixture.Create();
        InstanceSettingsService instance = CreateInstance(fixture.Store);

        Assert.Throws<UnauthorizedAccessException>(() => instance.Get(fixture.Owner.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            instance.UpdateAsync(fixture.Owner.Id, new UpdateInstanceSettingsRequest(true), null, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            instance.GrantAdminAsync(fixture.Owner.Id, fixture.Owner.Id, null, Ct));

        fixture.Admin.GrantInstanceAdmin(fixture.Now);
        Assert.False(instance.Get(fixture.Admin.Id).RegistrationEnabled);
    }

    [Fact]
    public async Task InstanceAdmins_ArePromotedAndDemoted_ButTheLastOneIsProtected()
    {
        var fixture = AccessFixture.Create();
        fixture.Owner.GrantInstanceAdmin(fixture.Now);
        InstanceSettingsService instance = CreateInstance(fixture.Store);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            instance.RevokeAdminAsync(fixture.Owner.Id, fixture.Owner.Id, null, Ct));

        await instance.GrantAdminAsync(fixture.Owner.Id, fixture.Member.Id, null, Ct);
        Assert.Equal(2, instance.ListAdmins(fixture.Owner.Id).Count);
        await instance.RevokeAdminAsync(fixture.Member.Id, fixture.Owner.Id, null, Ct);

        Assert.False(fixture.Owner.IsInstanceAdmin);
        Assert.True(fixture.Member.IsInstanceAdmin);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            instance.RevokeAdminAsync(fixture.Member.Id, fixture.Member.Id, null, Ct));
    }

    [Fact]
    public void InstanceManagement_ThroughATokenNeedsRoot()
    {
        var fixture = AccessFixture.Create();
        fixture.Owner.GrantInstanceAdmin(fixture.Now);
        var writeToken = new AccessGrantTests.FixedTokenContext(new ApiTokenAccess(PersonalAccessTokenId.New(),
            fixture.Owner.Id, fixture.Team.Id, new HashSet<string> { TokenScopeMapper.Write }, null));
        var rootToken = new AccessGrantTests.FixedTokenContext(new ApiTokenAccess(PersonalAccessTokenId.New(),
            fixture.Owner.Id, fixture.Team.Id, new HashSet<string> { TokenScopeMapper.Root }, null));

        Assert.True(fixture.Authorization().IsInstanceManager(fixture.Owner.Id));
        Assert.False(fixture.Authorization(writeToken).IsInstanceManager(fixture.Owner.Id));
        Assert.True(fixture.Authorization(rootToken).IsInstanceManager(fixture.Owner.Id));
    }

    private static InstanceSettingsService CreateInstance(InMemoryVesselDb store)
    {
        return new InstanceSettingsService(store.Context, new VesselAuthorizationService(store.Context),
            new AccessGrantTests.NullAuditWriter(), TimeProvider.System);
    }

    private static VesselAuthenticationService CreateAuthentication(InMemoryVesselDb store)
    {
        var tokenGenerator = new SequentialTokenGenerator();
        return new VesselAuthenticationService(store.Context, new FakePasswordHasher(), tokenGenerator,
            new TotpService(tokenGenerator), new AccessGrantTests.NullAuditWriter(),
            Options.Create(new AuthOptions()),
            new Vessel.UnitTests.Application.SecurityHardening.FakeSecretCipher("v1"));
    }
}
