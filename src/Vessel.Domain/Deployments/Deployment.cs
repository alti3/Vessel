using Vessel.Domain.Common;

namespace Vessel.Domain.Deployments;

public sealed class Deployment : Entity<DeploymentId>
{
    private readonly List<DeploymentLogLine> _logLines = [];
    private int _lastLogSequence;

    private Deployment()
    {
    }

    private Deployment(
        DeploymentId id,
        ApplicationId applicationId,
        ServerId serverId,
        UserId? actorUserId,
        string? commitSha,
        ApplicationPreviewId? previewId,
        WebhookEventId? webhookEventId,
        bool isRollback,
        DeploymentTrigger trigger,
        DeploymentId? parentDeploymentId,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        Trigger = trigger;
        ParentDeploymentId = parentDeploymentId;
        ApplicationId = applicationId;
        ServerId = serverId;
        ActorUserId = actorUserId;
        CommitSha = commitSha;
        PreviewId = previewId;
        WebhookEventId = webhookEventId;
        IsRollback = isRollback;
        Status = DeploymentStatus.Queued;
    }

    public ApplicationId ApplicationId { get; private set; }

    public ServerId ServerId { get; private set; }

    public UserId? ActorUserId { get; private set; }

    public string? CommitSha { get; private set; }

    public string? CommitBranch { get; private set; }

    public string? CommitMessage { get; private set; }

    public string? RepositoryUrl { get; private set; }

    public ApplicationPreviewId? PreviewId { get; }

    public WebhookEventId? WebhookEventId { get; }

    public bool IsPreview => PreviewId.HasValue;

    public bool IsWebhookTriggered => WebhookEventId.HasValue;

    public string? ArtifactReference { get; private set; }

    public string? ConfigurationSnapshotReference { get; private set; }

    public DeploymentId? RollbackDeploymentId { get; private set; }

    public bool IsRollback { get; private set; }

    /// <summary>Image a rollback redeploys (a listed local image of the application); null for normal deployments.</summary>
    public string? RollbackImage { get; private set; }

    /// <summary>What queued the deployment; drives auto-cancel and audit semantics.</summary>
    public DeploymentTrigger Trigger { get; private set; }

    /// <summary>Deployment this one was derived from (a restart or redeploy of an earlier deployment).</summary>
    public DeploymentId? ParentDeploymentId { get; private set; }

    /// <summary>
    ///     Set when the queue admitted the deployment and handed it to a worker. A dispatched deployment holds a
    ///     concurrency slot of its server even before the worker starts it.
    /// </summary>
    public DateTimeOffset? DispatchedAt { get; private set; }

    /// <summary>
    ///     Set right before containers start being replaced (Coolify <c>deploy_started_at</c>). From then on the
    ///     deployment is never auto-cancelled, so a cancel cannot leave the application half deployed.
    /// </summary>
    public DateTimeOffset? DeployPhaseStartedAt { get; private set; }

    /// <summary>Why cancellation was requested (user request or the newer deployment that superseded this one).</summary>
    public string? CancellationReason { get; private set; }

