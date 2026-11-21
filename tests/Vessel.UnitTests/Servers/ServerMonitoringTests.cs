using Vessel.Application.Authorization;
using Vessel.Application.Docker;
using Vessel.Application.Monitoring.HostMetrics;
using Vessel.Application.Notifications;
using Vessel.Application.Resources.RuntimeLogs;
using Vessel.Application.Resources.Status;
using Vessel.Application.Servers.Alerts;
using Vessel.Application.Servers.DockerCleanup;
using Vessel.Application.Deployments;
using Vessel.Domain;
using Vessel.Domain.Common;
using Vessel.Domain.Deployments;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Servers;
using Vessel.Domain.Services;
using Vessel.Domain.Teams;
using Vessel.Domain.ValueObjects;
using Vessel.UnitTests.Application;
using Vessel.UnitTests.Security;

namespace Vessel.UnitTests.Servers;

/// <summary>SRV-03/04/05/07/08: metrics, runtime status, runtime logs, Docker cleanup and server alerts.</summary>
public sealed class ServerMonitoringTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    // ---- Domain: pressure, rollups, aggregation, restart limits, disk alerts ----

    [Fact]
    public void PressureLevelsFollowUpstreamThresholds()
    {
        Assert.Equal(PressureLevel.Normal, PressureThresholds.ForCpu(69.9m));
        Assert.Equal(PressureLevel.Warning, PressureThresholds.ForCpu(70m));
        Assert.Equal(PressureLevel.Critical, PressureThresholds.ForMemory(90m));
        Assert.Equal(PressureLevel.Normal, PressureThresholds.ForDisk(79m));
        Assert.Equal(PressureLevel.Warning, PressureThresholds.ForDisk(80m));
        Assert.Equal(PressureLevel.Unknown, PressureThresholds.ForDisk(null));
    }

    [Fact]
    public void HourlyRollupAveragesRawSamplesOfOneServer()
    {
        var serverId = ServerId.New();
        ServerMetricSample[] raw =
        [
            Raw(serverId, Now.AddMinutes(1), 10m, 100, 1000),
            Raw(serverId, Now.AddMinutes(2), 30m, 300, 1000)
        ];
        ServerMetricSample rollup = ServerMetricSample.CreateHourlyRollup(serverId, Now.AddMinutes(30), raw, Now);
        Assert.Equal(MetricResolution.Hourly, rollup.Resolution);
        Assert.Equal(Now, rollup.BucketStart);
        Assert.Equal(20m, rollup.CpuPercent);
        Assert.Equal(30m, rollup.CpuPercentMax);
        Assert.Equal(200, rollup.MemoryUsedBytes);
        Assert.Equal(2, rollup.SampleCount);
        Assert.Throws<DomainException>(() =>
            ServerMetricSample.CreateHourlyRollup(ServerId.New(), Now, raw, Now));
    }

    [Theory]
    [InlineData(new[] { "running" }, new[] { "healthy" }, ResourceRuntimeState.Running, ResourceHealth.Healthy)]
    [InlineData(new[] { "running" }, new[] { "unhealthy" }, ResourceRuntimeState.Running, ResourceHealth.Unhealthy)]
    [InlineData(new[] { "running", "exited" }, new[] { "", "" }, ResourceRuntimeState.Degraded, ResourceHealth.Unhealthy)]
    [InlineData(new[] { "restarting" }, new[] { "" }, ResourceRuntimeState.Restarting, ResourceHealth.Unknown)]
    [InlineData(new[] { "exited", "exited" }, new[] { "", "" }, ResourceRuntimeState.Exited, ResourceHealth.Unknown)]
    [InlineData(new[] { "dead" }, new[] { "" }, ResourceRuntimeState.Degraded, ResourceHealth.Unhealthy)]
    public void AggregatorFollowsUpstreamPriorities(string[] states, string[] health, ResourceRuntimeState state,
        ResourceHealth expectedHealth)
    {
        var observations = states.Select((item, index) => new ContainerObservation(item, health[index], 0)).ToArray();
        AggregatedRuntimeStatus status = ContainerStatusAggregator.Aggregate(observations);
        Assert.Equal(state, status.State);
        Assert.Equal(expectedHealth, status.Health);
    }

    [Fact]
    public void ServiceStacksReportARestartingMemberAsDegraded()
    {
        var status = ContainerStatusAggregator.Aggregate(
            [new ContainerObservation("running", "healthy", 0), new ContainerObservation("restarting", null, 3)],
            preserveRestarting: false);
        Assert.Equal(ResourceRuntimeState.Degraded, status.State);
        Assert.Equal(ResourceRuntimeState.Exited, ContainerStatusAggregator.Aggregate([]).State);
    }

    [Fact]
    public void RestartLimitIsOptInAndClaimedOnce()
    {
        var status = ResourceRuntimeStatus.Create(ResourceRuntimeKind.Application, Guid.NewGuid(), TeamId.New(),
            ServerId.New(), Now);
        var restarting = new AggregatedRuntimeStatus(ResourceRuntimeState.Restarting, ResourceHealth.Unknown);

        // Disabled by default: no restart count ever reaches a limit.
        Assert.False(status.Observe(restarting, 1, 0, 500, Now).RestartLimitReachedNow);
        Assert.False(status.RestartLimitReached);

        status.ConfigureRestartLimit(5, Now);
        Assert.False(status.Observe(restarting, 1, 0, 4, Now).RestartLimitReachedNow);
        Assert.True(status.Observe(restarting, 1, 0, 5, Now).RestartLimitReachedNow);
        Assert.False(status.Observe(restarting, 1, 0, 6, Now).RestartLimitReachedNow);
        Assert.True(status.RestartLimitReached);

        // A new container generation running below the limit clears it.
        status.Observe(new AggregatedRuntimeStatus(ResourceRuntimeState.Running, ResourceHealth.Healthy), 1, 1, 0,
            Now);
        Assert.False(status.RestartLimitReached);
        Assert.Throws<DomainException>(() => status.ConfigureRestartLimit(-1, Now));
    }

    [Fact]
    public void StopIsUnexpectedOnlyFromAnUpStateWithoutAnExpectedStop()
    {
        var status = ResourceRuntimeStatus.Create(ResourceRuntimeKind.Database, Guid.NewGuid(), TeamId.New(),
            ServerId.New(), Now);
        var running = new AggregatedRuntimeStatus(ResourceRuntimeState.Running, ResourceHealth.Healthy);
        var exited = new AggregatedRuntimeStatus(ResourceRuntimeState.Exited, ResourceHealth.Unknown);

        // The first observation (from Unknown) never notifies.
        Assert.False(status.Observe(exited, 1, 0, 0, Now).StoppedUnexpectedly);
        status.Observe(running, 1, 1, 0, Now);
        Assert.True(status.Observe(exited, 1, 0, 0, Now.AddMinutes(1)).StoppedUnexpectedly);
        // Still exited: not a new transition.
        Assert.False(status.Observe(exited, 1, 0, 0, Now.AddMinutes(2)).StoppedUnexpectedly);

        status.Observe(running, 1, 1, 0, Now.AddMinutes(3));
        status.ExpectStop(Now.AddMinutes(3));
        Assert.False(status.Observe(exited, 1, 0, 0, Now.AddMinutes(4)).StoppedUnexpectedly);
    }

    [Fact]
    public void DiskAlertFiresOncePerIntervalAndReArmsAfterRecovery()
    {
        var settings = ServerMonitoringSettings.CreateDefault(ServerId.New(), Now);
        settings.ConfigureDiskAlerts(80, 24, Now);

        Assert.Equal(DiskAlertDecision.None, settings.EvaluateDiskUsage(79m, Now));
        Assert.Equal(DiskAlertDecision.Alert, settings.EvaluateDiskUsage(85m, Now));
        Assert.Equal(DiskAlertDecision.Suppressed, settings.EvaluateDiskUsage(90m, Now.AddHours(1)));
        // Dropping below the threshold but not by the margin does not re-arm.
        Assert.Equal(DiskAlertDecision.None, settings.EvaluateDiskUsage(77m, Now.AddHours(2)));
        Assert.Equal(DiskAlertDecision.Suppressed, settings.EvaluateDiskUsage(81m, Now.AddHours(3)));
        Assert.Equal(DiskAlertDecision.Rearmed, settings.EvaluateDiskUsage(75m, Now.AddHours(4)));
        Assert.Equal(DiskAlertDecision.Alert, settings.EvaluateDiskUsage(81m, Now.AddHours(5)));
        // After the interval it alerts again even without a recovery.
        Assert.Equal(DiskAlertDecision.Alert, settings.EvaluateDiskUsage(81m, Now.AddHours(29)));
        Assert.Throws<DomainException>(() => settings.ConfigureDiskAlerts(100, 24, Now));
        Assert.Throws<DomainException>(() => settings.ConfigureDiskAlerts(80, 0, Now));
    }

    [Fact]
    public void DockerCleanupPolicyNeverDeletesVolumesByDefaultAndValidatesCron()
    {
        Assert.False(DockerCleanupPolicy.Default.DeleteUnusedVolumes);
        Assert.False(DockerCleanupPolicy.Default.DeleteUnusedNetworks);
        Assert.Throws<DomainException>(() => DockerCleanupPolicy.Create(true, "* * *", 80, true, false, false));
        Assert.Throws<DomainException>(() => DockerCleanupPolicy.Create(true, "0 0 * * *; rm", 80, true, false, false));
        Assert.Throws<DomainException>(() => DockerCleanupPolicy.Create(true, "0 0 * * *", 0, true, false, false));
        Assert.Equal("0 3 * * 1", DockerCleanupPolicy.Create(true, " 0  3 * * 1 ", 50, false, false, false).CronExpression);
    }

    // ---- SRV-08 alert evaluator ----

    [Fact]
    public async Task DiskAlertReleasesTheDispatchThrottleAndPublishesOnce()
    {
        var notifications = new RecordingNotificationEventPublisher();
        var throttle = new FakeThrottle();
        var time = new ManualTime(Now);
        var evaluator = new ServerAlertEvaluator(notifications, throttle, time);
        Server server = NewServer(TeamId.New());
        var settings = ServerMonitoringSettings.CreateDefault(server.Id, Now);
        CancellationToken token = TestContext.Current.CancellationToken;

        Assert.Equal(DiskAlertDecision.Alert, await evaluator.EvaluateDiskAsync(server, settings, 95m, "/", token));
        Assert.Equal(DiskAlertDecision.Suppressed, await evaluator.EvaluateDiskAsync(server, settings, 96m, "/", token));
        Assert.Equal(DiskAlertDecision.None, await evaluator.EvaluateDiskAsync(server, settings, null, "/", token));

        TeamNotification alert = Assert.Single(notifications.Published);
        Assert.Equal(NotificationEventTypes.ServerDiskUsageHigh, alert.Notification.EventType);
        Assert.Equal(server.TeamId, alert.TeamId);
        Assert.Equal([NotificationEventTypes.ServerDiskUsageHigh], throttle.Released);
    }

    // ---- SRV-04 reconciliation ----

    [Fact]
    public async Task ReconcilerSchedulesADelayedStopCheckAndEnforcesRestartLimits()
    {
        CrossTeamFixture fixture = CrossTeamFixture.Create();
        var runtime = new FakeMonitoringRuntime();
        var jobs = new RecordingJobs();
        var notifications = new RecordingNotificationEventPublisher();
        var time = new ManualTime(Now);
        var reconciler = new ResourceStatusReconciler(fixture.Store.Context, runtime, jobs, notifications,
            new NullRealtime(), time);
        Server server = fixture.ServerA;
        var appLabels = new Dictionary<string, string>
        {
            ["vessel.managed"] = "true",
            ["vessel.application"] = fixture.ApplicationA.Id.Value.ToString("D")
        };
        CancellationToken token = TestContext.Current.CancellationToken;

        await reconciler.ReconcileAsync(server, [FakeMonitoringRuntime.Container("c1", "running", appLabels)], true,
            token);
        ResourceRuntimeStatus status = Assert.Single(fixture.Store.Set<ResourceRuntimeStatus>());
        Assert.Equal(ResourceRuntimeState.Running, status.State);

        // An incomplete snapshot without the container changes nothing.
        await reconciler.ReconcileAsync(server, [], false, token);
        Assert.Equal(ResourceRuntimeState.Running, status.State);

        time.Now = Now.AddMinutes(1);
        ResourceReconciliationResult stopped = await reconciler.ReconcileAsync(server,
            [FakeMonitoringRuntime.Container("c1", "exited", appLabels)], true, token);
        Assert.Equal(1, stopped.StopNotificationsScheduled);
        var scheduled = Assert.Single(jobs.Scheduled);
        Assert.Equal(nameof(NotifyResourceStoppedJob), scheduled.Job);
        Assert.Equal(NotifyResourceStoppedJob.ConfirmationDelay, scheduled.Delay);
        Assert.Empty(notifications.Published);

        status.ConfigureRestartLimit(3, Now);
        ResourceReconciliationResult limited = await reconciler.ReconcileAsync(server,
            [FakeMonitoringRuntime.Container("c1", "restarting", appLabels, restartCount: 3)], true, token);
        Assert.Equal(1, limited.RestartLimitsReached);
        Assert.Equal(["c1"], runtime.Stopped);
        TeamNotification limit = Assert.Single(notifications.Published);
        Assert.Equal(NotificationEventTypes.ApplicationRestartLimitReached, limit.Notification.EventType);
        // Vessel's own stop after the limit is expected and never reported as stopped unexpectedly.
        time.Now = Now.AddMinutes(2);
        await reconciler.ReconcileAsync(server,
            [FakeMonitoringRuntime.Container("c1", "exited", appLabels, restartCount: 3)], true, token);
        Assert.Single(jobs.Scheduled);
    }

    [Fact]
    public async Task ReconcilerIgnoresPreviewContainersAndOtherServersResources()
    {
        CrossTeamFixture fixture = CrossTeamFixture.Create();
        var reconciler = new ResourceStatusReconciler(fixture.Store.Context, new FakeMonitoringRuntime(),
            new RecordingJobs(), new RecordingNotificationEventPublisher(), new NullRealtime(), new ManualTime(Now));
        var preview = new Dictionary<string, string>
        {
            ["vessel.application"] = fixture.ApplicationA.Id.Value.ToString("D"),
            ["vessel.preview"] = Guid.NewGuid().ToString("D")
        };
        var foreign = new Dictionary<string, string>
        {
            ["vessel.application"] = fixture.ApplicationB.Id.Value.ToString("D")
        };

        await reconciler.ReconcileAsync(fixture.ServerA,
            [FakeMonitoringRuntime.Container("p1", "running", preview), FakeMonitoringRuntime.Container("x", "running", foreign)],
            true, TestContext.Current.CancellationToken);

        ResourceRuntimeStatus status = Assert.Single(fixture.Store.Set<ResourceRuntimeStatus>());
        Assert.Equal(fixture.ApplicationA.Id.Value, status.ResourceId);
        Assert.Equal(ResourceRuntimeState.Exited, status.State); // Production has no container.
    }

    [Fact]
    public async Task ExpectedStop_SuppressesStoppedNotificationForVesselInitiatedStops()
    {
        CrossTeamFixture fixture = CrossTeamFixture.Create();
        var notifications = new RecordingNotificationEventPublisher();
        var time = new ManualTime(Now);
        var recorder = new ExpectedStopRecorder(fixture.Store.Context, time,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ExpectedStopRecorder>.Instance);
        CancellationToken token = TestContext.Current.CancellationToken;

        // A resource never observed before gets a status row carrying the expectation.
        await recorder.ExpectStopAsync(ResourceRuntimeKind.Database, Guid.NewGuid(), fixture.TeamA.Id,
            fixture.ServerA.Id, cancellationToken: token);
        Assert.True(Assert.Single(fixture.Store.Set<ResourceRuntimeStatus>()).IsStopExpected(Now));

        var status = ResourceRuntimeStatus.Create(ResourceRuntimeKind.Application, fixture.ApplicationA.Id.Value,
            fixture.TeamA.Id, fixture.ServerA.Id, Now);
        status.Observe(new AggregatedRuntimeStatus(ResourceRuntimeState.Running, ResourceHealth.Healthy), 1, 1, 0, Now);
        fixture.Store.Set<ResourceRuntimeStatus>().Add(status);
        await recorder.ExpectStopAsync(ResourceRuntimeKind.Application, status.ResourceId, fixture.TeamA.Id,
            fixture.ServerA.Id, cancellationToken: token);
        status.Observe(new AggregatedRuntimeStatus(ResourceRuntimeState.Exited, ResourceHealth.Unknown), 1, 0, 0,
            Now.AddMinutes(1));

        var job = new NotifyResourceStoppedJob(fixture.Store.Context, notifications, time);
        Assert.False(await job.RunAsync(ResourceRuntimeKind.Application, status.ResourceId,
            status.StateChangedAt.UtcTicks, token));
        Assert.Empty(notifications.Published);

        // Once the window has passed, a stop is unexpected again.
        time.Now += ResourceRuntimeStatus.ExpectedStopWindow + TimeSpan.FromMinutes(1);
        Assert.False(status.IsStopExpected(time.GetUtcNow()));
    }

    [Fact]
    public async Task StoppedNotificationIsSentOnlyWhenStillStoppedAndNotDeploying()
    {
        CrossTeamFixture fixture = CrossTeamFixture.Create();
        var notifications = new RecordingNotificationEventPublisher();
        var time = new ManualTime(Now);
        var status = ResourceRuntimeStatus.Create(ResourceRuntimeKind.Application, fixture.ApplicationA.Id.Value,
            fixture.TeamA.Id, fixture.ServerA.Id, Now);
        status.Observe(new AggregatedRuntimeStatus(ResourceRuntimeState.Running, ResourceHealth.Healthy), 1, 1, 0, Now);
        status.Observe(new AggregatedRuntimeStatus(ResourceRuntimeState.Exited, ResourceHealth.Unknown), 1, 0, 0,
            Now.AddMinutes(1));
        fixture.Store.Set<ResourceRuntimeStatus>().Add(status);
        var job = new NotifyResourceStoppedJob(fixture.Store.Context, notifications, time);
        CancellationToken token = TestContext.Current.CancellationToken;

        // A newer transition (another stop/start) makes this check stale.
        Assert.False(await job.RunAsync(ResourceRuntimeKind.Application, status.ResourceId, 42, token));

        Deployment deploying = Deployment.Queue(fixture.ApplicationA.Id, fixture.ServerA.Id, null, null, Now);
        fixture.Store.Set<Deployment>().Add(deploying);
        Assert.False(await job.RunAsync(ResourceRuntimeKind.Application, status.ResourceId,
            status.StateChangedAt.UtcTicks, token));
        fixture.Store.Set<Deployment>().Remove(deploying);

        Assert.True(await job.RunAsync(ResourceRuntimeKind.Application, status.ResourceId,
            status.StateChangedAt.UtcTicks, token));
        TeamNotification sent = Assert.Single(notifications.Published);
        Assert.Equal(NotificationEventTypes.ApplicationStatusChanged, sent.Notification.EventType);
        Assert.Equal(fixture.TeamA.Id, sent.TeamId);
    }

    // ---- SRV-03 collection, queries and authorization ----

    [Fact]
    public async Task CollectionPersistsSamplesAlertsAndTriggersThresholdCleanup()
    {
        CrossTeamFixture fixture = CrossTeamFixture.Create();
        fixture.Actor.GrantInstanceAdmin(Now);
        var time = new ManualTime(Now);
        var collector = new FakeTelemetryCollector { Next = Telemetry(95m) };
        var jobs = new RecordingJobs();
        var notifications = new RecordingNotificationEventPublisher();
        ServerMetricsCollectionService service = Collection(fixture, collector, jobs, notifications, time);
        CancellationToken token = TestContext.Current.CancellationToken;

        ServerMetricsCollectionResult result = await service.CollectAsync(fixture.Actor.Id, fixture.TeamA.Id,
            fixture.ServerA.Id, token);

        Assert.True(result.Collected);
        ServerMetricSample sample = Assert.Single(fixture.Store.Set<ServerMetricSample>());
        Assert.Equal(95m, sample.DiskPercent);
        Assert.Equal(12.5m, sample.CpuPercent);
        Assert.Single(fixture.Store.Set<ContainerMetricSample>());
        Assert.Contains(nameof(RunDockerCleanupJob), jobs.Enqueued);
        Assert.Contains(notifications.Published,
            item => item.Notification.EventType == NotificationEventTypes.ServerDiskUsageHigh);

        // A collection failure records a cooldown for scheduled runs.
        collector.Fail = true;
        time.Now = Now.AddMinutes(1);
        Assert.False((await service.CollectScheduledAsync(fixture.ServerA.Id, token)).Collected);
        var calls = collector.Calls;
        Assert.False((await service.CollectScheduledAsync(fixture.ServerA.Id, token)).Collected);
        Assert.Equal(calls, collector.Calls);
    }

    [Fact]
    public async Task CollectionAndMetricsRequireAccessToTheOwningTeam()
    {
        CrossTeamFixture fixture = CrossTeamFixture.Create();
        var time = new ManualTime(Now);
        ServerMetricsCollectionService service = Collection(fixture, new FakeTelemetryCollector { Next = Telemetry(10m) },
            new RecordingJobs(), new RecordingNotificationEventPublisher(), time);
        var queries = new ServerMetricsQueryService(fixture.Store.Context, fixture.Authorization, time);
        CancellationToken token = TestContext.Current.CancellationToken;

        // Admin of team A acting through team A cannot reach team B's server.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.CollectAsync(fixture.Actor.Id, fixture.TeamA.Id, fixture.ServerB.Id, token));
        Assert.Throws<UnauthorizedAccessException>(() =>
            queries.GetServerMetrics(fixture.Actor.Id, fixture.TeamA.Id, fixture.ServerB.Id, null, null, null));
        // Host-local server: a team admin without instance administration cannot trigger collection.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.CollectAsync(fixture.Actor.Id, fixture.TeamA.Id, fixture.ServerA.Id, token));

        // The fleet lists only the active team's servers.
        FleetMetricsDto fleet = queries.GetFleet(fixture.Actor.Id, fixture.TeamA.Id);
        Assert.Equal([fixture.ServerA.Id.Value], fleet.Servers.Select(server => server.ServerId));
        Assert.Throws<DomainException>(() => queries.GetServerMetrics(fixture.Actor.Id, fixture.TeamA.Id,
            fixture.ServerA.Id, Now.AddDays(-40), Now, null));
    }

    [Fact]
    public async Task RetentionRollsUpCompletedHoursOnceAndPrunesExpiredRows()
    {
        CrossTeamFixture fixture = CrossTeamFixture.Create();
        var time = new ManualTime(Now.AddMinutes(10));
        ServerId serverId = fixture.ServerA.Id;
        fixture.Store.Set<ServerMetricSample>().AddRange([
            Raw(serverId, Now.AddMinutes(-50), 10m, 1, 2),
            Raw(serverId, Now.AddMinutes(-20), 20m, 1, 2),
            Raw(serverId, Now.AddMinutes(5), 50m, 1, 2),
            Raw(serverId, Now.AddDays(-2), 99m, 1, 2)
        ]);
        var retention = new ServerMetricsRetentionService(fixture.Store.Context,
            new QueryableServerMetricsPruner(fixture.Store.Context), time);
        CancellationToken token = TestContext.Current.CancellationToken;

        ServerMetricsRetentionResult first = await retention.RunAsync(token);
        ServerMetricsRetentionResult second = await retention.RunAsync(token);

        Assert.Equal(1, first.RollupsCreated);
        Assert.Equal(0, second.RollupsCreated);
        ServerMetricSample rollup = Assert.Single(fixture.Store.Set<ServerMetricSample>(),
            sample => sample.Resolution == MetricResolution.Hourly);
        Assert.Equal(15m, rollup.CpuPercent);
        Assert.DoesNotContain(fixture.Store.Set<ServerMetricSample>(), sample => sample.CpuPercent == 99m);
    }

    [Fact]
    public async Task DispatchStaggersServersAndSkipsDisabledMetrics()
    {
        CrossTeamFixture fixture = CrossTeamFixture.Create();
        var settings = ServerMonitoringSettings.CreateDefault(fixture.ServerB.Id, Now);
        settings.ConfigureMetrics(false, 60, Now);
        fixture.Store.Set<ServerMonitoringSettings>().Add(settings);
        var jobs = new RecordingJobs();
        var dispatch = new ServerMetricsDispatchJob(fixture.Store.Context, jobs, new ManualTime(Now));

        Assert.Equal(1, await dispatch.RunAsync(TestContext.Current.CancellationToken));
        var scheduled = Assert.Single(jobs.Scheduled);
        Assert.Equal(TimeSpan.FromSeconds(ServerMetricsDispatchJob.StaggerOffset(fixture.ServerA.Id)), scheduled.Delay);
        Assert.InRange(ServerMetricsDispatchJob.StaggerOffset(ServerId.New()), 0, ServerMetricsDispatchJob.StaggerSeconds - 1);
    }

    // ---- SRV-05 runtime logs ----

    [Fact]
    public async Task RuntimeLogsMergeReplicasByTimestampAndRedactSecrets()
    {
        CrossTeamFixture fixture = CrossTeamFixture.Create();
        fixture.Actor.GrantInstanceAdmin(Now);
        ServiceResource service = AddService(fixture, fixture.TeamA.Id, fixture.EnvironmentA.Id, fixture.ServerA.Id);
        var runtime = new FakeMonitoringRuntime();
        var labels = new Dictionary<string, string>
        {
            ["com.docker.compose.project"] = RuntimeResourceLabels.ServiceProjectName(service.Id.Value)
        };
        runtime.Containers.Add(FakeMonitoringRuntime.Container("a", "running", labels));
        runtime.Containers.Add(FakeMonitoringRuntime.Container("b", "exited", labels));
        runtime.Containers.Add(FakeMonitoringRuntime.Container("other", "running",
            new Dictionary<string, string> { ["com.docker.compose.project"] = "unrelated" }));
        runtime.Logs["a"] = ["2026-11-21T10:00:02.000000001Z second", "2026-11-21T10:00:04Z password=SuperSecret123"];
        runtime.Logs["b"] = ["2026-11-21T10:00:01Z first", "2026-11-21T10:00:03.5Z third"];
        runtime.Logs["other"] = ["2026-11-21T10:00:00Z never shown"];
        RuntimeLogQueryService service2 = Logs(fixture, runtime);

        RuntimeLogResultDto result = await service2.ReadAsync(fixture.Actor.Id, fixture.TeamA.Id,
            ResourceRuntimeKind.Service, service.Id.Value, new RuntimeLogQuery(), TestContext.Current.CancellationToken);

        Assert.Equal(["first", "second", "third", "password=<REDACTED>"], result.Lines.Select(line => line.Message));
        Assert.Equal(["b", "a", "b", "a"], result.Lines.Select(line => line.Container));
        Assert.Equal(2, result.Containers.Count);
        var export = await service2.ExportAsync(fixture.Actor.Id, fixture.TeamA.Id, ResourceRuntimeKind.Service,
            service.Id.Value, new RuntimeLogQuery(), TestContext.Current.CancellationToken);
        Assert.DoesNotContain("SuperSecret123", export, StringComparison.Ordinal);
        Assert.Contains("[b] first", export, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeLogsAreDeniedToReadOnlyMembersOtherTeamsAndNonAdminsOnHostLocalServers()
    {
        CrossTeamFixture fixture = CrossTeamFixture.Create();
        var runtime = new FakeMonitoringRuntime();
        runtime.Containers.Add(FakeMonitoringRuntime.Container("b", "running", new Dictionary<string, string>
        {
            ["vessel.application"] = fixture.ApplicationB.Id.Value.ToString("D")
        }));
        RuntimeLogQueryService service = Logs(fixture, runtime);
        CancellationToken token = TestContext.Current.CancellationToken;

        // Member (read-only) of team B: logs are read-sensitive.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReadAsync(fixture.Actor.Id,
            fixture.TeamB.Id, ResourceRuntimeKind.Application, fixture.ApplicationB.Id.Value, new RuntimeLogQuery(),
            token));
        // Admin of team A cannot reach team B's application through team A.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReadAsync(fixture.Actor.Id,
            fixture.TeamA.Id, ResourceRuntimeKind.Application, fixture.ApplicationB.Id.Value, new RuntimeLogQuery(),
            token));
        // Team admin, but the server is host-local and the actor is no instance administrator.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReadAsync(fixture.Actor.Id,
            fixture.TeamA.Id, ResourceRuntimeKind.Application, fixture.ApplicationA.Id.Value, new RuntimeLogQuery(),
            token));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
        {
            await foreach (RuntimeLogLineDto _ in service.FollowAsync(fixture.Actor.Id, fixture.TeamB.Id,
                               ResourceRuntimeKind.Application, fixture.ApplicationB.Id.Value, 10, true, token))
            {
            }
        });
        Assert.DoesNotContain(VesselPermissions.ResourcesReadLogs, VesselPermissions.ForRole(TeamRole.Member));
        Assert.Contains(VesselPermissions.ResourcesReadLogs, VesselPermissions.ForRole(TeamRole.Admin));
    }

    [Fact]
    public void RuntimeLogTimestampsAreSplitFromMessages()
    {
        (DateTimeOffset? timestamp, string message) = RuntimeLogQueryService.SplitTimestamp(
            "2026-11-21T10:00:02.123456789Z hello world");
        Assert.Equal(new DateTimeOffset(2026, 11, 21, 10, 0, 2, TimeSpan.Zero).AddTicks(1234567), timestamp);
        Assert.Equal("hello world", message);
        Assert.Equal((null, "plain line"), RuntimeLogQueryService.SplitTimestamp("plain line"));
    }

    // ---- SRV-07 Docker cleanup ----

    [Fact]
    public void CleanupPlanKeepsRollbackManagedInUseAndNewImagesAndResourceContainers()
    {
        var app = Guid.NewGuid();
        var deletedApp = Guid.NewGuid();
        var service = Guid.NewGuid();
        var inventory = new DockerCleanupInventory(new HashSet<Guid> { app }, new HashSet<Guid>(),
            new Dictionary<string, Guid> { [RuntimeResourceLabels.ServiceProjectName(service)] = service },
            new HashSet<string>(StringComparer.Ordinal) { $"vessel/{app:N}:keep" });
        ContainerStateReading[] containers =
        [
            FakeMonitoringRuntime.Container("app-stopped", "exited",
                new Dictionary<string, string> { ["vessel.managed"] = "true", ["vessel.application"] = app.ToString("D") },
                imageId: "sha256:current"),
            FakeMonitoringRuntime.Container("orphan", "exited",
                new Dictionary<string, string> { ["vessel.managed"] = "true", ["vessel.application"] = deletedApp.ToString("D") },
                imageId: "sha256:orphan"),
            FakeMonitoringRuntime.Container("proxy", "exited",
                new Dictionary<string, string> { ["vessel.managed"] = "true", ["vessel.proxy"] = "true" }),
            FakeMonitoringRuntime.Container("svc", "exited", new Dictionary<string, string>
                { ["com.docker.compose.project"] = RuntimeResourceLabels.ServiceProjectName(service) }),
            FakeMonitoringRuntime.Container("user", "exited", new Dictionary<string, string>()),
            FakeMonitoringRuntime.Container("running-orphan", "running",
                new Dictionary<string, string> { ["vessel.application"] = deletedApp.ToString("D") }, imageId: "sha256:busy")
        ];
        ImageDetails[] images =
        [
            Image("sha256:current", $"vessel/{app:N}:current"),
            Image("sha256:keep", $"vessel/{app:N}:keep"),
            Image("sha256:old", $"vessel/{app:N}:old"),
            Image("sha256:pr", $"vessel/{app:N}:pr-3-abc"),
            Image("sha256:orphan", $"vessel/{deletedApp:N}:x"),
            Image("sha256:busy", "busy:1"),
            Image("sha256:unused", "nginx:1.0"),
            Image("sha256:fresh", "fresh:1", created: Now.AddMinutes(-5)),
            Image("sha256:managed", "vessel-helper:1", labels: new Dictionary<string, string> { ["vessel.managed"] = "true" })
        ];

        DockerCleanupPlan plan = DockerCleanupPlanner.Plan(containers, images, inventory, Now);

        Assert.Equal(["orphan"], plan.ContainersToRemove.Select(container => container.Id));
        Assert.Equal([$"vessel/{app:N}:old", $"vessel/{deletedApp:N}:x", "nginx:1.0"],
            plan.ImagesToRemove.Select(item => item.Reference));
    }

    [Fact]
    public async Task CleanupRunRecordsHistorySkipsDuringDeploymentsAndKeepsVolumes()
    {
        CrossTeamFixture fixture = CrossTeamFixture.Create();
        fixture.Actor.GrantInstanceAdmin(Now);
        var runtime = new FakeMonitoringRuntime();
        runtime.Images.Add(Image("sha256:unused", "nginx:1.0"));
        var notifications = new RecordingNotificationEventPublisher();
        var jobs = new RecordingJobs();
        DockerCleanupService cleanup = Cleanup(fixture, runtime, jobs, notifications);
        CancellationToken token = TestContext.Current.CancellationToken;

        DockerCleanupExecutionDto? manual = await cleanup.RunAsync(fixture.ServerA.Id, DockerCleanupTrigger.Manual,
            fixture.Actor.Id, token);
        Assert.Equal(nameof(DockerCleanupStatus.Succeeded), manual!.Status);
        Assert.Equal(["nginx:1.0"], runtime.RemovedImages);
        Assert.DoesNotContain(RuntimePruneKind.AnonymousVolumes, runtime.Prunes);
        Assert.Contains(RuntimePruneKind.BuildCache, runtime.Prunes);
        Assert.Contains(notifications.Published,
            item => item.Notification.EventType == NotificationEventTypes.DockerCleanupSucceeded);

        // A scheduled run while a deployment is active is skipped and recorded.
        fixture.Store.Set<Deployment>().Add(Deployment.Queue(fixture.ApplicationA.Id, fixture.ServerA.Id, null, null,
            DateTimeOffset.UtcNow));
        DockerCleanupExecutionDto? scheduled = await cleanup.RunAsync(fixture.ServerA.Id,
            DockerCleanupTrigger.Scheduled, null, token);
        Assert.Equal(nameof(DockerCleanupStatus.Skipped), scheduled!.Status);
        Assert.Equal(2, cleanup.ListExecutions(fixture.Actor.Id, fixture.TeamA.Id, fixture.ServerA.Id).Count);

        // Volumes are pruned only after an explicit opt-in.
        await cleanup.UpdatePolicyAsync(fixture.Actor.Id, fixture.TeamA.Id, fixture.ServerA.Id,
            new UpdateDockerCleanupPolicyRequest(DeleteUnusedVolumes: true, CronExpression: "0 4 * * *"), token);
        Assert.Contains(jobs.Recurring, item => item.EndsWith("0 4 * * *", StringComparison.Ordinal));
        await cleanup.RunAsync(fixture.ServerA.Id, DockerCleanupTrigger.Manual, fixture.Actor.Id, token);
        Assert.Contains(RuntimePruneKind.AnonymousVolumes, runtime.Prunes);
    }

    [Fact]
    public async Task CleanupApisRequireServerAccessAndTypedConfirmation()
    {
        CrossTeamFixture fixture = CrossTeamFixture.Create();
        var runtime = new FakeMonitoringRuntime();
        runtime.Images.Add(Image("sha256:unused", "nginx:1.0"));
        DockerCleanupService cleanup = Cleanup(fixture, runtime, new RecordingJobs(),
            new RecordingNotificationEventPublisher());
        CancellationToken token = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => cleanup.UpdatePolicyAsync(fixture.Actor.Id,
            fixture.TeamB.Id, fixture.ServerB.Id, new UpdateDockerCleanupPolicyRequest(ThresholdPercent: 50), token));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            cleanup.RequestManualRunAsync(fixture.Actor.Id, fixture.TeamA.Id, fixture.ServerB.Id, token));
        // Host-local server without instance administration.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            cleanup.ListImagesAsync(fixture.Actor.Id, fixture.TeamA.Id, fixture.ServerA.Id, 1, 50, token));

        fixture.Actor.GrantInstanceAdmin(Now);
        await Assert.ThrowsAsync<DomainException>(() => cleanup.DeleteImageAsync(fixture.Actor.Id,
            fixture.TeamA.Id, fixture.ServerA.Id, new DeleteServerImageRequest("nginx:1.0", "nginx"), token));
        ServerImagesPageDto page = await cleanup.ListImagesAsync(fixture.Actor.Id, fixture.TeamA.Id,
            fixture.ServerA.Id, 1, 50, token);
        Assert.True(Assert.Single(page.Items).Reclaimable);
        await cleanup.DeleteImageAsync(fixture.Actor.Id, fixture.TeamA.Id, fixture.ServerA.Id,
            new DeleteServerImageRequest("nginx:1.0", "nginx:1.0"), token);
        Assert.Equal(["nginx:1.0"], runtime.RemovedImages);
    }

    // ---- helpers ----

    private static ServerMetricSample Raw(ServerId serverId, DateTimeOffset at, decimal cpu, long memoryUsed,
        long memoryTotal)
    {
        return ServerMetricSample.CreateRaw(serverId, at, cpu, 2, 0.5m, 0.4m, 0.3m, memoryTotal, memoryUsed, "/",
            100, 50, 10, 20, 1, 2);
    }

    private static ImageDetails Image(string id, string tag, DateTimeOffset? created = null,
        IReadOnlyDictionary<string, string>? labels = null)
    {
        return new ImageDetails(id, [tag], 1000, created ?? Now.AddDays(-3), labels ?? new Dictionary<string, string>());
    }

    private static Server NewServer(TeamId teamId)
    {
        return Server.Create(teamId, new ResourceName("server"), new ServerAddress("10.0.0.1", new PortNumber(22)),
            ServerConnectionType.Ssh, ContainerRuntimeKind.Docker, Now);
    }

    private static ServiceResource AddService(CrossTeamFixture fixture, TeamId teamId, EnvironmentId environmentId,
        ServerId serverId)
    {
        var service = ServiceResource.Create(teamId, environmentId, serverId, new ResourceName("svc"), "pgadmin",
            "1", "{\"password\":\"SuperSecret123\",\"email\":\"a@example.com\"}", Now);
        fixture.Store.Set<ServiceResource>().Add(service);
        return service;
    }

    private static ServerTelemetry Telemetry(decimal diskPercent)
    {
        var used = (long)diskPercent;
        return new ServerTelemetry(Now,
            new HostMetricsReading(12.5m, 4, 1m, 1m, 1m, 1000, 500, 10, 20,
                [new DiskUsageReading("/", "/dev/sda1", 100, used, 100 - used)]),
            [new ContainerStatsReading("c1", "web", 5m, 100, 1000, 1, 2)],
            [FakeMonitoringRuntime.Container("c1abcdef", "running", new Dictionary<string, string>())],
            true);
    }

    private static ServerMetricsCollectionService Collection(CrossTeamFixture fixture,
        IServerTelemetryCollector collector, RecordingJobs jobs, RecordingNotificationEventPublisher notifications,
        TimeProvider time)
    {
        var reconciler = new ResourceStatusReconciler(fixture.Store.Context, new FakeMonitoringRuntime(), jobs,
            notifications, new NullRealtime(), time);
        return new ServerMetricsCollectionService(fixture.Store.Context, fixture.Authorization, collector,
            new InMemoryDistributedLockManager(time), reconciler,
            new ServerAlertEvaluator(notifications, new FakeThrottle(), time), jobs, new NullRealtime(), time);
    }

    private static RuntimeLogQueryService Logs(CrossTeamFixture fixture, FakeMonitoringRuntime runtime)
    {
        return new RuntimeLogQueryService(fixture.Store.Context, fixture.Authorization, runtime, new NoVault(),
            new ValueRedactor(), new NullAudit(), new ManualTime(Now));
    }

    private static DockerCleanupService Cleanup(CrossTeamFixture fixture, FakeMonitoringRuntime runtime,
        RecordingJobs jobs, RecordingNotificationEventPublisher notifications)
    {
        return new DockerCleanupService(fixture.Store.Context, fixture.Authorization, runtime,
            new InMemoryDistributedLockManager(TimeProvider.System), jobs, jobs, notifications, new NullAudit(),
            TimeProvider.System);
    }
}
