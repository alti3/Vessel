using Vessel.Application.Auditing;
using Vessel.Application.Deployments;
using Vessel.Application.Deployments.Queue;
using Vessel.Application.Deployments.Rollback;
using Vessel.Application.Deployments.Settings;
using Vessel.Application.Docker;
using Vessel.Application.Jobs;
using Vessel.Application.Processes;
using Vessel.Application.Realtime;
using Vessel.Domain;
using Vessel.Domain.Applications;
using Vessel.Domain.Auditing;
using Vessel.Domain.Common;
using Vessel.Domain.Deployments;
using Vessel.Domain.Users;
using Vessel.UnitTests.Security;

namespace Vessel.UnitTests.Deployments;

/// <summary>DEP-06 rollback target restrictions and DEP-07/SEC-06 deployment settings authorization.</summary>
public sealed class RollbackAndSettingsTests
{
    private const string Good = "0123456789abcdef0123456789abcdef01234567";
    private const string Other = "89abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void Rollback_accepts_listed_images_deployments_and_successful_commits_only()
    {
        var deploymentId = Guid.NewGuid();
        RollbackTarget[] targets =
        [
            new("vessel/app:" + Good, Good, Good, deploymentId, DateTimeOffset.UtcNow, true, false),
            new("vessel/app:" + Other, Other, Other, null, null, true, false)
        ];

        Assert.Equal(deploymentId, RollbackService.Resolve(targets, new RollbackRequest(deploymentId)).DeploymentId);
        Assert.Equal(Other, RollbackService.Resolve(targets, new RollbackRequest(Image: Other)).Tag);
        Assert.Equal(Good, RollbackService.Resolve(targets, new RollbackRequest(CommitSha: Good)).CommitSha);
        // A commit that only a local image (not a successful deployment) names is not accepted by commit.
        Assert.Throws<DomainException>(() => RollbackService.Resolve(targets, new RollbackRequest(CommitSha: Other)));
        Assert.Throws<DomainException>(() =>
            RollbackService.Resolve(targets, new RollbackRequest(CommitSha: "1111111111111111111111111111111111111111")));
        Assert.Throws<DomainException>(() => RollbackService.Resolve(targets, new RollbackRequest(Guid.NewGuid())));
    }

    [Fact]
    public async Task Rollback_lists_successful_production_deployments_and_queues_a_never_cancelled_rollback()
    {
        CrossTeamFixture fixture = CrossTeamFixture.Create();
        fixture.Actor.GrantInstanceAdmin(fixture.Now);
        DateTimeOffset now = fixture.Now;
        Deployment succeeded = Deployment.Queue(fixture.ApplicationA.Id, fixture.ServerA.Id, null, Good, now);
        succeeded.Start(now);
        succeeded.MarkSucceeded(DeploymentPlanBuilder.ProductionImage(fixture.ApplicationA.Id, Good, succeeded.Id), now);
        Deployment failed = Deployment.Queue(fixture.ApplicationA.Id, fixture.ServerA.Id, null, Other, now);
        failed.Start(now);
        failed.MarkFailed(now);
        fixture.Store.Set<Deployment>().AddRange([succeeded, failed]);
        RollbackService service = Rollbacks(fixture);

        IReadOnlyList<RollbackTarget> targets = await service.ListTargetsAsync(fixture.Actor.Id, fixture.TeamA.Id,
            fixture.ApplicationA.Id, TestContext.Current.CancellationToken);
        RollbackTarget target = Assert.Single(targets);
        Assert.Equal(Good, target.CommitSha);

        await Assert.ThrowsAsync<DomainException>(() => service.RollbackAsync(fixture.Actor.Id, fixture.TeamA.Id,
            fixture.ApplicationA.Id, new RollbackRequest(CommitSha: Other), TestContext.Current.CancellationToken));
        StartDeploymentResult result = await service.RollbackAsync(fixture.Actor.Id, fixture.TeamA.Id,
            fixture.ApplicationA.Id, new RollbackRequest(succeeded.Id.Value), TestContext.Current.CancellationToken);

        Deployment rollback = fixture.Store.Set<Deployment>().Single(item => item.Id.Value == result.DeploymentId);
        Assert.True(rollback.IsRollback);
        Assert.Equal(DeploymentTrigger.Rollback, rollback.Trigger);
        Assert.Equal(succeeded.Id, rollback.RollbackDeploymentId);
        Assert.Equal(target.Image, rollback.RollbackImage);
        // Rollbacks are never superseded, even by a webhook deployment with building auto-cancel on.
        Assert.Empty(SupersededDeploymentPolicy.Select(
            Deployment.Queue(fixture.ApplicationA.Id, fixture.ServerA.Id, null, Other, null,
                WebhookEventId.New(), now),
            DeploymentSettings.Default with { AutoCancelBuildingDeployments = true },
            CancelPreviousDeployments.Building, [rollback]));
    }

