using System.Linq.Expressions;
using Vessel.Application.Deployments;
using Vessel.Application.Deployments.Queue;
using Vessel.Application.Jobs;
using Vessel.Application.Realtime;
using Vessel.Domain;
using Vessel.Domain.Applications;
using Vessel.Domain.Common;
using Vessel.Domain.Deployments;
using Vessel.Domain.Projects;
using Vessel.Domain.Servers;
using Vessel.Domain.ValueObjects;
using Vessel.Domain.Webhooks;
using Vessel.UnitTests.Security;
using AppEntity = Vessel.Domain.Applications.Application;
using EnvironmentEntity = Vessel.Domain.Projects.Environment;

namespace Vessel.UnitTests.Deployments;

/// <summary>DEP-04 / DEP-05: queue admission, dedupe, concurrency limits, auto-cancel and stuck recovery.</summary>
public sealed class DeploymentQueueTests
{
    private const string CommitA = "0123456789abcdef0123456789abcdef01234567";
    private const string CommitB = "89abcdef0123456789abcdef0123456789abcdef";
    private const string CommitC = "fedcba9876543210fedcba9876543210fedcba98";

    private readonly InMemoryVesselDb _store = InMemoryVesselDb.Create();
    private readonly RecordingDispatcher _jobs = new();
    private readonly RecordingCancellations _cancellations = new();
    private readonly MutableTime _time = new(DateTimeOffset.Parse("2026-11-20T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    private readonly TeamId _team = TeamId.New();

    [Fact]
    public async Task Second_request_while_one_runs_is_queued_not_rejected()
    {
        (_, AppEntity app) = AddApplication();

        DeploymentAdmission first = await Admit(app, CommitA);
        DeploymentAdmission second = await Admit(app, CommitB);

        Assert.Equal(DeploymentAdmissionOutcome.Dispatched, first.Outcome);
        Assert.Equal(DeploymentAdmissionOutcome.Queued, second.Outcome);
        Assert.Equal(DeploymentStatus.Queued, second.Deployment.Status);
        Assert.Null(second.Deployment.DispatchedAt);
        Assert.Equal(1, _jobs.Enqueued);
    }

    [Fact]
    public async Task Identical_active_request_is_deduplicated_unless_forced()
    {
        (_, AppEntity app) = AddApplication();
        DeploymentAdmission first = await Admit(app, CommitA);

        DeploymentAdmission duplicate = await Admit(app, CommitA);
        DeploymentAdmission forced = await Admit(app, CommitA, force: true);

        Assert.Equal(DeploymentAdmissionOutcome.Deduplicated, duplicate.Outcome);
        Assert.Equal(first.Deployment.Id, duplicate.Deployment.Id);
        Assert.NotEqual(first.Deployment.Id, forced.Deployment.Id);
        Assert.Equal(2, Deployments.Count);
    }

    [Fact]
    public async Task Branch_head_requests_dedupe_against_each_other()
    {
        (_, AppEntity app) = AddApplication();
        await Admit(app, CommitA);
        DeploymentAdmission head = await Admit(app, null);

        DeploymentAdmission again = await Admit(app, "HEAD");

        Assert.Equal(DeploymentAdmissionOutcome.Deduplicated, again.Outcome);
        Assert.Equal(head.Deployment.Id, again.Deployment.Id);
    }

    [Fact]
    public async Task Server_concurrent_build_limit_holds_extra_deployments_until_a_slot_frees()
    {
        (Server server, AppEntity first) = AddApplication();
        server.UpdateDeploymentLimits(2, 25, _time.GetUtcNow());
        AppEntity second = AddApplication(server).Application;
        AppEntity third = AddApplication(server).Application;

        DeploymentAdmission a = await Admit(first, CommitA);
        DeploymentAdmission b = await Admit(second, CommitA);
        DeploymentAdmission c = await Admit(third, CommitA);

        Assert.Equal(DeploymentAdmissionOutcome.Dispatched, a.Outcome);
        Assert.Equal(DeploymentAdmissionOutcome.Dispatched, b.Outcome);
        Assert.Equal(DeploymentAdmissionOutcome.Queued, c.Outcome);

        await Queue().ClaimRunAsync(a.Deployment, TestContext.Current.CancellationToken);
        await Queue().TransitionAsync(a.Deployment, (item, now) => item.MarkSucceeded("image", now),
            TestContext.Current.CancellationToken);
        await Queue().AdvanceAsync(server.Id, TestContext.Current.CancellationToken);

        Assert.NotNull(c.Deployment.DispatchedAt);
        Assert.Equal(3, _jobs.Enqueued);
    }

    [Fact]
    public async Task Full_server_queue_rejects_new_deployments()
    {
        (Server server, AppEntity first) = AddApplication();
        server.UpdateDeploymentLimits(1, 1, _time.GetUtcNow());
        AppEntity second = AddApplication(server).Application;
        AppEntity third = AddApplication(server).Application;
        await Admit(first, CommitA);
        await Admit(second, CommitA);

        await Assert.ThrowsAsync<DomainException>(() => Admit(third, CommitA));
    }

    [Fact]
    public async Task Preview_and_production_deployments_run_concurrently()
    {
        (_, AppEntity app) = AddApplication();
        ApplicationPreview preview = AddPreview(app, 7);

        DeploymentAdmission production = await Admit(app, CommitA);
        DeploymentAdmission previewDeployment = await Admit(app, CommitB, previewId: preview.Id);

        Assert.Equal(DeploymentAdmissionOutcome.Dispatched, production.Outcome);
        Assert.Equal(DeploymentAdmissionOutcome.Dispatched, previewDeployment.Outcome);
    }

    [Fact]
    public async Task Trigger_and_parent_deployment_are_recorded()
    {
        (_, AppEntity app) = AddApplication();
        DeploymentAdmission parent = await Admit(app, CommitA);

        DeploymentAdmission redeploy = await Queue().AdmitAsync(new DeploymentAdmissionRequest(app, null,
            DeploymentTrigger.Api, CommitB, ParentDeploymentId: parent.Deployment.Id), TestContext.Current.CancellationToken);

        Assert.Equal(DeploymentTrigger.Api, redeploy.Deployment.Trigger);
        Assert.Equal(parent.Deployment.Id, redeploy.Deployment.ParentDeploymentId);
        Assert.Equal(DeploymentTrigger.Manual, parent.Deployment.Trigger);
    }

    [Fact]
    public async Task Newer_webhook_push_cancels_older_queued_webhook_deployment_by_default()
    {
        (_, AppEntity app) = AddApplication();
        DeploymentAdmission running = await Admit(app, CommitA, DeploymentTrigger.Webhook);
        DeploymentAdmission queued = await Admit(app, CommitB, DeploymentTrigger.Webhook);

        DeploymentAdmission newest = await Admit(app, CommitC, DeploymentTrigger.Webhook);

        Assert.Equal(DeploymentStatus.CanceledByUser, queued.Deployment.Status);
        Assert.Contains(newest.Deployment.Id.Value.ToString("D"), queued.Deployment.CancellationReason);
        Assert.Contains(queued.Deployment.LogLines, line => line.Message.Contains("replaces it"));
        Assert.Equal([queued.Deployment.Id], newest.Superseded);
        // A building deployment is not cancelled unless the application opts in.
        Assert.NotEqual(DeploymentStatus.CanceledByUser, running.Deployment.Status);
        Assert.NotEqual(DeploymentStatus.CancelRequested, running.Deployment.Status);
    }

    [Fact]
    public async Task Queued_auto_cancel_can_be_turned_off()
    {
        (_, AppEntity app) = AddApplication(settings: DeploymentSettings.Default with
        { AutoCancelQueuedDeployments = false });
        await Admit(app, CommitA, DeploymentTrigger.Webhook);
        DeploymentAdmission queued = await Admit(app, CommitB, DeploymentTrigger.Webhook);

        await Admit(app, CommitC, DeploymentTrigger.Webhook);

        Assert.Equal(DeploymentStatus.Queued, queued.Deployment.Status);
    }

    [Fact]
    public async Task Building_auto_cancel_stops_older_builds_but_never_one_in_its_deploy_phase()
    {
        (_, AppEntity app) = AddApplication(settings: DeploymentSettings.Default with
        { AutoCancelBuildingDeployments = true });
        (_, AppEntity other) = AddApplication(settings: DeploymentSettings.Default with
        { AutoCancelBuildingDeployments = true });
        DeploymentAdmission building = await Admit(app, CommitA, DeploymentTrigger.Webhook);
        await Queue().ClaimRunAsync(building.Deployment, TestContext.Current.CancellationToken);
        DeploymentAdmission deploying = await Admit(other, CommitA, DeploymentTrigger.Webhook);
        await Queue().ClaimRunAsync(deploying.Deployment, TestContext.Current.CancellationToken);
        Assert.True(await Queue().TryStartDeployPhaseAsync(deploying.Deployment,
            TestContext.Current.CancellationToken));

        DeploymentAdmission newer = await Admit(app, CommitB, DeploymentTrigger.Webhook);
        await Admit(other, CommitB, DeploymentTrigger.Webhook);

        Assert.Equal(DeploymentStatus.CancelRequested, building.Deployment.Status);
        Assert.Contains(building.Deployment.Id, _cancellations.Cancelled);
        Assert.Equal(DeploymentStatus.InProgress, deploying.Deployment.Status);
        Assert.DoesNotContain(deploying.Deployment.Id, _cancellations.Cancelled);
        // The newer deployment waits until the cancelled build has stopped.
        Assert.Equal(DeploymentAdmissionOutcome.Queued, newer.Outcome);
        // A superseded run that reaches its deploy phase checkpoint does not touch containers.
        Assert.False(await Queue().TryStartDeployPhaseAsync(building.Deployment,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Manual_restart_and_rollback_deployments_are_never_auto_cancelled()
    {
        (_, AppEntity app) = AddApplication();
        await Admit(app, CommitA, DeploymentTrigger.Manual);
        DeploymentAdmission manual = await Admit(app, CommitB, DeploymentTrigger.Manual);
        DeploymentAdmission restart = await Admit(app, CommitC, DeploymentTrigger.Restart);

        await Admit(app, "1111111111111111111111111111111111111111", DeploymentTrigger.Webhook);
        await Queue().AdmitAsync(new DeploymentAdmissionRequest(app, null, DeploymentTrigger.Api,
                "2222222222222222222222222222222222222222",
                CancelPrevious: CancelPreviousDeployments.Building),
            TestContext.Current.CancellationToken);

        Assert.Equal(DeploymentStatus.Queued, manual.Deployment.Status);
        Assert.Equal(DeploymentStatus.Queued, restart.Deployment.Status);
    }

    [Fact]
    public async Task Api_deployment_cancels_older_webhook_and_api_deployments_only_when_asked()
    {
        (_, AppEntity app) = AddApplication();
        await Admit(app, CommitA, DeploymentTrigger.Manual);
        DeploymentAdmission webhook = await Admit(app, CommitB, DeploymentTrigger.Webhook);

        await Queue().AdmitAsync(new DeploymentAdmissionRequest(app, null, DeploymentTrigger.Api, CommitC),
            TestContext.Current.CancellationToken);
        Assert.Equal(DeploymentStatus.Queued, webhook.Deployment.Status);

        await Queue().AdmitAsync(new DeploymentAdmissionRequest(app, null, DeploymentTrigger.Api,
                "3333333333333333333333333333333333333333", CancelPrevious: CancelPreviousDeployments.Queued),
            TestContext.Current.CancellationToken);

        Assert.Equal(DeploymentStatus.CanceledByUser, webhook.Deployment.Status);
        Assert.Single(Deployments, item => item.Trigger == DeploymentTrigger.Api &&
                                           item.Status == DeploymentStatus.CanceledByUser);
    }

    [Fact]
    public async Task Auto_cancel_is_scoped_to_application_preview_and_server()
    {
        (_, AppEntity app) = AddApplication();
        ApplicationPreview preview = AddPreview(app, 3);
        await Admit(app, CommitA, DeploymentTrigger.Webhook);
        DeploymentAdmission production = await Admit(app, CommitB, DeploymentTrigger.Webhook);
        await Admit(app, CommitA, DeploymentTrigger.Webhook, previewId: preview.Id);

        await Admit(app, CommitC, DeploymentTrigger.Webhook, previewId: preview.Id);

        Assert.Equal(DeploymentStatus.Queued, production.Deployment.Status);
    }

    [Fact]
    public void Cancel_previous_parsing_accepts_only_known_values()
    {
        Assert.Equal(CancelPreviousDeployments.None, SupersededDeploymentPolicy.Parse(null));
        Assert.Equal(CancelPreviousDeployments.Queued, SupersededDeploymentPolicy.Parse("queued"));
        Assert.Equal(CancelPreviousDeployments.Building, SupersededDeploymentPolicy.Parse("Building"));
        Assert.Throws<DomainException>(() => SupersededDeploymentPolicy.Parse("all"));
    }

    [Fact]
    public async Task Claim_starts_dispatched_deployment_and_reports_orphaned_running_one()
    {
        (_, AppEntity app) = AddApplication();
        DeploymentAdmission admission = await Admit(app, CommitA);

        Assert.Equal(DeploymentRunClaim.Started,
            await Queue().ClaimRunAsync(admission.Deployment, TestContext.Current.CancellationToken));
        Assert.Equal(DeploymentStatus.InProgress, admission.Deployment.Status);
        // A second worker that gets the run lock while the deployment is running finds it orphaned.
        Assert.Equal(DeploymentRunClaim.Orphaned,
            await Queue().ClaimRunAsync(admission.Deployment, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Undispatchable_deployment_is_failed_instead_of_blocking_the_queue()
    {
        (_, AppEntity app) = AddApplication();
        _jobs.Fail = true;

        await Assert.ThrowsAsync<BackgroundJobsUnavailableException>(() => Admit(app, CommitA));

        Deployment deployment = Assert.Single(Deployments);
        Assert.Equal(DeploymentStatus.Failed, deployment.Status);
        Assert.Contains(deployment.LogLines, line => line.Message.Contains("could not be started"));
    }

    [Fact]
    public async Task Recovery_fails_stuck_deployments_and_restarts_waiting_queues()
    {
        (Server server, AppEntity app) = AddApplication();
        server.UpdateDeploymentLimits(1, 25, _time.GetUtcNow());
        AppEntity other = AddApplication(server).Application;
        DeploymentAdmission stuck = await Admit(app, CommitA);
        await Queue().ClaimRunAsync(stuck.Deployment, TestContext.Current.CancellationToken);
        DeploymentAdmission waiting = await Admit(other, CommitA);
        Assert.Null(waiting.Deployment.DispatchedAt);

        _time.Advance(TimeSpan.FromMinutes(71));
        var failed = await new DeploymentQueueJob(Queue(), _time).RecoverStuckAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(1, failed);
        Assert.Equal(DeploymentStatus.Failed, stuck.Deployment.Status);
        Assert.NotNull(waiting.Deployment.DispatchedAt);
    }

    [Fact]
    public async Task Recovery_keeps_active_deployments_and_startup_recovery_fails_orphans()
    {
        (_, AppEntity app) = AddApplication();
        DeploymentAdmission running = await Admit(app, CommitA);
        await Queue().ClaimRunAsync(running.Deployment, TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(0, await new DeploymentQueueJob(Queue(), _time).RecoverStuckAsync(
            TestContext.Current.CancellationToken));
        Assert.Equal(DeploymentStatus.InProgress, running.Deployment.Status);

        // The control plane restarted: nothing from the previous process is running any more.
        DateTimeOffset startedAt = _time.GetUtcNow();
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(1, await new DeploymentQueueJob(Queue(), _time).RecoverAfterStartupAsync(startedAt,
            TestContext.Current.CancellationToken));
        Assert.Equal(DeploymentStatus.Failed, running.Deployment.Status);
        Assert.Contains(running.Deployment.LogLines, line => line.Message.Contains("control plane restarted"));
    }

    [Fact]
    public async Task User_cancel_of_queued_deployment_cancels_it_and_after_deploy_phase_finishes_instead()
    {
        (_, AppEntity app) = AddApplication();
        DeploymentAdmission running = await Admit(app, CommitA);
        DeploymentAdmission queued = await Admit(app, CommitB);
        await Queue().ClaimRunAsync(running.Deployment, TestContext.Current.CancellationToken);
        await Queue().TryStartDeployPhaseAsync(running.Deployment, TestContext.Current.CancellationToken);

        await Queue().RequestCancellationAsync(queued.Deployment, "Cancelled by user.",
            TestContext.Current.CancellationToken);
        await Queue().RequestCancellationAsync(running.Deployment, "Cancelled by user.",
            TestContext.Current.CancellationToken);

        Assert.Equal(DeploymentStatus.CanceledByUser, queued.Deployment.Status);
        Assert.Equal(DeploymentStatus.CancelRequested, running.Deployment.Status);
        Assert.DoesNotContain(running.Deployment.Id, _cancellations.Cancelled);
        running.Deployment.MarkSucceeded("image", _time.GetUtcNow());
        Assert.Equal(DeploymentStatus.Succeeded, running.Deployment.Status);
    }

    [Fact]
    public void Log_sequence_follows_lines_persisted_by_other_writers()
    {
        var deployment = Deployment.Queue(Vessel.Domain.ApplicationId.New(), ServerId.New(), null, CommitA, _time.GetUtcNow());
        deployment.ObserveLogSequence(41);

        deployment.AddLogLine("system", "next", _time.GetUtcNow());

        Assert.Equal(42, deployment.LogLines.Single().Sequence);
    }

    private List<Deployment> Deployments => _store.Set<Deployment>();

    private DeploymentQueueService Queue()
    {
        return new DeploymentQueueService(_store.Context, new InMemoryDistributedLockManager(TimeProvider.System), _jobs,
            _cancellations, new NullRealtime(), _time);
    }

    private Task<DeploymentAdmission> Admit(AppEntity app, string? commit,
        DeploymentTrigger trigger = DeploymentTrigger.Manual, bool force = false,
        ApplicationPreviewId? previewId = null)
    {
        return Queue().AdmitAsync(new DeploymentAdmissionRequest(app, null, trigger, commit, previewId,
                trigger == DeploymentTrigger.Webhook ? WebhookEventId.New() : null, force),
            TestContext.Current.CancellationToken);
    }

    private (Server Server, AppEntity Application) AddApplication(Server? server = null,
        DeploymentSettings? settings = null)
    {
        DateTimeOffset now = _time.GetUtcNow();
        var project = Project.Create(_team, new ResourceName("project"), now);
        var environment = EnvironmentEntity.CreateProduction(project.Id, now);
        if (server is null)
        {
            server = Server.Create(_team, new ResourceName("server"),
                new ServerAddress("10.0.0.5", new PortNumber(22)), ServerConnectionType.Ssh,
                ContainerRuntimeKind.Docker, now);
            server.ChangeStatus(ServerStatus.Reachable, now);
            _store.Set<Server>().Add(server);
        }

        var application = AppEntity.Create(environment.Id, server.Id, new ResourceName("app"),
            new GitSource(new RepositoryUrl("https://github.com/org/app.git"), "main"),
            BuildConfiguration.Default(ApplicationBuildPack.Dockerfile), now);
        if (settings.HasValue) application.UpdateDeploymentSettings(settings.Value, now);
        _store.Set<Project>().Add(project);
        _store.Set<EnvironmentEntity>().Add(environment);
        _store.Set<AppEntity>().Add(application);
        return (server, application);
    }

    private ApplicationPreview AddPreview(AppEntity app, int number)
    {
        var preview = ApplicationPreview.Open(app.Id, WebhookProvider.GitHub, number, "feature", "main", CommitA,
            null, null, _time.GetUtcNow(), "org/app", true);
        _store.Set<ApplicationPreview>().Add(preview);
        return preview;
    }

    private sealed class RecordingDispatcher : IBackgroundJobDispatcher
    {
        public int Enqueued { get; private set; }

        public bool Fail { get; set; }

        public string Enqueue<TJob>(Expression<Func<TJob, Task>> methodCall)
        {
            if (Fail) throw new BackgroundJobsUnavailableException("Hangfire storage is disabled.");
            Enqueued++;
            return "job-" + Enqueued;
        }

        public string Schedule<TJob>(Expression<Func<TJob, Task>> methodCall, TimeSpan delay)
        {
            return "scheduled";
        }
    }

    private sealed class RecordingCancellations : IDeploymentCancellationRegistry
    {
        private readonly DeploymentCancellationRegistry _inner = new();

        public List<DeploymentId> Cancelled { get; } = [];

        public DeploymentCancellationScope Register(DeploymentId deploymentId, CancellationToken jobToken)
        {
            return _inner.Register(deploymentId, jobToken);
        }

        public bool Cancel(DeploymentId deploymentId)
        {
            Cancelled.Add(deploymentId);
            return _inner.Cancel(deploymentId);
        }
    }

    private sealed class NullRealtime : IRealtimeNotifier
    {
        public Task PublishAsync(RealtimeGroup group, RealtimeMessage message,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    internal sealed class MutableTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow()
        {
            return _now;
        }

        public void Advance(TimeSpan by)
        {
            _now += by;
        }
    }
}
