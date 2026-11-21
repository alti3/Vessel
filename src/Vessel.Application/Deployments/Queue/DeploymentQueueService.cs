using Vessel.Application.Jobs;
using Vessel.Application.Persistence;
using Vessel.Application.Realtime;
using Vessel.Application.Redis;
using Vessel.Domain;
using Vessel.Domain.Common;
using Vessel.Domain.Deployments;
using Vessel.Domain.Servers;
using Vessel.Domain.ValueObjects;
using AppEntity = Vessel.Domain.Applications.Application;

namespace Vessel.Application.Deployments.Queue;

/// <summary>What to queue: the trigger, the requested ref and the auto-cancel request of the caller.</summary>
public sealed record DeploymentAdmissionRequest(
    AppEntity Application,
    UserId? ActorUserId,
    DeploymentTrigger Trigger,
    string? CommitSha,
    ApplicationPreviewId? PreviewId = null,
    WebhookEventId? WebhookEventId = null,
    bool ForceRebuild = false,
    CancelPreviousDeployments CancelPrevious = CancelPreviousDeployments.None,
    DeploymentId? ParentDeploymentId = null,
    DeploymentRollbackTarget? Rollback = null,
    string QueuedMessage = "Deployment queued.");

/// <summary>Rollback target: an earlier successful production deployment and/or a listed local image.</summary>
public sealed record DeploymentRollbackTarget(string CommitSha, DeploymentId? TargetDeploymentId, string? Image);

public enum DeploymentAdmissionOutcome
{
    /// <summary>Queued; it starts when the application and server have a free slot.</summary>
    Queued,

    /// <summary>Queued and handed to a worker right away.</summary>
    Dispatched,

    /// <summary>An identical deployment is already queued or running; no new deployment was created.</summary>
    Deduplicated
}

public sealed record DeploymentAdmission(
    Deployment Deployment,
    DeploymentAdmissionOutcome Outcome,
    IReadOnlyList<DeploymentId> Superseded);

/// <summary>Result of a worker picking up a deployment.</summary>
public enum DeploymentRunClaim
{
    /// <summary>The deployment is now running in this worker.</summary>
    Started,

    /// <summary>The deployment already finished (or was cancelled); nothing to do.</summary>
    Terminal,

    /// <summary>The deployment is queued but has no free slot; the queue dispatches it later.</summary>
    Waiting,

    /// <summary>The deployment was running but no worker holds it any more (the previous worker stopped).</summary>
    Orphaned
}

