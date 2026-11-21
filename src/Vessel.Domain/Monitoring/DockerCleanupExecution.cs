using Vessel.Domain.Common;

namespace Vessel.Domain.Monitoring;

public enum DockerCleanupTrigger
{
    Scheduled = 0,
    DiskThreshold = 1,
    Manual = 2
}

public enum DockerCleanupStatus
{
    Running = 0,
    Succeeded = 1,
    Failed = 2,
    Skipped = 3
}

/// <summary>
///     One Docker cleanup run on a server (SRV-07, upstream <c>DockerCleanupExecution</c>): status, timing, reclaimed
///     space, what was removed, and a redacted, size-capped log.
/// </summary>
public sealed class DockerCleanupExecution : Entity<DockerCleanupExecutionId>
{
    public const int MaxLogLength = 32 * 1024;
    public const int MaxMessageLength = 1000;

    /// <summary>A run that has not finished after this long belongs to a lost worker and is marked failed.</summary>
    public static readonly TimeSpan InterruptedAfter = TimeSpan.FromMinutes(30);

    private DockerCleanupExecution()
    {
    }

    private DockerCleanupExecution(DockerCleanupExecutionId id, ServerId serverId, DockerCleanupTrigger trigger,
        UserId? requestedBy, DateTimeOffset now) : base(id, now)
    {
        ServerId = serverId;
        Trigger = trigger;
        RequestedByUserId = requestedBy;
        StartedAt = now;
        Status = DockerCleanupStatus.Running;
    }

    public ServerId ServerId { get; private set; }

    public DockerCleanupTrigger Trigger { get; private set; }

    public UserId? RequestedByUserId { get; private set; }

    public DockerCleanupStatus Status { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public long ReclaimedBytes { get; private set; }

    public int RemovedContainers { get; private set; }

    public int RemovedImages { get; private set; }

    public bool BuildCachePruned { get; private set; }

    /// <summary>Redacted step log (one line per step).</summary>
    public string Log { get; private set; } = string.Empty;

    /// <summary>Safe failure or skip reason (never raw command output).</summary>
    public string? Message { get; private set; }

    public static DockerCleanupExecution Start(ServerId serverId, DockerCleanupTrigger trigger, UserId? requestedBy,
        DateTimeOffset now)
    {
        return new DockerCleanupExecution(DockerCleanupExecutionId.New(), serverId, trigger, requestedBy, now);
    }

    public void AppendLog(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        var next = Log.Length == 0 ? line : Log + "\n" + line;
        Log = next.Length <= MaxLogLength ? next : next[..MaxLogLength];
    }

    public void RecordRemoval(int containers, int images, long reclaimedBytes, bool buildCachePruned)
    {
        RemovedContainers += Math.Max(0, containers);
        RemovedImages += Math.Max(0, images);
        ReclaimedBytes += Math.Max(0L, reclaimedBytes);
        BuildCachePruned |= buildCachePruned;
    }

    public void Succeed(DateTimeOffset now)
    {
        Finish(DockerCleanupStatus.Succeeded, null, now);
    }

    public void Fail(string message, DateTimeOffset now)
    {
        Finish(DockerCleanupStatus.Failed, message, now);
    }

    public void Skip(string reason, DateTimeOffset now)
    {
        Finish(DockerCleanupStatus.Skipped, reason, now);
    }

    private void Finish(DockerCleanupStatus status, string? message, DateTimeOffset now)
    {
        if (Status != DockerCleanupStatus.Running)
            throw new DomainException("The Docker cleanup execution already finished.");
        Status = status;
        Message = message is null ? null : message.Length <= MaxMessageLength ? message : message[..MaxMessageLength];
        FinishedAt = now;
        Touch(now);
    }
}
