using System.Text;
using Vessel.Domain.Common;
using Vessel.Domain.Scheduling;

namespace Vessel.Domain.ScheduledTasks;

public readonly record struct ScheduledTaskExecutionId(Guid Value) : IStronglyTypedId
{
    public static ScheduledTaskExecutionId New()
    {
        return new ScheduledTaskExecutionId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public enum ScheduledTaskExecutionStatus
{
    Running = 0,
    Succeeded = 1,
    Failed = 2,

    /// <summary>Not run: no running container, the previous run is still active, or the task is not allowed to run.</summary>
    Skipped = 3
}

/// <summary>
///     One run of a scheduled task. Output is stored redacted and bounded (<see cref="MaxOutputLength" />); it may still
///     contain application data, so it is shown only to users who can change the resource or read sensitive data.
/// </summary>
public sealed class ScheduledTaskExecution : Entity<ScheduledTaskExecutionId>
{
    public const int MaxOutputLength = 256 * 1024;

    public const int MaxMessageLength = 2000;

    public const string TruncationNotice = "\n[... output truncated ...]";

    private ScheduledTaskExecution()
    {
    }

    private ScheduledTaskExecution(ScheduledTaskExecutionId id, ScheduledTaskId taskId, TeamId teamId,
        ScheduledJobRunId? runId, ScheduledJobTrigger trigger, string? workerHost, DateTimeOffset now)
        : base(id, now)
    {
        ScheduledTaskId = taskId;
        TeamId = teamId;
        RunId = runId;
        Trigger = trigger;
        WorkerHost = workerHost is { Length: > ScheduledJobRun.MaxWorkerHostLength }
            ? workerHost[..ScheduledJobRun.MaxWorkerHostLength]
            : workerHost;
        Status = ScheduledTaskExecutionStatus.Running;
        StartedAt = now;
    }

    public ScheduledTaskId ScheduledTaskId { get; private set; }

    public TeamId TeamId { get; private set; }

    public ScheduledJobRunId? RunId { get; private set; }

    public ScheduledJobTrigger Trigger { get; private set; }

    public ScheduledTaskExecutionStatus Status { get; private set; }

    public string? ContainerName { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public double? DurationSeconds { get; private set; }

    public int? ExitCode { get; private set; }

    /// <summary>Redacted combined standard output and error.</summary>
    public string Output { get; private set; } = string.Empty;

    public bool OutputTruncated { get; private set; }

    /// <summary>Redacted outcome summary (failure or skip reason).</summary>
    public string? Message { get; private set; }

    public string? WorkerHost { get; private set; }

    public static ScheduledTaskExecution Start(ScheduledTaskId taskId, TeamId teamId, ScheduledJobRunId? runId,
        ScheduledJobTrigger trigger, string? workerHost, DateTimeOffset now)
    {
        return new ScheduledTaskExecution(ScheduledTaskExecutionId.New(), taskId, teamId, runId, trigger, workerHost,
            now);
    }

    public void UseContainer(string containerName)
    {
        ContainerName = DomainValidation.Required(containerName, nameof(ContainerName),
            ScheduledTask.MaxContainerNameLength);
    }

    /// <summary>Appends redacted output; returns false once the output limit was reached.</summary>
    public bool AppendOutput(string redactedText)
    {
        if (OutputTruncated) return false;
        var builder = new StringBuilder(Output);
        if (builder.Length > 0) builder.Append('\n');
        builder.Append(redactedText);
        if (builder.Length > MaxOutputLength)
        {
            builder.Length = MaxOutputLength;
            builder.Append(TruncationNotice);
            OutputTruncated = true;
        }

        Output = builder.ToString();
        return !OutputTruncated;
    }

    public void Succeed(int exitCode, DateTimeOffset now)
    {
        Finish(ScheduledTaskExecutionStatus.Succeeded, exitCode, null, now);
    }

    public void Fail(int? exitCode, string redactedMessage, DateTimeOffset now)
    {
        Finish(ScheduledTaskExecutionStatus.Failed, exitCode, redactedMessage, now);
    }

    public void Skip(string message, DateTimeOffset now)
    {
        Finish(ScheduledTaskExecutionStatus.Skipped, null, message, now);
    }

    private void Finish(ScheduledTaskExecutionStatus status, int? exitCode, string? message, DateTimeOffset now)
    {
        if (Status != ScheduledTaskExecutionStatus.Running) return;
        Status = status;
        ExitCode = exitCode;
        Message = message is { Length: > MaxMessageLength } ? message[..MaxMessageLength] : message;
        FinishedAt = now;
        DurationSeconds = Math.Round(Math.Max(0, (now - StartedAt).TotalSeconds), 2);
        Touch(now);
    }
}