/// <summary>
///     Transactional deployment queue (Coolify <c>queue_application_deployment</c>, <c>next_queuable</c>,
///     <c>queue_next_deployment</c>). Every decision runs under one lock per server, so concurrent requests cannot both
///     see an idle queue and start two deployments, exceed the server's concurrent build limit, or race a superseding
///     cancel with the start of a deploy phase:
///     <list type="bullet">
///         <item>A full server queue (<see cref="Server.DeploymentQueueLimit" />) rejects the request.</item>
///         <item>
///             An active deployment with the same application, preview and commit is returned instead of a duplicate,
///             unless the request forces a rebuild or is a rollback.
///         </item>
///         <item>Older deployments are cancelled per <see cref="SupersededDeploymentPolicy" />.</item>
///         <item>
///             A queued deployment starts only when its application and preview have no active deployment and the
///             server runs fewer than <see cref="Server.ConcurrentBuilds" /> deployments; every terminal transition
///             advances the queue.
///         </item>
///     </list>
/// </summary>
public sealed class DeploymentQueueService(
    IVesselDbContext dbContext,
    IDistributedLockManager locks,
    IBackgroundJobDispatcher backgroundJobs,
    IDeploymentCancellationRegistry cancellations,
    IRealtimeNotifier realtime,
    TimeProvider timeProvider)
{
    public static readonly TimeSpan LockLease = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan LockWait = TimeSpan.FromSeconds(30);

    /// <summary>A dispatched deployment that no worker started within this time is dispatched again.</summary>
    public static readonly TimeSpan RedispatchAfter = TimeSpan.FromMinutes(10);

    public static string LockKey(ServerId serverId)
    {
        return $"deployments:queue:{serverId.Value:D}";
    }

    public async Task<DeploymentAdmission> AdmitAsync(DeploymentAdmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        AppEntity application = request.Application;
        // SEC-10: every trigger (UI, API, webhook, rollback) passes the same queue-time ref validation; the runner
        // validates again at start.
        application.GitSource.EnsureSafeForGit();
        foreach (var requested in new[] { request.CommitSha, request.Rollback?.CommitSha })
            if (!string.IsNullOrWhiteSpace(requested) && requested != "HEAD")
                GitRef.Parse(requested, "commit or ref");
        ServerId serverId = application.ServerId;
        var dispatch = new List<Deployment>();
        var supersededBuilding = new List<Deployment>();
        var superseded = new List<Deployment>();
        Deployment deployment;

        await using (DistributedLockHandle handle = await AcquireAsync(serverId, cancellationToken))
        {
            Server server = dbContext.Servers.SingleOrDefault(item => item.Id == serverId)
                            ?? throw new InvalidOperationException("Deployment server was not found.");
            await dbContext.RefreshAsync(server, cancellationToken);
            DateTimeOffset now = timeProvider.GetUtcNow();
            var commit = request.Rollback?.CommitSha ?? request.CommitSha;

            if (!request.ForceRebuild && request.Rollback is null)
            {
                Deployment? existing = await FindDuplicateAsync(application, request.PreviewId, commit,
                    cancellationToken);
                if (existing is not null)
                    return new DeploymentAdmission(existing, DeploymentAdmissionOutcome.Deduplicated, []);
            }

            var waiting = dbContext.Deployments.Count(item =>
                item.ServerId == serverId && item.Status == DeploymentStatus.Queued && item.DispatchedAt == null);
            if (waiting >= server.DeploymentQueueLimit)
                throw new DomainException(
                    "Deployment queue is full. Please wait for existing deployments to complete.");

            deployment = request.Rollback is { } rollback
                ? Deployment.QueueRollback(application.Id, serverId, request.ActorUserId, rollback.CommitSha,
                    rollback.TargetDeploymentId, rollback.Image, now)
                : Deployment.Queue(application.Id, serverId, request.ActorUserId, commit, request.PreviewId,
                    request.WebhookEventId, request.Trigger, request.ParentDeploymentId, now);
            deployment.RecordSource(application.GitSource.RepositoryUrl.Value, application.GitSource.Branch,
                commit ?? application.GitSource.CommitSha ?? "pending", null, now);
            deployment.AddLogLine("system", request.QueuedMessage, now);
            await dbContext.DeploymentRepository.AddAsync(deployment, cancellationToken);

            Deployment[] candidates = await ActiveAsync(item =>
                item.ApplicationId == application.Id && item.PreviewId == request.PreviewId &&
                item.ServerId == serverId, cancellationToken);
            foreach (Deployment old in SupersededDeploymentPolicy.Select(deployment, application.DeploymentSettings,
                         request.CancelPrevious, candidates))
            {
                var wasRunning = old.Status == DeploymentStatus.InProgress;
                old.Supersede(deployment.Id, commit, now);
                DeploymentLog.Append(dbContext, old, "stderr", old.CancellationReason!, now);
                superseded.Add(old);
                if (wasRunning) supersededBuilding.Add(old);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            dispatch.AddRange(await MarkEligibleAsync(server, cancellationToken));
        }

        // A cancelled build is stopped right away; when it ends it advances the queue, which starts the newer one.
        foreach (Deployment old in supersededBuilding)
            cancellations.Cancel(old.Id);
        foreach (Deployment old in superseded)
            await PublishStatusAsync(old, cancellationToken);
        await PublishStatusAsync(deployment, cancellationToken);
        await DispatchAsync(dispatch, deployment.Id, cancellationToken);

        return new DeploymentAdmission(deployment,
            dispatch.Any(item => item.Id == deployment.Id)
                ? DeploymentAdmissionOutcome.Dispatched
                : DeploymentAdmissionOutcome.Queued,
            superseded.Select(item => item.Id).ToArray());
    }

    /// <summary>
    ///     Starts the queued deployments of a server that can run now. Called after every terminal transition, by the
    ///     recurring queue maintenance job and at startup. When the lock is busy another advance is scheduled.
    /// </summary>
    public async Task AdvanceAsync(ServerId serverId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Deployment> dispatch;
        DistributedLockHandle? handle =
            await locks.TryAcquireAsync(LockKey(serverId), LockLease, LockWait, cancellationToken);
        if (handle is null)
        {
            TryScheduleAdvance(serverId);
            return;
        }

        await using (handle)
        {
            Server? server = dbContext.Servers.SingleOrDefault(item => item.Id == serverId);
            if (server is null) return;
            await dbContext.RefreshAsync(server, cancellationToken);
            dispatch = await MarkEligibleAsync(server, cancellationToken);
        }

        await DispatchAsync(dispatch, null, cancellationToken);
    }

    /// <summary>
    ///     Claims a deployment for the worker that holds its run lock: a dispatched (or legacy, directly enqueued)
    ///     queued deployment starts; a deployment already running without a live worker is reported orphaned.
    /// </summary>
    public async Task<DeploymentRunClaim> ClaimRunAsync(Deployment deployment,
        CancellationToken cancellationToken = default)
    {
        await using DistributedLockHandle handle = await AcquireAsync(deployment.ServerId, cancellationToken);
        await dbContext.RefreshAsync(deployment, cancellationToken);
        if (Deployment.IsTerminal(deployment.Status)) return DeploymentRunClaim.Terminal;
        if (deployment.Status is DeploymentStatus.InProgress or DeploymentStatus.CancelRequested)
            return DeploymentRunClaim.Orphaned;

        if (deployment.DispatchedAt is null)
        {
            // Enqueued directly (before queue admission existed): start only if a slot is free.
            Server? server = dbContext.Servers.SingleOrDefault(item => item.Id == deployment.ServerId);
            if (server is null) return DeploymentRunClaim.Waiting;
            Deployment[] active = await SlotHoldersAsync(deployment.ServerId, cancellationToken);
            if (!CanStart(deployment, server, active)) return DeploymentRunClaim.Waiting;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        deployment.Start(now);
        DeploymentLog.Append(dbContext, deployment, "system", "Deployment started.", now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return DeploymentRunClaim.Started;
    }

    /// <summary>
    ///     Marks the deploy phase (containers are about to be replaced). Serialized with superseding cancels, so a
    ///     deployment is either cancelled before this point or never auto-cancelled. False when it was cancelled.
    /// </summary>
    public async Task<bool> TryStartDeployPhaseAsync(Deployment deployment,
        CancellationToken cancellationToken = default)
    {
        await using DistributedLockHandle handle = await AcquireAsync(deployment.ServerId, cancellationToken);
        await dbContext.RefreshAsync(deployment, cancellationToken);
        if (!deployment.TryStartDeployPhase(timeProvider.GetUtcNow())) return false;
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Applies a state transition to a deployment with its current persisted state, under the queue lock.</summary>
    public async Task TransitionAsync(Deployment deployment, Action<Deployment, DateTimeOffset> transition,
        CancellationToken cancellationToken = default)
    {
        DistributedLockHandle? handle = await locks.TryAcquireAsync(LockKey(deployment.ServerId), LockLease, LockWait,
            cancellationToken);
        try
        {
            await dbContext.RefreshAsync(deployment, cancellationToken);
            transition(deployment, timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            if (handle is not null) await handle.DisposeAsync();
        }

        await PublishStatusAsync(deployment, cancellationToken);
    }

    /// <summary>
    ///     Requests cancellation of a deployment (user request). A queued deployment is cancelled at once; a running
    ///     one is signalled and stops at its next step. After its deploy phase started it finishes instead.
    /// </summary>
    public async Task RequestCancellationAsync(Deployment deployment, string reason,
        CancellationToken cancellationToken = default)
    {
        bool signal;
        await using (DistributedLockHandle handle = await AcquireAsync(deployment.ServerId, cancellationToken))
        {
            await dbContext.RefreshAsync(deployment, cancellationToken);
            DateTimeOffset now = timeProvider.GetUtcNow();
            if (Deployment.IsTerminal(deployment.Status))
                throw new DomainException("Deployment already finished.");
            signal = deployment.Status == DeploymentStatus.InProgress && deployment.DeployPhaseStartedAt is null;
            deployment.RequestCancellation(now, reason);
            DeploymentLog.Append(dbContext, deployment, "system",
                deployment.DeployPhaseStartedAt is null
                    ? reason
                    : reason + " Containers are already being replaced, so the deployment finishes first.", now);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        if (signal) cancellations.Cancel(deployment.Id);
        await PublishStatusAsync(deployment, cancellationToken);
        if (Deployment.IsTerminal(deployment.Status))
            await AdvanceAsync(deployment.ServerId, cancellationToken);
    }

    /// <summary>
    ///     Fails deployments no worker runs: active deployments without activity since <paramref name="inactiveSince" />
    ///     and, when <paramref name="orphanedBefore" /> is set (startup), every running deployment last active before
    ///     the control plane started. Then advances every server with waiting deployments. Returns the failed count.
    /// </summary>
    public async Task<int> RecoverAsync(DateTimeOffset inactiveSince, DateTimeOffset? orphanedBefore,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset cutoff = orphanedBefore is { } startup && startup > inactiveSince ? startup : inactiveSince;
        Deployment[] stale = dbContext.Deployments
            .Where(item => item.Status == DeploymentStatus.InProgress ||
                           item.Status == DeploymentStatus.CancelRequested ||
                           (item.Status == DeploymentStatus.Queued && item.DispatchedAt != null))
            .Where(item => (item.StartedAt ?? item.DispatchedAt ?? item.CreatedAt) < cutoff)
            .Where(item => !item.LogLines.Any(line => line.CreatedAt >= cutoff))
            .ToArray();

        var failed = 0;
        foreach (Deployment deployment in stale)
        {
            // Dispatched-but-unstarted deployments are dispatched again instead of failed (their job may be lost).
            if (deployment.Status == DeploymentStatus.Queued) continue;
            var reason = orphanedBefore.HasValue && cutoff == orphanedBefore
                ? "Deployment failed: the control plane restarted while it was running. Deploy again."
                : "Deployment failed: the worker that ran it stopped responding. Deploy again.";
            var changed = false;
            await TransitionAsync(deployment, (item, at) =>
            {
                if (!Deployment.IsActive(item.Status) || item.Status == DeploymentStatus.Queued) return;
                item.FailUnattended(at);
                DeploymentLog.Append(dbContext, item, "stderr", reason, at);
                changed = true;
            }, cancellationToken);
            if (changed) failed++;
        }

        ServerId[] servers = dbContext.Deployments
            .Where(item => item.Status == DeploymentStatus.Queued)
            .Select(item => item.ServerId)
            .Distinct()
            .ToArray();
        foreach (ServerId serverId in servers.Concat(stale.Select(item => item.ServerId)).Distinct())
            await AdvanceAsync(serverId, cancellationToken);
        return failed;
    }

    private async Task<Deployment?> FindDuplicateAsync(AppEntity application, ApplicationPreviewId? previewId,
        string? commit, CancellationToken cancellationToken)
    {
        Deployment[] active = await ActiveAsync(item =>
                item.ApplicationId == application.Id && item.PreviewId == previewId &&
                (item.Status == DeploymentStatus.Queued || item.Status == DeploymentStatus.InProgress),
            cancellationToken);
        return active
            .Where(item => item.Status is DeploymentStatus.Queued or DeploymentStatus.InProgress)
            .Where(item => !item.IsRollback)
            .FirstOrDefault(item => SameRef(item.CommitSha, commit ?? application.GitSource.CommitSha));
    }

    private static bool SameRef(string? left, string? right)
    {
        static string Normalize(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ||
                   string.Equals(value.Trim(), "pending", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value.Trim(), "HEAD", StringComparison.Ordinal)
                ? "HEAD"
                : value.Trim();
        }

        return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
    }

    private async Task<Deployment[]> ActiveAsync(System.Linq.Expressions.Expression<Func<Deployment, bool>> filter,
        CancellationToken cancellationToken)
    {
        Deployment[] items = dbContext.Deployments
            .Where(item => item.Status == DeploymentStatus.Queued || item.Status == DeploymentStatus.InProgress ||
                           item.Status == DeploymentStatus.CancelRequested)
            .Where(filter)
            .ToArray();
        // Tracked instances may be stale when another scope changed them; act on persisted state.
        foreach (Deployment item in items)
            await dbContext.RefreshAsync(item, cancellationToken);
        return items.Where(item => Deployment.IsActive(item.Status)).ToArray();
    }

    /// <summary>Deployments that hold a slot of the server: running ones and dispatched queued ones.</summary>
    private Task<Deployment[]> SlotHoldersAsync(ServerId serverId, CancellationToken cancellationToken)
    {
        return ActiveAsync(item => item.ServerId == serverId &&
                                   (item.Status != DeploymentStatus.Queued || item.DispatchedAt != null),
            cancellationToken);
    }

    private static bool CanStart(Deployment candidate, Server server, IReadOnlyCollection<Deployment> slotHolders)
    {
        // One active deployment per application and preview (production and each preview run independently).
        if (slotHolders.Any(item => item.Id != candidate.Id && item.ApplicationId == candidate.ApplicationId &&
                                    item.PreviewId == candidate.PreviewId))
            return false;
        return slotHolders.Count(item => item.Id != candidate.Id) < server.ConcurrentBuilds;
    }

    private async Task<IReadOnlyList<Deployment>> MarkEligibleAsync(Server server,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        List<Deployment> holders = [.. await SlotHoldersAsync(server.Id, cancellationToken)];
        var dispatch = new List<Deployment>();

        // Dispatched long ago but never started: the job was lost, dispatch it again (the run lock makes a duplicate
        // job harmless).
        dispatch.AddRange(holders.Where(item =>
            item.Status == DeploymentStatus.Queued && item.DispatchedAt < now - RedispatchAfter));

        Deployment[] waiting = (await ActiveAsync(item =>
                item.ServerId == server.Id && item.Status == DeploymentStatus.Queued && item.DispatchedAt == null,
            cancellationToken)).Where(item => item.DispatchedAt is null).OrderBy(item => item.CreatedAt).ToArray();
        foreach (Deployment candidate in waiting)
        {
            if (!CanStart(candidate, server, holders)) continue;
            candidate.MarkDispatched(now);
            holders.Add(candidate);
            dispatch.Add(candidate);
        }

        foreach (Deployment item in dispatch.Where(item => item.DispatchedAt < now - RedispatchAfter))
            item.MarkDispatched(now);
        if (dispatch.Count > 0) await dbContext.SaveChangesAsync(cancellationToken);
        return dispatch;
    }

    private async Task DispatchAsync(IReadOnlyList<Deployment> deployments, DeploymentId? admitted,
        CancellationToken cancellationToken)
    {
        Exception? admittedFailure = null;
        foreach (Deployment deployment in deployments)
            try
            {
                backgroundJobs.Enqueue<RunDeploymentJob>(job =>
                    job.RunAsync(deployment.Id.Value, CancellationToken.None));
            }
            catch (Exception ex)
            {
                // A deployment that cannot be dispatched is failed; leaving it would block the queue.
                await TransitionAsync(deployment, (item, at) =>
                {
                    item.FailUnattended(at);
                    DeploymentLog.Append(dbContext, item, "stderr",
                        $"Deployment could not be started: {ex.Message}", at);
                }, cancellationToken);
                if (deployment.Id == admitted) admittedFailure = ex;
            }

        if (admittedFailure is not null) throw admittedFailure;
    }

    private void TryScheduleAdvance(ServerId serverId)
    {
        try
        {
            backgroundJobs.Schedule<DeploymentQueueJob>(job => job.AdvanceAsync(serverId.Value, CancellationToken.None),
                TimeSpan.FromSeconds(15));
        }
        catch (BackgroundJobsUnavailableException)
        {
            // Without job storage nothing can run; the recurring maintenance job is unavailable too.
        }
    }

    private async Task<DistributedLockHandle> AcquireAsync(ServerId serverId, CancellationToken cancellationToken)
    {
        return await locks.TryAcquireAsync(LockKey(serverId), LockLease, LockWait, cancellationToken)
               ?? throw new DomainException("The deployment queue of this server is busy. Try again shortly.");
    }

    private Task PublishStatusAsync(Deployment deployment, CancellationToken cancellationToken)
    {
        var payload = new
        {
            deploymentId = deployment.Id.Value,
            applicationId = deployment.ApplicationId.Value,
            status = deployment.Status.ToString(),
            commitSha = deployment.CommitSha,
            finishedAt = deployment.FinishedAt
        };
        return Task.WhenAll(
            realtime.PublishAsync(new RealtimeGroup(RealtimeGroupKind.Deployment, deployment.Id.Value.ToString("D")),
                new RealtimeMessage("deployment.status", payload), cancellationToken),
            realtime.PublishAsync(
                new RealtimeGroup(RealtimeGroupKind.Application, deployment.ApplicationId.Value.ToString("D")),
                new RealtimeMessage("deployment.status", payload), cancellationToken));
    }
}
