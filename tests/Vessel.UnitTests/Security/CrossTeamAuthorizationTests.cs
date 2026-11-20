using Vessel.Application.Authorization;
using Vessel.Application.Deployments;
using Vessel.Application.Monitoring;
using Vessel.Application.Proxy;
using Vessel.Application.Resources;
using Vessel.Application.Terminals;
using Vessel.Application.Webhooks;
using Vessel.Domain.Applications;
using Vessel.Domain.EnvironmentVariables;
using Vessel.Domain.Webhooks;

namespace Vessel.UnitTests.Security;

/// <summary>
///     SEC-02 regression tests. The actor is Admin in team A (and owns a personal team) but only a Member in team B.
///     No combination of active team and resource may let the Admin role in A (or the personal team) authorize a
///     write to team B's resources.
/// </summary>
public sealed class CrossTeamAuthorizationTests
{
    private static readonly Dictionary<string, Func<CrossTeamFixture, Task>> CrossTeamWrites = new()
    {
        ["update project via admin team"] = f => f.Services.Get<ResourceManagementService>().UpdateProjectAsync(
            f.Actor.Id, f.TeamA.Id, f.ProjectB.Id, new UpdateProjectRequest("hijacked", null), Ct),
        ["update project via personal team"] = f => f.Services.Get<ResourceManagementService>().UpdateProjectAsync(
            f.Actor.Id, f.PersonalTeam.Id, f.ProjectB.Id, new UpdateProjectRequest("hijacked", null), Ct),
        ["update project as member of owning team"] = f => f.Services.Get<ResourceManagementService>()
            .UpdateProjectAsync(f.Actor.Id, f.TeamB.Id, f.ProjectB.Id, new UpdateProjectRequest("hijacked", null),
                Ct),
        ["archive project via admin team"] = f => f.Services.Get<ResourceManagementService>().ArchiveProjectAsync(
            f.Actor.Id, f.TeamA.Id, f.ProjectB.Id, Ct),
        ["create application on another team's server"] = f => f.Services.Get<ResourceManagementService>()
            .CreateApplicationAsync(f.Actor.Id, f.TeamA.Id,
                new CreateApplicationRequest(f.ProjectA.Id.Value, f.EnvironmentA.Id.Value, f.ServerB.Id.Value,
                    "app", null, "https://example.com/repo.git", "main", ApplicationBuildPack.Dockerfile, "/",
                    null, null, []), Ct),
        ["create environment variable targeting another team's application"] = f =>
            f.Services.Get<ResourceManagementService>().CreateEnvironmentVariableAsync(f.Actor.Id, f.TeamA.Id,
                new CreateEnvironmentVariableRequest(EnvironmentVariableTargetType.Application,
                    f.ProjectB.Id.Value, f.EnvironmentB.Id.Value, null, f.ApplicationB.Id.Value, null, "KEY",
                    "value", EnvironmentVariableValueKind.Plain, false, true, false, false, false, null), Ct),
        ["test connectivity as member of owning team"] = f => f.Services.Get<ResourceManagementService>()
            .TestServerConnectivityAsync(f.Actor.Id, f.TeamB.Id, f.ServerB.Id, Ct),
        ["configure domain via admin team"] = f => f.Services.Get<DomainRoutingService>().ConfigureAsync(
            f.Actor.Id, f.TeamA.Id, f.ApplicationB.Id,
            new ConfigureDomainRouteRequest("app.example.com", 8080, true, false, false), Ct),
        ["configure domain as member of owning team"] = f => f.Services.Get<DomainRoutingService>().ConfigureAsync(
            f.Actor.Id, f.TeamB.Id, f.ApplicationB.Id,
            new ConfigureDomainRouteRequest("app.example.com", 8080, true, false, false), Ct),
        ["queue certificate via admin team"] = f => f.Services.Get<CertificateManagementService>()
            .QueueIssuanceAsync(f.Actor.Id, f.TeamA.Id, f.ApplicationB.Id, "app.example.com", Ct),
        ["rewrite webhook secret via admin team"] = f => f.Services.Get<ApplicationWebhookConfigurationService>()
            .ConfigureAsync(f.Actor.Id, f.TeamA.Id, f.ApplicationB.Id,
                new ConfigureApplicationWebhookRequest(WebhookProvider.GitHub, "attacker-secret-value-123456"), Ct),
        ["apply proxy configuration via admin team"] = f => f.Services.Get<ProxyConfigurationService>()
            .GenerateValidateAndApplyAsync(f.Actor.Id, f.TeamA.Id, f.ServerB.Id, Ct),
        ["apply proxy configuration as member of owning team"] = f => f.Services.Get<ProxyConfigurationService>()
            .GenerateValidateAndApplyAsync(f.Actor.Id, f.TeamB.Id, f.ServerB.Id, Ct),
        ["start deployment via admin team"] = f => f.Services.Get<StartDeploymentService>().StartAsync(
            f.Actor.Id, f.TeamA.Id, new StartDeploymentRequest(f.ApplicationB.Id.Value), Ct),
        ["cancel deployment via admin team"] = f => f.Services.Get<StartDeploymentService>().CancelAsync(
            f.Actor.Id, f.TeamA.Id, f.DeploymentB.Id, Ct),
        ["poll server via admin team"] = f => f.Services.Get<ServerHealthPollingService>().PollAsync(
            f.Actor.Id, f.TeamA.Id, f.ServerB.Id, Ct),
        ["open terminal via admin team"] = f => f.Services.Get<TerminalSessionManager>().OpenAsync(
            f.Actor.Id, f.TeamA.Id, new OpenTerminalSessionRequest(f.ServerB.Id.Value), null, Ct)
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> CrossTeamWriteCases => new(CrossTeamWrites.Keys);

    [Theory]
    [MemberData(nameof(CrossTeamWriteCases))]
    public async Task CrossTeamWrite_IsRejectedWithoutSideEffects(string scenario)
    {
        var fixture = CrossTeamFixture.Create();
        string originalProjectName = fixture.ProjectB.Name.Value;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => CrossTeamWrites[scenario](fixture));

        Assert.Equal(originalProjectName, fixture.ProjectB.Name.Value);
        Assert.False(fixture.ProjectB.IsArchived);
        Assert.Empty(fixture.ApplicationB.Domains);
        Assert.Single(fixture.Store.Set<Vessel.Domain.Deployments.Deployment>());
        Assert.Empty(fixture.Store.Set<Vessel.Domain.Terminals.TerminalSession>());
        Assert.Empty(fixture.Store.Set<EnvironmentVariable>());
        Assert.All(fixture.Services.Stubs, stub => Assert.Empty(((InertStub)stub).Calls));
    }

