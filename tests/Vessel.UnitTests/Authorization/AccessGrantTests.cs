using Vessel.Application.Auditing;
using Vessel.Application.Auth;
using Vessel.Application.Authorization;
using Vessel.Application.Resources;
using Vessel.Domain;
using Vessel.Domain.Access;
using Vessel.Domain.Applications;
using Vessel.Domain.Auditing;
using Vessel.Domain.Projects;
using Vessel.Domain.Servers;
using Vessel.Domain.Teams;
using Vessel.Domain.Users;
using Vessel.Domain.ValueObjects;
using Vessel.UnitTests.Security;
using AppEntity = Vessel.Domain.Applications.Application;
using EnvironmentEntity = Vessel.Domain.Projects.Environment;

namespace Vessel.UnitTests.Authorization;

/// <summary>
///     IAM-03/IAM-04/IAM-05: access level hierarchy, default read-only members, grant inheritance, the token layer
///     (a token never exceeds its owner), team-wide-only operations and list filtering.
/// </summary>
public sealed class AccessGrantTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(AccessLevel.Root, new[] { AccessLevel.Read, AccessLevel.ReadSensitive, AccessLevel.Deploy, AccessLevel.Write, AccessLevel.Delete, AccessLevel.Root }, new AccessLevel[0])]
    [InlineData(AccessLevel.Write, new[] { AccessLevel.Read, AccessLevel.ReadSensitive, AccessLevel.Deploy, AccessLevel.Write }, new[] { AccessLevel.Delete, AccessLevel.Root })]
    [InlineData(AccessLevel.Delete, new[] { AccessLevel.Read, AccessLevel.Delete }, new[] { AccessLevel.ReadSensitive, AccessLevel.Deploy, AccessLevel.Write, AccessLevel.Root })]
    [InlineData(AccessLevel.Deploy, new[] { AccessLevel.Read, AccessLevel.Deploy }, new[] { AccessLevel.ReadSensitive, AccessLevel.Write, AccessLevel.Delete, AccessLevel.Root })]
    [InlineData(AccessLevel.ReadSensitive, new[] { AccessLevel.Read, AccessLevel.ReadSensitive }, new[] { AccessLevel.Deploy, AccessLevel.Write, AccessLevel.Delete, AccessLevel.Root })]
    [InlineData(AccessLevel.Read, new[] { AccessLevel.Read }, new[] { AccessLevel.ReadSensitive, AccessLevel.Deploy, AccessLevel.Write, AccessLevel.Delete, AccessLevel.Root })]
    public void AccessLevelHierarchy_MatchesUpstream(AccessLevel level, AccessLevel[] included, AccessLevel[] excluded)
    {
        IReadOnlySet<AccessLevel> expanded = AccessLevels.Expand([level]);

        foreach (AccessLevel expected in included) Assert.Contains(expected, expanded);
        foreach (AccessLevel unexpected in excluded) Assert.DoesNotContain(unexpected, expanded);
    }

    [Fact]
    public void TokenAbilities_MapDeleteAndDeployToDistinctPermissions()
    {
        IReadOnlySet<string> write = TokenScopeMapper.ToPermissions([TokenScopeMapper.Write]);
        IReadOnlySet<string> delete = TokenScopeMapper.ToPermissions([TokenScopeMapper.Delete]);
        IReadOnlySet<string> deploy = TokenScopeMapper.ToPermissions([TokenScopeMapper.Deploy]);

        Assert.Contains(VesselPermissions.DeploymentsStart, write);
        Assert.Contains(VesselPermissions.SecretsRead, write);
        Assert.DoesNotContain(VesselPermissions.ProjectsDelete, write);
        Assert.Contains(VesselPermissions.ProjectsDelete, delete);
        Assert.DoesNotContain(VesselPermissions.ProjectsWrite, delete);
        Assert.Contains(VesselPermissions.DeploymentsStart, deploy);
        Assert.Contains(VesselPermissions.DeploymentsCancel, deploy);
        Assert.Contains(VesselPermissions.ResourcesLifecycle, deploy);
        Assert.DoesNotContain(VesselPermissions.ApplicationsWrite, deploy);
        Assert.DoesNotContain(VesselPermissions.InstanceManage, write);
        Assert.Throws<InvalidOperationException>(() => TokenScopeMapper.Serialize(["admin"]));
    }

    [Theory]
    [InlineData("read write", "delete read write")]
    [InlineData("write", "delete write")]
    [InlineData("delete write", "delete write")]
    [InlineData("read deploy", "read deploy")]
    [InlineData("root", "root")]
    public void LegacyTokenMigration_AddsDeleteToWriteTokens(string stored, string expected)
    {
        Assert.Equal(expected, TokenScopeMapper.MigrateLegacyScopes(stored));
    }

    [Fact]
    public void DefaultMember_IsReadOnly()
    {
        var fixture = AccessFixture.Create();
        VesselAuthorizationService authorization = fixture.Authorization();
        UserId member = fixture.Member.Id;

        Assert.DoesNotContain(VesselPermissions.DeploymentsStart, VesselPermissions.ForRole(TeamRole.Member));
        Assert.DoesNotContain(VesselPermissions.DeploymentsCancel, VesselPermissions.ForRole(TeamRole.Member));
        Assert.True(authorization.IsAuthorized(member, fixture.Team.Id, App(fixture.App1), VesselPermissions.ApplicationsRead));
        Assert.False(authorization.IsAuthorized(member, fixture.Team.Id, App(fixture.App1), VesselPermissions.DeploymentsStart));
        Assert.False(authorization.IsAuthorized(member, fixture.Team.Id, App(fixture.App1), VesselPermissions.DeploymentsCancel));
        Assert.False(authorization.IsAuthorized(member, fixture.Team.Id, App(fixture.App1), VesselPermissions.ApplicationsWrite));
        Assert.False(authorization.IsAuthorized(member, fixture.Team.Id, ResourceRef.Project(fixture.Project1.Id), VesselPermissions.ProjectsDelete));
        Assert.False(authorization.CanReadSensitive(member, fixture.Team.Id));
        Assert.True(authorization.CanReadSensitive(fixture.Owner.Id, fixture.Team.Id));
    }

    [Fact]
    public void ProjectGrant_AppliesToItsChildren_NotToSiblingsOrTeamWideOperations()
    {
        var fixture = AccessFixture.Create();
        VesselAuthorizationService authorization = fixture.Authorization();
        UserId developer = fixture.Developer.Id;

        Assert.True(authorization.IsAuthorized(developer, fixture.Team.Id, App(fixture.App1), VesselPermissions.ApplicationsWrite));
        Assert.True(authorization.IsAuthorized(developer, fixture.Team.Id, App(fixture.App1), VesselPermissions.DeploymentsStart));
        Assert.True(authorization.IsAuthorized(developer, fixture.Team.Id, App(fixture.App1), VesselPermissions.SecretsRead));
        Assert.True(authorization.IsAuthorized(developer, fixture.Team.Id, ResourceRef.Environment(fixture.Environment1.Id), VesselPermissions.ProjectsWrite));
        Assert.False(authorization.IsAuthorized(developer, fixture.Team.Id, ResourceRef.Project(fixture.Project1.Id), VesselPermissions.ProjectsDelete));
        Assert.False(authorization.IsAuthorized(developer, fixture.Team.Id, App(fixture.App2), VesselPermissions.ApplicationsRead));
        // Creating a project and other team-level operations need a team-wide grant.
        Assert.False(authorization.HasPermission(developer, fixture.Team.Id, VesselPermissions.ProjectsWrite));
        Assert.False(authorization.HasPermission(developer, fixture.Team.Id, VesselPermissions.NotificationsRead));
        Assert.True(authorization.HasPermissionSomewhere(developer, fixture.Team.Id, VesselPermissions.ApplicationsWrite));
    }

    [Fact]
    public void ResourceGrant_GivesOnlyItsLevelsOnThatResource()
    {
        var fixture = AccessFixture.Create();
        VesselAuthorizationService authorization = fixture.Authorization();
        UserId deployer = fixture.ResourceDeployer.Id;

        Assert.True(authorization.IsAuthorized(deployer, fixture.Team.Id, App(fixture.App2), VesselPermissions.DeploymentsStart));
        Assert.True(authorization.IsAuthorized(deployer, fixture.Team.Id, App(fixture.App2), VesselPermissions.ApplicationsRead));
        Assert.False(authorization.IsAuthorized(deployer, fixture.Team.Id, App(fixture.App2), VesselPermissions.ApplicationsWrite));
        Assert.False(authorization.IsAuthorized(deployer, fixture.Team.Id, App(fixture.App1), VesselPermissions.ApplicationsRead));
        Assert.False(authorization.IsAuthorized(deployer, fixture.Team.Id, ResourceRef.Project(fixture.Project2.Id), VesselPermissions.ProjectsRead));
    }

    [Fact]
    public void FullAccessGrant_NeverGrantsTeamAdministration()
    {
        var fixture = AccessFixture.Create();
        fixture.Team.SetMemberAccess(fixture.Member.Id, AccessPresets.Grants(AccessPresets.Full), fixture.Now);
        VesselAuthorizationService authorization = fixture.Authorization();

        Assert.True(authorization.IsAuthorized(fixture.Member.Id, fixture.Team.Id, App(fixture.App1), VesselPermissions.ApplicationsDelete));
        Assert.True(authorization.HasPermission(fixture.Member.Id, fixture.Team.Id, VesselPermissions.ServersWrite));
        Assert.False(authorization.HasPermission(fixture.Member.Id, fixture.Team.Id, VesselPermissions.TeamsManage));
        Assert.False(authorization.HasPermission(fixture.Member.Id, fixture.Team.Id, VesselPermissions.TerminalsOpen));
        Assert.False(authorization.HasPermission(fixture.Member.Id, fixture.Team.Id, VesselPermissions.SettingsManage));
        Assert.False(authorization.IsInstanceManager(fixture.Member.Id));
    }

    [Fact]
    public void Servers_AreUsableByScopedMembersOnlyWhenTheyRunAReachableResource()
    {
        var fixture = AccessFixture.Create();
        VesselAuthorizationService authorization = fixture.Authorization();
        UserId developer = fixture.Developer.Id;

        Assert.True(authorization.IsAuthorized(developer, fixture.Team.Id, ResourceRef.Server(fixture.Server1.Id), VesselPermissions.ApplicationsWrite));
        Assert.True(authorization.IsAuthorized(developer, fixture.Team.Id, ResourceRef.Server(fixture.Server1.Id), VesselPermissions.ServersRead));
        Assert.False(authorization.IsAuthorized(developer, fixture.Team.Id, ResourceRef.Server(fixture.Server2.Id), VesselPermissions.ServersRead));
        Assert.False(authorization.IsAuthorized(developer, fixture.Team.Id, ResourceRef.Server(fixture.Server1.Id), VesselPermissions.ServersWrite));
    }

    [Fact]
    public void Lists_AreFilteredForScopedMembers()
    {
        var fixture = AccessFixture.Create();
        ResourceManagementService resources = fixture.Services.Get<ResourceManagementService>();

        Assert.Equal([fixture.Project1.Id.Value], resources.ListProjects(fixture.Developer.Id, fixture.Team.Id).Select(project => project.Id));
        Assert.Equal([fixture.App1.Id.Value], resources.ListApplications(fixture.Developer.Id, fixture.Team.Id).Select(app => app.Id));
        Assert.Equal([fixture.Server1.Id.Value], resources.ListServers(fixture.Developer.Id, fixture.Team.Id).Select(server => server.Id));

        // A resource grant makes its parent project visible, but only the granted resource is listed.
        Assert.Equal([fixture.Project2.Id.Value], resources.ListProjects(fixture.ResourceDeployer.Id, fixture.Team.Id).Select(project => project.Id));
        Assert.Equal([fixture.App2.Id.Value], resources.ListApplications(fixture.ResourceDeployer.Id, fixture.Team.Id).Select(app => app.Id));
        Assert.Single(resources.ListEnvironments(fixture.ResourceDeployer.Id, fixture.Team.Id, fixture.Project2.Id));
        Assert.Throws<UnauthorizedAccessException>(() =>
            resources.ListEnvironments(fixture.ResourceDeployer.Id, fixture.Team.Id, fixture.Project1.Id));

        Assert.Equal(2, resources.ListProjects(fixture.Owner.Id, fixture.Team.Id).Count);
        Assert.Equal(2, resources.ListApplications(fixture.Member.Id, fixture.Team.Id).Count);
    }

    [Fact]
    public void TokenLayer_IntersectsWithTheOwner()
    {
        var fixture = AccessFixture.Create();
        // The owner is unrestricted, but the request token is limited to project 1 with read + deploy.
        var token = new FixedTokenContext(new ApiTokenAccess(PersonalAccessTokenId.New(), fixture.Owner.Id,
            fixture.Team.Id, new HashSet<string> { TokenScopeMapper.Read, TokenScopeMapper.Deploy },
            [AccessGrant.Project(fixture.Project1.Id, AccessLevel.Read)]));
        VesselAuthorizationService authorization = fixture.Authorization(token);

        Assert.True(authorization.IsAuthorized(fixture.Owner.Id, fixture.Team.Id, App(fixture.App1), VesselPermissions.DeploymentsStart));
        Assert.False(authorization.IsAuthorized(fixture.Owner.Id, fixture.Team.Id, App(fixture.App1), VesselPermissions.ApplicationsWrite));
        Assert.False(authorization.IsAuthorized(fixture.Owner.Id, fixture.Team.Id, App(fixture.App2), VesselPermissions.ApplicationsRead));
        Assert.False(authorization.HasPermission(fixture.Owner.Id, fixture.Team.Id, VesselPermissions.NotificationsRead));
        Assert.False(authorization.CanReadSensitive(fixture.Owner.Id, fixture.Team.Id, App(fixture.App1)));
        fixture.Services.TokenContext = token;
        Assert.Equal([fixture.App1.Id.Value],
            fixture.Services.Get<ResourceManagementService>().ListApplications(fixture.Owner.Id, fixture.Team.Id)
                .Select(app => app.Id));
        Assert.True(authorization.GetVisibility(fixture.Owner.Id, fixture.Team.Id).CanSeeResource(fixture.Environment1.Id.Value, fixture.App1.Id.Value));
        Assert.False(authorization.GetVisibility(fixture.Owner.Id, fixture.Team.Id).CanSeeResource(fixture.Environment2.Id.Value, fixture.App2.Id.Value));
    }

    [Fact]
    public void TokenLayer_NeverExceedsTheOwnersGrants()
    {
        var fixture = AccessFixture.Create();
        // A (forged or stale) root token of the project-scoped developer still cannot reach project 2.
        var token = new FixedTokenContext(new ApiTokenAccess(PersonalAccessTokenId.New(), fixture.Developer.Id,
            fixture.Team.Id, new HashSet<string> { TokenScopeMapper.Root }, null));
        VesselAuthorizationService authorization = fixture.Authorization(token);

        Assert.True(authorization.IsAuthorized(fixture.Developer.Id, fixture.Team.Id, App(fixture.App1), VesselPermissions.ApplicationsWrite));
        Assert.False(authorization.IsAuthorized(fixture.Developer.Id, fixture.Team.Id, App(fixture.App2), VesselPermissions.ApplicationsRead));
        Assert.False(authorization.IsAuthorized(fixture.Developer.Id, fixture.Team.Id, App(fixture.App1), VesselPermissions.ApplicationsDelete));
    }

    [Fact]
    public void TokenLayer_IsBoundToTheTokensTeam()
    {
        var fixture = AccessFixture.Create();
        var token = new FixedTokenContext(new ApiTokenAccess(PersonalAccessTokenId.New(), fixture.Owner.Id,
            fixture.Team.Id, new HashSet<string> { TokenScopeMapper.Root }, null));
        VesselAuthorizationService authorization = fixture.Authorization(token);

        // The owner also owns the other team, but a token for team 1 never works there.
        Assert.False(authorization.IsAuthorized(fixture.Owner.Id, fixture.OtherTeam.Id,
            ResourceRef.Project(fixture.OtherProject.Id), VesselPermissions.ProjectsRead));
        Assert.True(fixture.Authorization().IsAuthorized(fixture.Owner.Id, fixture.OtherTeam.Id,
            ResourceRef.Project(fixture.OtherProject.Id), VesselPermissions.ProjectsRead));
    }

    [Fact]
    public async Task ScopedTokens_CannotExceedTheIssuer()
    {
        var fixture = AccessFixture.Create();
        VesselTokenService tokens = fixture.Services.Get<VesselTokenService>();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tokens.CreateAsync(fixture.Developer.Id,
            fixture.Team.Id, "wide", [TokenScopeMapper.Write], null, null, null, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tokens.CreateAsync(fixture.Developer.Id,
            fixture.Team.Id, "other-project", [TokenScopeMapper.Write], null, null,
            [AccessGrant.Project(fixture.Project2.Id, AccessLevel.Read)], null, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tokens.CreateAsync(fixture.Developer.Id,
            fixture.Team.Id, "delete", [TokenScopeMapper.Delete], null, null,
            [AccessGrant.Project(fixture.Project1.Id, AccessLevel.Read)], null, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tokens.CreateAsync(fixture.Owner.Id,
            fixture.Team.Id, "outside", [TokenScopeMapper.Read], null, null,
            [AccessGrant.Project(fixture.OtherProject.Id, AccessLevel.Read)], null, Ct));

        CreatedToken created = await tokens.CreateAsync(fixture.Developer.Id, fixture.Team.Id, "ci",
            [TokenScopeMapper.Write, TokenScopeMapper.Deploy], null, null,
            [AccessGrant.Project(fixture.Project1.Id, AccessLevel.Read)], null, Ct);
        AuthTokenValidationResult validated = await tokens.ValidateAsync(created.PlainTextToken, Ct);

        Assert.True(validated.Succeeded);
        Assert.Equal(AccessScope.Project, Assert.Single(validated.Access!).Scope);
        // Team-level permissions are clamped away from the scoped developer's token.
        Assert.DoesNotContain(VesselPermissions.ServersWrite, validated.Permissions);
        Assert.Contains(VesselPermissions.DeploymentsStart, validated.Permissions);
    }

    [Fact]
    public async Task MemberAccess_TargetsMustBelongToTheTeam_AndRoleChangesResetGrants()
    {
        var fixture = AccessFixture.Create();
        VesselTeamService teams = fixture.Services.Get<VesselTeamService>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => teams.SetMemberAccessAsync(fixture.Owner.Id,
            fixture.Team.Id, fixture.Member.Id, [AccessGrant.Project(fixture.OtherProject.Id, AccessLevel.Write)],
            null, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => teams.SetMemberAccessAsync(fixture.Member.Id,
            fixture.Team.Id, fixture.Developer.Id, null, null, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => teams.SetMemberAccessAsync(fixture.Owner.Id,
            fixture.Team.Id, fixture.Admin.Id, AccessPresets.Grants(AccessPresets.Viewer), null, Ct));

        await teams.SetMemberAccessAsync(fixture.Owner.Id, fixture.Team.Id, fixture.Member.Id,
            AccessPresets.Grants(AccessPresets.Developer, fixture.Project2.Id), null, Ct);
        Assert.True(fixture.Authorization().IsAuthorized(fixture.Member.Id, fixture.Team.Id, App(fixture.App2),
            VesselPermissions.DeploymentsStart));
        Assert.False(fixture.Authorization().IsAuthorized(fixture.Member.Id, fixture.Team.Id, App(fixture.App1),
            VesselPermissions.ApplicationsRead));

        // Promoting and demoting again resets the member to the default team-wide read.
        await teams.ChangeRoleAsync(fixture.Owner.Id, fixture.Team.Id, fixture.Member.Id, TeamRole.Admin, null, Ct);
        await teams.ChangeRoleAsync(fixture.Owner.Id, fixture.Team.Id, fixture.Member.Id, TeamRole.Member, null, Ct);
        Assert.True(fixture.Authorization().IsAuthorized(fixture.Member.Id, fixture.Team.Id, App(fixture.App1),
            VesselPermissions.ApplicationsRead));
        Assert.False(fixture.Authorization().IsAuthorized(fixture.Member.Id, fixture.Team.Id, App(fixture.App2),
            VesselPermissions.DeploymentsStart));
    }

    [Fact]
    public async Task Invitation_CarriesGrantsToTheNewMember()
    {
        var fixture = AccessFixture.Create();
        VesselTeamService teams = fixture.Services.Get<VesselTeamService>();
        var invitee = User.Create(new DisplayName("Invitee"), new EmailAddress("invitee@example.com"), fixture.Now);
        fixture.Store.Set<User>().Add(invitee);

        TeamInvitationResult invitation = await teams.InviteAsync(fixture.Owner.Id, fixture.Team.Id,
            invitee.Email.Value, TeamRole.Member, AccessPresets.Grants(AccessPresets.Developer, fixture.Project1.Id),
            fixture.Now.AddDays(1), null, Ct);
        Assert.True(await teams.AcceptInvitationAsync(invitee.Id, invitation.PlainTextToken, null, Ct));

        VesselAuthorizationService authorization = fixture.Authorization();
        Assert.True(authorization.IsAuthorized(invitee.Id, fixture.Team.Id, App(fixture.App1), VesselPermissions.DeploymentsStart));
        Assert.False(authorization.IsAuthorized(invitee.Id, fixture.Team.Id, App(fixture.App2), VesselPermissions.ApplicationsRead));
        await Assert.ThrowsAsync<InvalidOperationException>(() => teams.InviteAsync(fixture.Owner.Id,
            fixture.Team.Id, "admin@example.com", TeamRole.Admin, AccessPresets.Grants(AccessPresets.Viewer),
            fixture.Now.AddDays(1), null, Ct));
    }

    private static ResourceRef App(AppEntity application)
    {
        return ResourceRef.Application(application.Id);
    }

    internal sealed class FixedTokenContext(ApiTokenAccess? token) : IApiTokenAccessContext
    {
        public ApiTokenAccess? Current { get; } = token;
    }

    internal sealed class NullAuditWriter : IAuditWriter
    {
        public Task RecordAsync(TeamId? teamId, UserId? actorUserId, string action, AuditTarget target,
            string? correlationId, IReadOnlyDictionary<string, object?> metadata,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}

/// <summary>
///     One team with an owner, an admin, a default member, a project-scoped developer (write + deploy on project 1)
///     and a resource-scoped deployer (deploy on application 2); plus another team owned by the same owner.
/// </summary>
internal sealed class AccessFixture
{
    private AccessFixture()
    {
        Store = InMemoryVesselDb.Create();
        Services = new SecurityServiceFactory(Store);
    }

    public DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;
    public InMemoryVesselDb Store { get; }
    public SecurityServiceFactory Services { get; }
    public User Owner { get; private set; } = null!;
    public User Admin { get; private set; } = null!;
    public User Member { get; private set; } = null!;
    public User Developer { get; private set; } = null!;
    public User ResourceDeployer { get; private set; } = null!;
    public Team Team { get; private set; } = null!;
    public Team OtherTeam { get; private set; } = null!;
    public Project Project1 { get; private set; } = null!;
    public Project Project2 { get; private set; } = null!;
    public Project OtherProject { get; private set; } = null!;
    public EnvironmentEntity Environment1 { get; private set; } = null!;
    public EnvironmentEntity Environment2 { get; private set; } = null!;
    public Server Server1 { get; private set; } = null!;
    public Server Server2 { get; private set; } = null!;
    public AppEntity App1 { get; private set; } = null!;
    public AppEntity App2 { get; private set; } = null!;

    public static AccessFixture Create()
    {
        var fixture = new AccessFixture();
        DateTimeOffset now = fixture.Now;
        fixture.Owner = NewUser("owner");
        fixture.Admin = NewUser("admin");
        fixture.Member = NewUser("member");
        fixture.Developer = NewUser("developer");
        fixture.ResourceDeployer = NewUser("deployer");

        fixture.Team = Team.Create(new DisplayName("Team"), fixture.Owner.Id, false, now);
        fixture.Team.AddMember(fixture.Admin.Id, TeamRole.Admin, now);
        fixture.Team.AddMember(fixture.Member.Id, TeamRole.Member, now);
        fixture.OtherTeam = Team.Create(new DisplayName("Other"), fixture.Owner.Id, false, now);

        (fixture.Project1, fixture.Environment1, fixture.Server1, fixture.App1) = fixture.AddResources(fixture.Team.Id, "1");
        (fixture.Project2, fixture.Environment2, fixture.Server2, fixture.App2) = fixture.AddResources(fixture.Team.Id, "2");
        (fixture.OtherProject, _, _, _) = fixture.AddResources(fixture.OtherTeam.Id, "other");

        fixture.Team.AddMember(fixture.Developer.Id, TeamRole.Member, now,
            AccessPresets.Grants(AccessPresets.Developer, fixture.Project1.Id));
        fixture.Team.AddMember(fixture.ResourceDeployer.Id, TeamRole.Member, now,
            [AccessGrant.Resource(fixture.App2.Id.Value, AccessLevel.Deploy)]);

        fixture.Store.Set<User>().AddRange([fixture.Owner, fixture.Admin, fixture.Member, fixture.Developer,
            fixture.ResourceDeployer]);
        fixture.Store.Set<Team>().AddRange([fixture.Team, fixture.OtherTeam]);
        return fixture;

        User NewUser(string name)
        {
            return User.Create(new DisplayName(name), new EmailAddress($"{name}@example.com"), now);
        }
    }

    public VesselAuthorizationService Authorization(IApiTokenAccessContext? token = null)
    {
        IApiTokenAccessContext context = token ?? NoApiTokenAccessContext.Instance;
        return new VesselAuthorizationService(Store.Context, new AccessResolver(Store.Context, context), context);
    }

    private (Project, EnvironmentEntity, Server, AppEntity) AddResources(TeamId teamId, string suffix)
    {
        var project = Project.Create(teamId, new ResourceName($"project-{suffix}"), Now);
        var environment = EnvironmentEntity.CreateProduction(project.Id, Now);
        var server = Server.Create(teamId, new ResourceName($"server-{suffix}"),
            new ServerAddress("10.0.0.1", new PortNumber(22)), ServerConnectionType.Ssh,
            ContainerRuntimeKind.Docker, Now);
        var application = AppEntity.Create(environment.Id, server.Id, new ResourceName($"app-{suffix}"),
            new GitSource(new RepositoryUrl("https://example.com/repo.git"), "main"),
            BuildConfiguration.Default(ApplicationBuildPack.Dockerfile), Now);

        Store.Set<Project>().Add(project);
        Store.Set<EnvironmentEntity>().Add(environment);
        Store.Set<Server>().Add(server);
        Store.Set<AppEntity>().Add(application);
        return (project, environment, server, application);
    }
}
