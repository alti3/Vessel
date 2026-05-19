using Vessel.Domain.Common;

namespace Vessel.Domain.Deployments;

public sealed class Deployment : Entity<DeploymentId>
{
    private readonly List<DeploymentLogLine> _logLines = [];

    private Deployment()
    {
    }

    private Deployment(
        DeploymentId id,
        ApplicationId applicationId,
        ServerId serverId,
        UserId? actorUserId,
        string? commitSha,
        bool isRollback,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        ApplicationId = applicationId;
        ServerId = serverId;
        ActorUserId = actorUserId;
        CommitSha = commitSha;
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

    public string? ArtifactReference { get; private set; }

    public string? ConfigurationSnapshotReference { get; private set; }

    public DeploymentId? RollbackDeploymentId { get; private set; }

    public bool IsRollback { get; private set; }

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
        return new Deployment(DeploymentId.New(), applicationId, serverId, actorUserId, commitSha, false, now);
    }

    public void Start(DateTimeOffset now)
    {
        TransitionTo(DeploymentStatus.InProgress, now);
    }

    public void RecordSource(string repositoryUrl, string branch, string commitSha, string? commitMessage, DateTimeOffset now)
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

    public void RequestCancellation(DateTimeOffset now)
    {
        if (IsTerminal(Status)) return;
        CancellationRequestedAt = now;
        TransitionTo(Status == DeploymentStatus.Queued ? DeploymentStatus.CanceledByUser : DeploymentStatus.CancelRequested, now);
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
        _logLines.Add(new DeploymentLogLine(Id, _logLines.Count + 1, stream.Trim(), message, now));
        Touch(now);
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
            DeploymentStatus.Queued => nextStatus is DeploymentStatus.InProgress or DeploymentStatus.CanceledByUser,
            DeploymentStatus.InProgress => nextStatus is DeploymentStatus.Succeeded or DeploymentStatus.Failed
                or DeploymentStatus.CanceledByUser or DeploymentStatus.CancelRequested,
            DeploymentStatus.CancelRequested => nextStatus is DeploymentStatus.CanceledByUser or DeploymentStatus.Failed,
            _ => false
        };
    }

    public static bool IsTerminal(DeploymentStatus status)
    {
        return status is DeploymentStatus.Succeeded or DeploymentStatus.Failed or DeploymentStatus.CanceledByUser;
    }
}