    [Fact]
    public void ResourceCheck_UsesRoleInOwningTeam_NeverAnotherMembership()
    {
        var fixture = CrossTeamFixture.Create();
        VesselAuthorizationService authorization = fixture.Authorization;
        ResourceRef applicationB = ResourceRef.Application(fixture.ApplicationB.Id);

        // Admin role in A or ownership of the personal team never applies to B's resources.
        Assert.False(authorization.IsAuthorized(fixture.Actor.Id, fixture.TeamA.Id, applicationB,
            VesselPermissions.ApplicationsWrite));
        Assert.False(authorization.IsAuthorized(fixture.Actor.Id, fixture.PersonalTeam.Id, applicationB,
            VesselPermissions.ApplicationsWrite));
        Assert.False(authorization.IsAuthorized(fixture.Actor.Id, fixture.TeamA.Id, applicationB,
            VesselPermissions.ApplicationsRead));
        // In B the actor's role is Member: read is allowed, write is not.
        Assert.False(authorization.IsAuthorized(fixture.Actor.Id, fixture.TeamB.Id, applicationB,
            VesselPermissions.ApplicationsWrite));
        Assert.True(authorization.IsAuthorized(fixture.Actor.Id, fixture.TeamB.Id, applicationB,
            VesselPermissions.ApplicationsRead));
        // Positive control: the admin role does apply to team A's own resources.
        Assert.True(authorization.IsAuthorized(fixture.Actor.Id, fixture.TeamA.Id,
            ResourceRef.Application(fixture.ApplicationA.Id), VesselPermissions.ApplicationsWrite));
        Assert.True(authorization.IsAuthorized(fixture.Actor.Id, fixture.TeamA.Id,
            ResourceRef.Server(fixture.ServerA.Id), VesselPermissions.ServersWrite));
    }

