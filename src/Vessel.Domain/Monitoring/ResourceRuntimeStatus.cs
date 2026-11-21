using Vessel.Domain.Common;

namespace Vessel.Domain.Monitoring;

public enum ResourceRuntimeKind
{
    Application = 0,
    Database = 1,
    Service = 2
}

/// <summary>Runtime state of a resource, aggregated from its containers (upstream Coolify status strings).</summary>
public enum ResourceRuntimeState
{
    Unknown = 0,
    Running = 1,
    Starting = 2,
    Restarting = 3,
    Degraded = 4,
    Exited = 5,
    Paused = 6
}

public enum ResourceHealth
{
    Unknown = 0,
    Healthy = 1,
    Unhealthy = 2,
    Starting = 3
}

/// <summary>One container as the runtime reports it (<c>State.Status</c>, <c>State.Health.Status</c>, <c>RestartCount</c>).</summary>
public sealed record ContainerObservation(string State, string? Health, int RestartCount);

public sealed record AggregatedRuntimeStatus(ResourceRuntimeState State, ResourceHealth Health);

/// <summary>
///     Aggregates container states into one resource state with upstream Coolify's priority rules
///     (<c>ContainerStatusAggregator::resolveStatus</c>): restarting, then mixed running/exited (degraded), then
///     running with its health, then dead (degraded), paused, starting, and finally exited.
/// </summary>
public static class ContainerStatusAggregator
{
    /// <param name="containers">Containers of one resource.</param>
    /// <param name="preserveRestarting">
    ///     Keep "restarting" for a single resource (an application or database); a multi-container service stack reports
    ///     a restarting member as degraded.
    /// </param>
    public static AggregatedRuntimeStatus Aggregate(IReadOnlyCollection<ContainerObservation> containers,
        bool preserveRestarting = true)
    {
        if (containers.Count == 0) return new AggregatedRuntimeStatus(ResourceRuntimeState.Exited, ResourceHealth.Unknown);

        bool running = false, restarting = false, unhealthy = false, unknown = false, healthStarting = false;
        bool exited = false, starting = false, paused = false, dead = false;
        foreach (ContainerObservation container in containers)
        {
            var state = (container.State ?? string.Empty).Trim().ToLowerInvariant();
            var health = (container.Health ?? string.Empty).Trim().ToLowerInvariant();
            switch (state)
            {
                case "restarting":
                    restarting = true;
                    break;
                case "running":
                    running = true;
                    if (health == "unhealthy") unhealthy = true;
                    else if (health == "starting") healthStarting = true;
                    else if (health != "healthy") unknown = true;
                    break;
                case "exited":
                    exited = true;
                    break;
                case "created":
                case "starting":
                    starting = true;
                    break;
                case "paused":
                    paused = true;
                    break;
                case "dead":
                case "removing":
                    dead = true;
                    break;
                default:
                    exited = true;
                    break;
            }
        }

        if (restarting)
            return preserveRestarting
                ? new AggregatedRuntimeStatus(ResourceRuntimeState.Restarting, ResourceHealth.Unknown)
                : new AggregatedRuntimeStatus(ResourceRuntimeState.Degraded, ResourceHealth.Unhealthy);
        if (running && exited) return new AggregatedRuntimeStatus(ResourceRuntimeState.Degraded, ResourceHealth.Unhealthy);
        if (running && starting) return new AggregatedRuntimeStatus(ResourceRuntimeState.Starting, ResourceHealth.Unknown);
        if (running)
            return new AggregatedRuntimeStatus(ResourceRuntimeState.Running,
                unhealthy ? ResourceHealth.Unhealthy
                : healthStarting ? ResourceHealth.Starting
                : unknown ? ResourceHealth.Unknown
                : ResourceHealth.Healthy);
        if (dead) return new AggregatedRuntimeStatus(ResourceRuntimeState.Degraded, ResourceHealth.Unhealthy);
        if (paused) return new AggregatedRuntimeStatus(ResourceRuntimeState.Paused, ResourceHealth.Unknown);
        if (starting) return new AggregatedRuntimeStatus(ResourceRuntimeState.Starting, ResourceHealth.Unknown);
        return new AggregatedRuntimeStatus(ResourceRuntimeState.Exited, ResourceHealth.Unknown);
    }
}

/// <summary>What one reconciliation changed for a resource.</summary>
public sealed record ResourceRuntimeObservation(
    ResourceRuntimeState PreviousState,
    ResourceRuntimeState State,
    ResourceHealth Health,
    bool StateChanged,
    bool StoppedUnexpectedly,
    bool RestartLimitReachedNow);

/// <summary>
///     Observed runtime status of one application, database or service (SRV-04), reconciled from the real runtime.
///     Kept in its own table so the frequent status writes never touch (or conflict with) the resource's configuration
///     row. Restart limits are opt-in (<see cref="MaxRestartCount" /> 0 disables them) and follow upstream Coolify's
///     <c>HasRestartLimit</c> / <c>RestartCountTracker</c> semantics.
/// </summary>
public sealed class ResourceRuntimeStatus : Entity<ResourceRuntimeStatusId>
{
    public const int MaxAllowedRestartLimit = 1000;

