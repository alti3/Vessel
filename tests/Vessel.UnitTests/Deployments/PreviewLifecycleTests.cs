using Vessel.Application.Deployments;
using Vessel.Application.Deployments.Previews;
using Vessel.Application.Deployments.Queue;
using Vessel.Application.Docker;
using Vessel.Application.Jobs;
using Vessel.Application.Processes;
using Vessel.Application.Proxy;
using Vessel.Application.Realtime;
using Vessel.Application.Webhooks;
using Vessel.Domain;
using Vessel.Domain.Common;
using Vessel.Domain.Proxy;
using Vessel.Domain.Webhooks;
using Vessel.UnitTests.Security;

namespace Vessel.UnitTests.Deployments;

/// <summary>DEP-07: preview close/stop removes only the preview's own resources, with deploy/write permission splits.</summary>
public sealed class PreviewLifecycleTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public async Task Closing_a_preview_removes_its_project_volumes_and_only_its_own_images()
    {
        (CrossTeamFixture fixture, RecordingRuntime runtime, PreviewLifecycleService service) = Create();
        fixture.Actor.GrantInstanceAdmin(fixture.Now);
        ApplicationPreview one = AddPreview(fixture, 1);
        AddPreview(fixture, 12);
        var repository = DeploymentPlanBuilder.ImageRepository(fixture.ApplicationA.Id);
        runtime.Images.AddRange([$"{repository}:pr-1-aaaa", $"{repository}:pr-12-bbbb", $"{repository}:{Commit}"]);

        ApplicationPreviewSummary closed = await service.CloseAsync(fixture.Actor.Id, fixture.TeamA.Id,
            fixture.ApplicationA.Id, 1, TestContext.Current.CancellationToken);

        Assert.Equal(nameof(ApplicationPreviewStatus.Archived), closed.Status);
        (string project, bool volumes) = Assert.Single(runtime.RemovedProjects);
        Assert.Equal($"vessel-{fixture.ApplicationA.Id.Value:N}-pr-1", project);
        Assert.True(volumes);
        Assert.Equal([$"{repository}:pr-1-aaaa"], runtime.RemovedImages);
        Assert.Null(one.PreviewUrl);
    }

    [Fact]
    public async Task Cleanup_skips_a_preview_whose_pull_request_was_reopened()
    {
        (CrossTeamFixture fixture, RecordingRuntime runtime, PreviewLifecycleService service) = Create();
        ApplicationPreview preview = AddPreview(fixture, 4);
        preview.Archive(fixture.Now);
        preview.Refresh("feature", "main", Commit, null, null, fixture.Now, "org/app", true);

        await new CleanupPreviewJob(service).RunAsync(preview.Id.Value, TestContext.Current.CancellationToken);

        Assert.Empty(runtime.RemovedProjects);
        Assert.Equal(ApplicationPreviewStatus.Open, preview.Status);
    }

    [Fact]
    public async Task Stop_keeps_volumes_and_redeploy_reopens_the_preview()
    {
        (CrossTeamFixture fixture, RecordingRuntime runtime, PreviewLifecycleService service) = Create();
        fixture.Actor.GrantInstanceAdmin(fixture.Now);
        ApplicationPreview preview = AddPreview(fixture, 2);

        await service.StopAsync(fixture.Actor.Id, fixture.TeamA.Id, fixture.ApplicationA.Id, 2,
            TestContext.Current.CancellationToken);

        Assert.Equal(ApplicationPreviewStatus.Stopped, preview.Status);
        Assert.False(Assert.Single(runtime.RemovedProjects).RemoveVolumes);

        StartDeploymentResult redeploy = await service.RedeployAsync(fixture.Actor.Id, fixture.TeamA.Id,
            fixture.ApplicationA.Id, 2, false, TestContext.Current.CancellationToken);

        Assert.Equal(ApplicationPreviewStatus.Open, preview.Status);
        Assert.Equal(Vessel.Domain.Deployments.DeploymentTrigger.Preview,
            fixture.Store.Set<Vessel.Domain.Deployments.Deployment>()
                .Single(item => item.Id.Value == redeploy.DeploymentId).Trigger);
    }

    [Fact]
    public async Task Stop_and_close_enforce_permissions_and_the_host_local_boundary()
    {
        (CrossTeamFixture fixture, RecordingRuntime runtime, PreviewLifecycleService service) = Create();
        AddPreview(fixture, 3);

        // Admin of team A, but not an instance admin: the host-local server boundary applies.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.StopAsync(fixture.Actor.Id,
            fixture.TeamA.Id, fixture.ApplicationA.Id, 3, TestContext.Current.CancellationToken));
        fixture.Actor.GrantInstanceAdmin(fixture.Now);
        // Read-only member of team B cannot close team B's previews (write permission needed).
        ApplicationPreview other = ApplicationPreview.Open(fixture.ApplicationB.Id, WebhookProvider.GitHub, 3,
            "feature", "main", Commit, null, null, fixture.Now, "org/app", true);
        fixture.Store.Set<ApplicationPreview>().Add(other);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CloseAsync(fixture.Actor.Id,
            fixture.TeamB.Id, fixture.ApplicationB.Id, 3, TestContext.Current.CancellationToken));

        Assert.Empty(runtime.RemovedProjects);
        Assert.Equal(ApplicationPreviewStatus.Open, other.Status);
    }

    private static ApplicationPreview AddPreview(CrossTeamFixture fixture, int number)
    {
        var preview = ApplicationPreview.Open(fixture.ApplicationA.Id, WebhookProvider.GitHub, number, "feature",
            "main", Commit, null, null, fixture.Now, "org/app", true);
        preview.SetPreviewUrl($"https://{number}.example.com", fixture.Now);
        fixture.Store.Set<ApplicationPreview>().Add(preview);
        return preview;
    }

    private static (CrossTeamFixture, RecordingRuntime, PreviewLifecycleService) Create()
    {
        CrossTeamFixture fixture = CrossTeamFixture.Create();
        foreach (var server in new[] { fixture.ServerA, fixture.ServerB })
        {
            var settings = ServerProxySettings.CreateDefault(server.Id, "traefik:v3", fixture.Now);
            settings.Configure(ServerProxyType.None, 80, 443, fixture.Now);
            fixture.Store.Set<ServerProxySettings>().Add(settings);
        }

        var runtime = new RecordingRuntime();
        var queue = new DeploymentQueueService(fixture.Store.Context,
            new InMemoryDistributedLockManager(TimeProvider.System), new NullJobs(),
            new DeploymentCancellationRegistry(), new NullRealtime(), TimeProvider.System);
        var deployments = new StartDeploymentService(fixture.Store.Context, fixture.Authorization, queue,
            new Vessel.Application.Auditing.NoopAuditWriter(), TimeProvider.System);
        var service = new PreviewLifecycleService(fixture.Store.Context, fixture.Authorization, runtime,
            fixture.Services.Get<ProxyConfigurationService>(), deployments,
            new Vessel.Application.Auditing.NoopAuditWriter(), TimeProvider.System);
        return (fixture, runtime, service);
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

    private sealed class RecordingRuntime : IContainerRuntimeClient
    {
        public List<string> Images { get; } = [];

        public List<(string Project, bool RemoveVolumes)> RemovedProjects { get; } = [];

        public List<string> RemovedImages { get; } = [];

        public Task<bool> RemoveComposeProjectAsync(ContainerRuntimeTarget target, string projectName,
            bool removeVolumes, CancellationToken cancellationToken = default)
        {
            RemovedProjects.Add((projectName, removeVolumes));
            return Task.FromResult(true);
        }

        public Task<bool> RemoveImageAsync(ContainerRuntimeTarget target, string imageReference,
            CancellationToken cancellationToken = default)
        {
            RemovedImages.Add(imageReference);
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<ImageSummary>> ListImagesAsync(ContainerRuntimeTarget target,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ImageSummary>>(Images.Select(image => new ImageSummary(image, [image], 0))
                .ToArray());

        public Task<ContainerRuntimeInfo> GetInfoAsync(ContainerRuntimeTarget target,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<ContainerSummary>> ListContainersAsync(ContainerRuntimeTarget target, bool all,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string> InspectContainerAsync(ContainerRuntimeTarget target, string containerId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

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