    public DeploymentStatus Status { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public DateTimeOffset? CancellationRequestedAt { get; private set; }

    public IReadOnlyCollection<DeploymentLogLine> LogLines => _logLines.AsReadOnly();

    public static Deployment Queue(
        ApplicationId applicationId,
        ServerId serverId,
        UserId? actorUserId,
        string? commitSha,
        DateTimeOffset now)
    {
        return Queue(applicationId, serverId, actorUserId, commitSha, null, null, now);
    }

    public static Deployment Queue(
        ApplicationId applicationId,
        ServerId serverId,
        UserId? actorUserId,
        string? commitSha,
        ApplicationPreviewId? previewId,
        WebhookEventId? webhookEventId,
        DateTimeOffset now)
    {
        return Queue(applicationId, serverId, actorUserId, commitSha, previewId, webhookEventId,
            webhookEventId.HasValue ? DeploymentTrigger.Webhook : DeploymentTrigger.Manual, null, now);
    }

    public static Deployment Queue(
        ApplicationId applicationId,
        ServerId serverId,
        UserId? actorUserId,
        string? commitSha,
        ApplicationPreviewId? previewId,
        WebhookEventId? webhookEventId,
        DeploymentTrigger trigger,
        DeploymentId? parentDeploymentId,
        DateTimeOffset now)
    {
        if (trigger == DeploymentTrigger.Rollback)
            throw new DomainException("Rollback deployments are queued with QueueRollback.");
        if (trigger == DeploymentTrigger.Webhook && !webhookEventId.HasValue)
            throw new DomainException("A webhook deployment requires its webhook event.");
        return new Deployment(DeploymentId.New(), applicationId, serverId, actorUserId, commitSha, previewId,
            webhookEventId, false, trigger, parentDeploymentId, now);
    }

    /// <summary>
    ///     Queues a rollback to an earlier successful production deployment (<paramref name="target" />) or a listed
    ///     local image. Rollbacks never target previews and are never auto-cancelled.
    /// </summary>
    public static Deployment QueueRollback(
        ApplicationId applicationId,
        ServerId serverId,
        UserId? actorUserId,
        string commitSha,
        DeploymentId? target,
        string? image,
        DateTimeOffset now)
    {
        var deployment = new Deployment(DeploymentId.New(), applicationId, serverId, actorUserId,
            DomainValidation.Required(commitSha, nameof(commitSha), 80), null, null, true,
            DeploymentTrigger.Rollback, target, now)
        {
            RollbackDeploymentId = target,
            RollbackImage = DomainValidation.Optional(image, nameof(image), 512)
        };
        return deployment;
    }

    public static bool IsActive(DeploymentStatus status)
    {
        return status is DeploymentStatus.Queued or DeploymentStatus.InProgress or DeploymentStatus.CancelRequested;
    }

    /// <summary>Marks a queued deployment as handed to a worker (it now holds a concurrency slot).</summary>
    public void MarkDispatched(DateTimeOffset now)
    {
        if (Status != DeploymentStatus.Queued)
            throw new DomainException($"Only queued deployments can be dispatched (status {Status}).");
        DispatchedAt = now;
        Touch(now);
    }

    /// <summary>
    ///     Marks the start of the deploy phase (containers are about to be replaced). Returns false when the deployment
    ///     is no longer running (for example it was cancelled), in which case containers must not be touched.
    /// </summary>
    public bool TryStartDeployPhase(DateTimeOffset now)
    {
        if (Status != DeploymentStatus.InProgress) return false;
        DeployPhaseStartedAt ??= now;
        Touch(now);
        return true;
    }

    /// <summary>
    ///     True when a newer deployment may cancel this one: queued deployments always, running ones only with
    ///     <paramref name="includeBuilding" /> and only before the deploy phase started.
    /// </summary>
    public bool CanBeSuperseded(bool includeBuilding)
    {
        if (DeployPhaseStartedAt.HasValue) return false;
        // A dispatched deployment already holds a build slot, so it counts as building (Coolify marks it in progress).
        if (Status == DeploymentStatus.Queued && DispatchedAt is null) return true;
        return includeBuilding && Status is DeploymentStatus.Queued or DeploymentStatus.InProgress;
    }

    /// <summary>Cancels this deployment because <paramref name="replacement" /> replaces it.</summary>
    public void Supersede(DeploymentId replacement, string? replacementCommit, DateTimeOffset now)
    {
        if (!CanBeSuperseded(true))
            throw new DomainException("Deployment can no longer be superseded.");
        RequestCancellation(now,
            $"Deployment cancelled: the newer deployment {replacement.Value:D}" +
            (string.IsNullOrWhiteSpace(replacementCommit) ? string.Empty : $" (commit {replacementCommit})") +
            " replaces it.");
    }

    /// <summary>Fails a deployment that no worker runs (undispatchable, orphaned by a restart, or stuck).</summary>
    public void FailUnattended(DateTimeOffset now)
    {
        if (IsTerminal(Status)) return;
        TransitionTo(DeploymentStatus.Failed, now);
    }

    /// <summary>
    ///     Records the highest log sequence already persisted (other writers may have appended lines), so the next
    ///     line never reuses a sequence number.
    /// </summary>
    public void ObserveLogSequence(int lastPersistedSequence)
    {
        if (lastPersistedSequence > _lastLogSequence) _lastLogSequence = lastPersistedSequence;
    }

    public void Start(DateTimeOffset now)
    {
        TransitionTo(DeploymentStatus.InProgress, now);
    }

    public void RecordSource(string repositoryUrl, string branch, string commitSha, string? commitMessage,
        DateTimeOffset now)
    {
        RepositoryUrl = DomainValidation.Required(repositoryUrl, nameof(repositoryUrl), 2048);
        CommitBranch = DomainValidation.Required(branch, nameof(branch), 255);
        CommitSha = DomainValidation.Required(commitSha, nameof(commitSha), 80);
        CommitMessage = DomainValidation.Optional(commitMessage, nameof(commitMessage), 512);
        Touch(now);
    }

    public void RecordConfigurationSnapshot(string snapshotReference, DateTimeOffset now)
    {
        ConfigurationSnapshotReference = DomainValidation.Required(snapshotReference, nameof(snapshotReference), 512);
        Touch(now);
    }

    public void RequestCancellation(DateTimeOffset now, string? reason = null)
    {
        if (IsTerminal(Status) || Status == DeploymentStatus.CancelRequested) return;
        CancellationRequestedAt = now;
        CancellationReason = DomainValidation.Optional(reason, nameof(reason), 512);
        TransitionTo(
            Status == DeploymentStatus.Queued ? DeploymentStatus.CanceledByUser : DeploymentStatus.CancelRequested,
            now);
    }

    public void MarkSucceeded(string? artifactReference, DateTimeOffset now)
    {
        ArtifactReference = artifactReference;
        TransitionTo(DeploymentStatus.Succeeded, now);
    }

    public void MarkFailed(DateTimeOffset now)
    {
        TransitionTo(DeploymentStatus.Failed, now);
    }

    public void CancelByUser(DateTimeOffset now)
    {
        TransitionTo(DeploymentStatus.CanceledByUser, now);
    }

    public void AddLogLine(string stream, string message, DateTimeOffset now)
    {
        // Appending a line inserts a log row only; the deployment row (and its concurrency stamp) is unchanged, so
        // several writers (runner, queue, user cancel) can append without conflicting.
        var sequence = Math.Max(_lastLogSequence, _logLines.Count == 0 ? 0 : _logLines.Max(line => line.Sequence)) + 1;
        _lastLogSequence = sequence;
        _logLines.Add(new DeploymentLogLine(Id, sequence, stream.Trim(), message, now));
    }

    public void PruneLogLines(DateTimeOffset olderThan)
    {
        _logLines.RemoveAll(line => line.CreatedAt < olderThan);
    }

    private void TransitionTo(DeploymentStatus nextStatus, DateTimeOffset now)
    {
        if (!CanTransition(Status, nextStatus))
            throw new DomainException($"Cannot transition deployment from {Status} to {nextStatus}.");

        DeploymentStatus previous = Status;
        Status = nextStatus;
        StartedAt ??= nextStatus == DeploymentStatus.InProgress ? now : null;
        FinishedAt = IsTerminal(nextStatus) ? now : FinishedAt;
        Touch(now);
        AddDomainEvent(new DeploymentStatusChangedEvent(Id, previous, nextStatus, now));
    }

    public static bool CanTransition(DeploymentStatus currentStatus, DeploymentStatus nextStatus)
    {
        return currentStatus switch
        {
            // Queued -> Failed: undispatchable or orphaned queued deployments are failed by the queue.
            DeploymentStatus.Queued => nextStatus is DeploymentStatus.InProgress or DeploymentStatus.CanceledByUser
                or DeploymentStatus.Failed,
            DeploymentStatus.InProgress => nextStatus is DeploymentStatus.Succeeded or DeploymentStatus.Failed
                or DeploymentStatus.CanceledByUser or DeploymentStatus.CancelRequested,
            // CancelRequested -> Succeeded: the request arrived after containers started being replaced, so the
            // deployment finished instead of leaving a half-replaced stack.
            DeploymentStatus.CancelRequested =>
                nextStatus is DeploymentStatus.CanceledByUser or DeploymentStatus.Failed or DeploymentStatus.Succeeded,
            _ => false
        };
    }

    public static bool IsTerminal(DeploymentStatus status)
    {
        return status is DeploymentStatus.Succeeded or DeploymentStatus.Failed or DeploymentStatus.CanceledByUser;
    }
}
