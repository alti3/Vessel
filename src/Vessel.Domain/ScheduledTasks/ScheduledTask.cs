using Vessel.Domain.Common;
using Vessel.Domain.Scheduling;
using Vessel.Domain.ValueObjects;

namespace Vessel.Domain.ScheduledTasks;

public readonly record struct ScheduledTaskId(Guid Value) : IStronglyTypedId
{
    public static ScheduledTaskId New()
    {
        return new ScheduledTaskId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

/// <summary>The resource whose container runs a scheduled task.</summary>
public enum ScheduledTaskOwnerKind
{
    Application = 0,
    Database = 1,
    Service = 2
}

/// <summary>
///     A user-defined cron command executed inside a container of an application, database or service (NTF-07,
///     upstream <c>ScheduledTask</c>). With <see cref="UseShell" /> the command runs as <c>sh -c &lt;command&gt;</c>;
///     otherwise it is split into arguments like a POSIX shell would (without expansion) and executed directly, which
///     also works in images without a shell. Failure tracking disables the task automatically after
///     <see cref="AutoDisableAfterFailures" /> consecutive failures once the first failure of the streak is at least
///     <see cref="AutoDisableAfterHours" /> hours old, so a short outage never disables a frequent task.
/// </summary>
public sealed class ScheduledTask : Entity<ScheduledTaskId>
{
    public const int AutoDisableAfterFailures = 10;

    public const int AutoDisableAfterHours = 24;

    public const int MaxNameLength = 120;

    public const int MaxCommandLength = 8000;

    public const int MaxContainerNameLength = 255;

    public const int DefaultTimeoutSeconds = 300;

    public const int MinTimeoutSeconds = 10;

    public const int MaxTimeoutSeconds = 36000;

    public const int MaxDisabledReasonLength = 500;

    private ScheduledTask()
    {
    }

    private ScheduledTask(ScheduledTaskId id, TeamId teamId, ScheduledTaskOwnerKind ownerKind, Guid ownerId,
        DateTimeOffset now)
        : base(id, now)
    {
        TeamId = teamId;
        OwnerKind = ownerKind;
        OwnerId = ownerId;
    }

    public TeamId TeamId { get; private set; }

    public ScheduledTaskOwnerKind OwnerKind { get; private set; }

    /// <summary>The application, database or service identifier (by <see cref="OwnerKind" />).</summary>
    public Guid OwnerId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string Command { get; private set; } = string.Empty;

    public string CronExpression { get; private set; } = string.Empty;

    /// <summary>IANA time zone the cron expression is evaluated in; null uses the instance default.</summary>
    public string? TimeZone { get; private set; }

    /// <summary>
    ///     Container to run in. Required when the resource runs several containers (compose applications, services); for
    ///     single-container resources it may be left empty.
    /// </summary>
    public string? ContainerName { get; private set; }

    public int TimeoutSeconds { get; private set; } = DefaultTimeoutSeconds;

    public bool UseShell { get; private set; } = true;

    public bool Enabled { get; private set; } = true;

    /// <summary>Catch-up policy after downtime; null uses the instance default.</summary>
    public MissedRunPolicy? MissedRunPolicy { get; private set; }

    /// <summary>Failed runs since the last successful run.</summary>
    public int ConsecutiveFailures { get; private set; }

    /// <summary>First failure since the last successful run.</summary>
    public DateTimeOffset? FailingSince { get; private set; }

    /// <summary>Why Vessel disabled the task automatically; cleared when it is enabled again.</summary>
    public string? DisabledReason { get; private set; }

    public DateTimeOffset? LastRunAt { get; private set; }

    public static ScheduledTask Create(TeamId teamId, ScheduledTaskOwnerKind ownerKind, Guid ownerId, string name,
        string command, string cronExpression, string? timeZone, string? containerName, int timeoutSeconds,
        bool useShell, bool enabled, MissedRunPolicy? missedRunPolicy, DateTimeOffset now)
    {
        if (ownerId == Guid.Empty) throw new DomainException("A scheduled task needs its resource.");
        var task = new ScheduledTask(ScheduledTaskId.New(), teamId, ownerKind, ownerId, now);
        task.Configure(name, command, cronExpression, timeZone, containerName, timeoutSeconds, useShell,
            missedRunPolicy, now);
        task.Enabled = enabled;
        return task;
    }

    public void Configure(string name, string command, string cronExpression, string? timeZone,
        string? containerName, int timeoutSeconds, bool useShell, MissedRunPolicy? missedRunPolicy,
        DateTimeOffset now)
    {
        Name = DomainValidation.Required(name, nameof(Name), MaxNameLength);
        var trimmedCommand = command?.Trim() ?? string.Empty;
        if (trimmedCommand.Length == 0) throw new DomainException("Scheduled task command is required.");
        if (trimmedCommand.Length > MaxCommandLength)
            throw new DomainException($"Scheduled task command must be at most {MaxCommandLength} characters.");
        if (trimmedCommand.Contains('\0', StringComparison.Ordinal))
            throw new DomainException("Scheduled task command must not contain NUL characters.");
        if (!useShell && ScheduledTaskCommandLine.Split(trimmedCommand).Count == 0)
            throw new DomainException("Scheduled task command is empty.");
        Command = trimmedCommand;
        CronExpression = CronSchedule.Parse(cronExpression).Expression;
        TimeZone = string.IsNullOrWhiteSpace(timeZone) ? null : timeZone.Trim();
        if (TimeZone is not null) CronSchedule.ResolveTimeZone(TimeZone);
        ContainerName = NormalizeContainer(containerName);
        TimeoutSeconds = timeoutSeconds is >= MinTimeoutSeconds and <= MaxTimeoutSeconds
            ? timeoutSeconds
            : throw new DomainException(
                $"Scheduled task timeout must be between {MinTimeoutSeconds} and {MaxTimeoutSeconds} seconds.");
        UseShell = useShell;
        MissedRunPolicy = missedRunPolicy;
        Touch(now);
    }

    /// <summary>Enables the task. A task that is enabled again starts a new failure streak (upstream).</summary>
    public void Enable(DateTimeOffset now)
    {
        if (Enabled) return;
        Enabled = true;
        ConsecutiveFailures = 0;
        FailingSince = null;
        DisabledReason = null;
        Touch(now);
    }

    public void Disable(DateTimeOffset now)
    {
        if (!Enabled) return;
        Enabled = false;
        Touch(now);
    }

    public void RecordRun(DateTimeOffset now)
    {
        LastRunAt = now;
        Touch(now);
    }

    /// <summary>Counts a failed run and disables the task when it failed too often for too long.</summary>
    /// <returns><see langword="true" /> when this failure disabled the task.</returns>
    public bool RecordFailure(DateTimeOffset now)
    {
        ConsecutiveFailures++;
        FailingSince ??= now;
        var disable = Enabled && ConsecutiveFailures >= AutoDisableAfterFailures &&
                      FailingSince.Value <= now.AddHours(-AutoDisableAfterHours);
        if (disable)
        {
            Enabled = false;
            DisabledReason = $"Disabled automatically after {ConsecutiveFailures} failed runs in a row since " +
                             $"{FailingSince.Value.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC.";
        }

        Touch(now);
        return disable;
    }

    /// <summary>Ends the failure streak after a successful run.</summary>
    /// <returns><see langword="true" /> when the task was failing (send one "works again" notice).</returns>
    public bool RecordSuccess(DateTimeOffset now)
    {
        var wasFailing = ConsecutiveFailures > 0 || FailingSince is not null;
        ConsecutiveFailures = 0;
        FailingSince = null;
        Touch(now);
        return wasFailing;
    }

    private static string? NormalizeContainer(string? containerName)
    {
        if (string.IsNullOrWhiteSpace(containerName)) return null;
        var value = containerName.Trim().TrimStart('/');
        if (value.Length > MaxContainerNameLength)
            throw new DomainException($"Container name must be at most {MaxContainerNameLength} characters.");
        // Docker container names: [a-zA-Z0-9][a-zA-Z0-9_.-]*; never an option.
        if (!char.IsAsciiLetterOrDigit(value[0]) ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '.' or '-')))
            throw new DomainException("Container name may only contain letters, digits, '_', '.' and '-'.");
        return value;
    }
}