    /// <summary>A stop Vessel starts is expected for this long (upstream: 300 s plus the stop timeout).</summary>
    public static readonly TimeSpan ExpectedStopWindow = TimeSpan.FromSeconds(330);

    private ResourceRuntimeStatus()
    {
    }

    private ResourceRuntimeStatus(ResourceRuntimeStatusId id, ResourceRuntimeKind kind, Guid resourceId,
        TeamId teamId, ServerId serverId, DateTimeOffset now) : base(id, now)
    {
        Kind = kind;
        ResourceId = resourceId;
        TeamId = teamId;
        ServerId = serverId;
        StateChangedAt = now;
    }

    public ResourceRuntimeKind Kind { get; private set; }

    public Guid ResourceId { get; private set; }

    public TeamId TeamId { get; private set; }

    public ServerId ServerId { get; private set; }

    public ResourceRuntimeState State { get; private set; } = ResourceRuntimeState.Unknown;

    public ResourceHealth Health { get; private set; } = ResourceHealth.Unknown;

    public int ContainerCount { get; private set; }

    public int RunningContainerCount { get; private set; }

    public int RestartCount { get; private set; }

    /// <summary>Opt-in restart limit; 0 (the default) disables it.</summary>
    public int MaxRestartCount { get; private set; }

    public bool RestartLimitReached { get; private set; }

    public DateTimeOffset? LastRestartAt { get; private set; }

    public DateTimeOffset StateChangedAt { get; private set; }

    public DateTimeOffset? LastCheckedAt { get; private set; }

    public DateTimeOffset? LastSeenRunningAt { get; private set; }

    /// <summary>Until this time a stop is expected (Vessel stopped it) and sends no "stopped" notification.</summary>
    public DateTimeOffset? ExpectedStopUntil { get; private set; }

    public bool IsUp => State is ResourceRuntimeState.Running or ResourceRuntimeState.Starting
        or ResourceRuntimeState.Restarting or ResourceRuntimeState.Degraded;

    public static ResourceRuntimeStatus Create(ResourceRuntimeKind kind, Guid resourceId, TeamId teamId,
        ServerId serverId, DateTimeOffset now)
    {
        if (resourceId == Guid.Empty) throw new DomainException("Resource id is required.");
        return new ResourceRuntimeStatus(ResourceRuntimeStatusId.New(), kind, resourceId, teamId, serverId, now);
    }

    public bool IsStopExpected(DateTimeOffset now)
    {
        return ExpectedStopUntil is { } until && until > now;
    }

    /// <summary>Call before Vessel stops or recreates the resource's containers.</summary>
    public void ExpectStop(DateTimeOffset now, TimeSpan? window = null)
    {
        ExpectedStopUntil = now + (window ?? ExpectedStopWindow);
        Touch(now);
    }

    public void ConfigureRestartLimit(int maxRestartCount, DateTimeOffset now)
    {
        if (maxRestartCount is < 0 or > MaxAllowedRestartLimit)
            throw new DomainException($"Restart limit must be between 0 (disabled) and {MaxAllowedRestartLimit}.");
        MaxRestartCount = maxRestartCount;
        if (maxRestartCount == 0 || RestartCount < maxRestartCount) RestartLimitReached = false;
        Touch(now);
    }

    public void ResetRestartLimit(DateTimeOffset now)
    {
        RestartCount = 0;
        RestartLimitReached = false;
        LastRestartAt = null;
        Touch(now);
    }

    /// <summary>
    ///     Records one observation. <paramref name="observedRestartCount" /> is the highest <c>RestartCount</c> of the
    ///     resource's current containers. The restart limit is reached once it is at least
    ///     <see cref="MaxRestartCount" /> (when enabled) and is claimed once (<see cref="ResourceRuntimeObservation.RestartLimitReachedNow" />).
    /// </summary>
    public ResourceRuntimeObservation Observe(AggregatedRuntimeStatus status, int containerCount, int runningCount,
        int observedRestartCount, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(status);
        ResourceRuntimeState previous = State;
        var wasUp = IsUp;
        var observed = Math.Max(0, observedRestartCount);
        if (observed > RestartCount) LastRestartAt = now;
        else if (observed == 0) LastRestartAt = null;
        RestartCount = observed;

        var limitReachedNow = false;
        if (MaxRestartCount > 0 && observed >= MaxRestartCount)
        {
            if (!RestartLimitReached)
            {
                RestartLimitReached = true;
                limitReachedNow = true;
            }
        }
        else if (RestartLimitReached && status.State == ResourceRuntimeState.Running)
        {
            // A new container generation (redeploy, manual start) is running below the limit again.
            RestartLimitReached = false;
        }

        var changed = previous != status.State;
        State = status.State;
        Health = status.Health;
        ContainerCount = Math.Max(0, containerCount);
        RunningContainerCount = Math.Max(0, runningCount);
        LastCheckedAt = now;
        if (State == ResourceRuntimeState.Running) LastSeenRunningAt = now;
        if (changed) StateChangedAt = now;
        UpdatedAt = now;

        var stoppedUnexpectedly = changed && wasUp && State == ResourceRuntimeState.Exited &&
                                  !IsStopExpected(now) && !RestartLimitReached;
        return new ResourceRuntimeObservation(previous, State, Health, changed, stoppedUnexpectedly, limitReachedNow);
    }
}
