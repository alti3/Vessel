using Vessel.Domain.ScheduledTasks;
using Vessel.Domain.Scheduling;

namespace Vessel.Application.ScheduledTasks;

/// <summary>Creates a scheduled task on a resource.</summary>
/// <param name="Name">Display name.</param>
/// <param name="Command">Command to run inside the container.</param>
/// <param name="CronExpression">Cron frequency (five fields, a macro such as <c>@daily</c>, or <c>every_minute</c>).</param>
/// <param name="ContainerName">Container (or compose service) to run in; required for multi-container resources.</param>
/// <param name="TimeoutSeconds">Run deadline (default 300 seconds).</param>
/// <param name="UseShell">Run as <c>sh -c</c> (default) or split into arguments and run directly.</param>
/// <param name="Enabled">Whether the task is scheduled.</param>
/// <param name="TimeZone">IANA time zone of the cron expression; null uses the instance default.</param>
/// <param name="MissedRunPolicy">Catch-up policy after downtime; null uses the instance default.</param>
public sealed record CreateScheduledTaskRequest(
    string Name,
    string Command,
    string CronExpression,
    string? ContainerName = null,
    int? TimeoutSeconds = null,
    bool UseShell = true,
    bool Enabled = true,
    string? TimeZone = null,
    MissedRunPolicy? MissedRunPolicy = null);

/// <summary>Partial update; null fields keep their value. Use the <c>Clear*</c> flags to reset optional fields.</summary>
public sealed record UpdateScheduledTaskRequest(
    string? Name = null,
    string? Command = null,
    string? CronExpression = null,
    string? ContainerName = null,
    int? TimeoutSeconds = null,
    bool? UseShell = null,
    bool? Enabled = null,
    string? TimeZone = null,
    MissedRunPolicy? MissedRunPolicy = null,
    bool ClearContainerName = false,
    bool ClearTimeZone = false,
    bool ClearMissedRunPolicy = false);

public sealed record ScheduledTaskSummary(
    Guid Id,
    ScheduledTaskOwnerKind OwnerKind,
    Guid OwnerId,
    string Name,
    string Command,
    string CronExpression,
    string? TimeZone,
    string? ContainerName,
    int TimeoutSeconds,
    bool UseShell,
    bool Enabled,
    MissedRunPolicy? MissedRunPolicy,
    int ConsecutiveFailures,
    DateTimeOffset? FailingSince,
    string? DisabledReason,
    DateTimeOffset? LastRunAt,
    DateTimeOffset? NextRunAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
///     One run. <see cref="Output" /> and <see cref="Message" /> are returned only to callers who can change the
///     resource or read sensitive data (<see cref="OutputHidden" /> is set otherwise). Duration is a JSON number.
/// </summary>
public sealed record ScheduledTaskExecutionSummary(
    Guid Id,
    Guid TaskId,
    Guid? RunId,
    ScheduledJobTrigger Trigger,
    ScheduledTaskExecutionStatus Status,
    string? ContainerName,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    double? DurationSeconds,
    int? ExitCode,
    string? Output,
    bool OutputTruncated,
    string? Message,
    string? WorkerHost,
    bool OutputHidden);

public sealed record PagedScheduledTaskExecutions(
    IReadOnlyList<ScheduledTaskExecutionSummary> Items,
    int Page,
    int PageSize,
    int Total);

/// <summary>A queued "run now" request.</summary>
public sealed record ScheduledTaskRunQueued(Guid TaskId, Guid RunId);