    [Fact]
    public async Task Rollback_requires_deploy_permission_in_the_owning_team()
    {
        CrossTeamFixture fixture = CrossTeamFixture.Create();
        RollbackService service = Rollbacks(fixture);

        // Actor is Admin in team A, so acting with team A on team B's application is denied.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RollbackAsync(fixture.Actor.Id,
            fixture.TeamA.Id, fixture.ApplicationB.Id, new RollbackRequest(CommitSha: Good),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Public_previews_can_be_enabled_only_with_write_permission_and_are_audited()
    {
        CrossTeamFixture fixture = CrossTeamFixture.Create();
        var audit = new RecordingAudit();
        var service = new DeploymentSettingsService(fixture.Store.Context, fixture.Authorization, audit,
            TimeProvider.System);
        var enable = new UpdateApplicationDeploymentSettingsRequest(PreviewDeploymentsEnabled: true,
            PublicPreviewsEnabled: true);

        // Member of team B (read-only role) cannot change team B's application.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.UpdateAsync(fixture.Actor.Id,
            fixture.TeamB.Id, fixture.ApplicationB.Id, enable, TestContext.Current.CancellationToken));
        // Admin of team A cannot reach team B's application through team A.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.UpdateAsync(fixture.Actor.Id,
            fixture.TeamA.Id, fixture.ApplicationB.Id, enable, TestContext.Current.CancellationToken));
        Assert.False(fixture.ApplicationB.DeploymentSettings.PublicPreviewsEnabled);

        ApplicationDeploymentSettingsDto updated = await service.UpdateAsync(fixture.Actor.Id, fixture.TeamA.Id,
            fixture.ApplicationA.Id, enable with { ImagesToKeep = 5, AutoCancelBuildingDeployments = true },
            TestContext.Current.CancellationToken);

        Assert.True(updated.PublicPreviewsEnabled);
        Assert.True(updated.PreviewDeploymentsEnabled);
        Assert.True(updated.AutoCancelQueuedDeployments);
        Assert.True(updated.AutoCancelBuildingDeployments);
        Assert.True(updated.RedactAllEnvValuesInLogs);
        Assert.Equal(5, updated.ImagesToKeep);
        Assert.Equal(AuditActions.ApplicationDeploymentSettingsUpdated, Assert.Single(audit.Actions));
        await Assert.ThrowsAsync<DomainException>(() => service.UpdateAsync(fixture.Actor.Id, fixture.TeamA.Id,
            fixture.ApplicationA.Id, new UpdateApplicationDeploymentSettingsRequest(ImagesToKeep: 500),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Server_deployment_limits_need_server_write_and_are_bounded()
    {
        CrossTeamFixture fixture = CrossTeamFixture.Create();
        var service = new DeploymentSettingsService(fixture.Store.Context, fixture.Authorization, new RecordingAudit(),
            TimeProvider.System);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.UpdateServerLimitsAsync(
            fixture.Actor.Id, fixture.TeamB.Id, fixture.ServerB.Id, new ServerDeploymentLimitsDto(4, 10),
            TestContext.Current.CancellationToken));
        Assert.Equal(new ServerDeploymentLimitsDto(2, 25),
            service.GetServerLimits(fixture.Actor.Id, fixture.TeamA.Id, fixture.ServerA.Id));

        await service.UpdateServerLimitsAsync(fixture.Actor.Id, fixture.TeamA.Id, fixture.ServerA.Id,
            new ServerDeploymentLimitsDto(4, 10), TestContext.Current.CancellationToken);

        Assert.Equal(4, fixture.ServerA.ConcurrentBuilds);
        await Assert.ThrowsAsync<DomainException>(() => service.UpdateServerLimitsAsync(fixture.Actor.Id,
            fixture.TeamA.Id, fixture.ServerA.Id, new ServerDeploymentLimitsDto(0, 10),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Production_images_are_tagged_by_commit()
    {
        var app = Vessel.Domain.ApplicationId.New();
        var deployment = DeploymentId.New();

        Assert.Equal($"vessel/{app.Value:N}:{Good}", DeploymentPlanBuilder.ProductionImage(app, Good, deployment));
        Assert.Equal($"vessel/{app.Value:N}:{deployment.Value:N}",
            DeploymentPlanBuilder.ProductionImage(app, "pending", deployment));
    }

    private static RollbackService Rollbacks(CrossTeamFixture fixture)
    {
        var queue = new DeploymentQueueService(fixture.Store.Context,
            new InMemoryDistributedLockManager(TimeProvider.System), new NullJobs(), new DeploymentCancellationRegistry(),
            new NullRealtime(), TimeProvider.System);
        return new RollbackService(fixture.Store.Context, fixture.Authorization, new EmptyRuntime(), queue,
            new RecordingAudit());
    }

    private sealed class RecordingAudit : IAuditWriter
    {
        public List<string> Actions { get; } = [];

        public Task RecordAsync(TeamId? teamId, UserId? actorUserId, string action, AuditTarget target,
            string? correlationId, IReadOnlyDictionary<string, object?> metadata,
            CancellationToken cancellationToken = default)
        {
            Actions.Add(action);
            return Task.CompletedTask;
        }
    }

    private sealed class NullJobs : IBackgroundJobDispatcher
    {
        public string Enqueue<TJob>(System.Linq.Expressions.Expression<Func<TJob, Task>> methodCall) => "job";

        public string Schedule<TJob>(System.Linq.Expressions.Expression<Func<TJob, Task>> methodCall,
            TimeSpan delay) => "job";
    }

    private sealed class NullRealtime : IRealtimeNotifier
    {
        public Task PublishAsync(RealtimeGroup group, RealtimeMessage message,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class EmptyRuntime : IContainerRuntimeClient
    {
        public Task<ContainerRuntimeInfo> GetInfoAsync(ContainerRuntimeTarget target,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<ContainerSummary>> ListContainersAsync(ContainerRuntimeTarget target, bool all,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string> InspectContainerAsync(ContainerRuntimeTarget target, string containerId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<ImageSummary>> ListImagesAsync(ContainerRuntimeTarget target,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ImageSummary>>([]);

        public Task<IReadOnlyList<NetworkSummary>> ListNetworksAsync(ContainerRuntimeTarget target,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<VolumeSummary>> ListVolumesAsync(ContainerRuntimeTarget target,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task EnsureNetworkAsync(ContainerRuntimeTarget target, string name,
            IReadOnlyDictionary<string, string> labels, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<ProcessOutputLine> BuildImageAsync(ContainerRuntimeTarget target,
            DockerBuildCommand command, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<ContainerEvent> StreamEventsAsync(ContainerRuntimeTarget target,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<ProcessOutputLine> RunComposeAsync(ContainerRuntimeTarget target,
            ComposeCommand command, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