    [Fact]
    public void ResourceCheck_ResolvesOwningTeamForEveryResourceKind()
    {
        var fixture = CrossTeamFixture.Create();
        VesselAuthorizationService authorization = fixture.Authorization;

        Assert.Equal(fixture.TeamB.Id, authorization.ResolveOwningTeam(ResourceRef.Project(fixture.ProjectB.Id)));
        Assert.Equal(fixture.TeamB.Id,
            authorization.ResolveOwningTeam(ResourceRef.Environment(fixture.EnvironmentB.Id)));
        Assert.Equal(fixture.TeamB.Id, authorization.ResolveOwningTeam(ResourceRef.Server(fixture.ServerB.Id)));
        Assert.Equal(fixture.TeamB.Id,
            authorization.ResolveOwningTeam(ResourceRef.Application(fixture.ApplicationB.Id)));
        Assert.Equal(fixture.TeamB.Id,
            authorization.ResolveOwningTeam(ResourceRef.Deployment(fixture.DeploymentB.Id)));
        Assert.Null(authorization.ResolveOwningTeam(ResourceRef.Server(Vessel.Domain.ServerId.New())));
    }

    [Fact]
    public void Demand_ReportsMissingPermissionAndForeignResourceDistinctly()
    {
        var fixture = CrossTeamFixture.Create();
        VesselAuthorizationService authorization = fixture.Authorization;

        UnauthorizedAccessException missing = Assert.Throws<UnauthorizedAccessException>(() =>
            authorization.Demand(fixture.Actor.Id, fixture.TeamB.Id, ResourceRef.Server(fixture.ServerB.Id),
                VesselPermissions.ServersWrite));
        UnauthorizedAccessException foreign = Assert.Throws<UnauthorizedAccessException>(() =>
            authorization.Demand(fixture.Actor.Id, fixture.TeamA.Id, ResourceRef.Server(fixture.ServerB.Id),
                VesselPermissions.ServersWrite));
        UnauthorizedAccessException unknown = Assert.Throws<UnauthorizedAccessException>(() =>
            authorization.Demand(fixture.Actor.Id, fixture.TeamA.Id, ResourceRef.Server(Vessel.Domain.ServerId.New()),
                VesselPermissions.ServersWrite));

        Assert.Contains("servers.write", missing.Message, StringComparison.Ordinal);
        Assert.Equal("Server is outside the active team.", foreign.Message);
        Assert.Equal(foreign.Message, unknown.Message);
    }

    [Fact]
    public void AuthorizationService_DoesNotExposeTeamAgnosticResourceChecks()
    {
        // The old "HasPermission(activeTeam) + CanAccessX(resource in any team)" pair is the SEC-02 bug. Resource
        // checks must go through IsAuthorized/Demand, which bind the resource to the active team.
        string[] forbidden = ["CanAccessProject", "CanAccessServer", "CanAccessApplication", "CanAccessDeployment"];
        string[] publicMethods = typeof(VesselAuthorizationService).GetMethods().Select(method => method.Name)
            .ToArray();

        Assert.DoesNotContain(publicMethods, forbidden.Contains);
    }

    [Fact]
    public async Task OwnTeamWrite_StillSucceeds()
    {
        var fixture = CrossTeamFixture.Create();

        ProjectDetails updated = await fixture.Services.Get<ResourceManagementService>().UpdateProjectAsync(
            fixture.Actor.Id, fixture.TeamA.Id, fixture.ProjectA.Id, new UpdateProjectRequest("renamed", null), Ct);

        Assert.Equal("renamed", updated.Name);
    }
}
