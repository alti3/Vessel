using Vessel.Domain.Common;
using Vessel.Domain.ValueObjects;

namespace Vessel.Domain.Databases;

public sealed class DatabaseResource : Entity<DatabaseResourceId>
{
    private DatabaseResource()
    {
    }

    private DatabaseResource(
        DatabaseResourceId id,
        EnvironmentId environmentId,
        ServerId serverId,
        ResourceName name,
        DatabaseEngine engine,
        VersionLabel version,
        StorageConfiguration storage,
        SecretReferenceId credentialsReferenceId,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        EnvironmentId = environmentId;
        ServerId = serverId;
        Name = name;
        Engine = engine;
        Version = version;
        Storage = storage;
        CredentialsReferenceId = credentialsReferenceId;
    }

    public EnvironmentId EnvironmentId { get; private set; }

    public ServerId ServerId { get; private set; }

    public ResourceName Name { get; private set; }

    public Description? Description { get; private set; }

    public DatabaseEngine Engine { get; private set; }

    public VersionLabel Version { get; private set; }

    public StorageConfiguration Storage { get; private set; }

    public SecretReferenceId CredentialsReferenceId { get; private set; }

    public DatabaseHealthState HealthState { get; private set; } = DatabaseHealthState.Unknown;

    public DatabaseLifecycleState LifecycleState { get; private set; } = DatabaseLifecycleState.NotProvisioned;

    public string? ContainerName { get; private set; }

    public string? ComposeSnapshotReference { get; private set; }

    /// <summary>
    ///     Full container image reference (repository plus explicit tag or digest). <see langword="null" /> for databases
    ///     created before images were pinned; those keep their legacy <c>{engine image}:{Version}</c> reference.
    /// </summary>
    public DatabaseImage? Image { get; private set; }

    public bool HealthCheckEnabled { get; private set; } = true;

    public int HealthCheckIntervalSeconds { get; private set; } = DatabaseHealthCheckPolicy.DefaultIntervalSeconds;

    public int HealthCheckTimeoutSeconds { get; private set; } = DatabaseHealthCheckPolicy.DefaultTimeoutSeconds;

    public int HealthCheckRetries { get; private set; } = DatabaseHealthCheckPolicy.DefaultRetries;

    public int HealthCheckStartPeriodSeconds { get; private set; } =
        DatabaseHealthCheckPolicy.DefaultStartPeriodSeconds;

    public DateTimeOffset? LastHealthCheckAt { get; private set; }

    public DatabaseHealthCheckPolicy HealthCheck => new(HealthCheckEnabled, HealthCheckIntervalSeconds,
        HealthCheckTimeoutSeconds, HealthCheckRetries, HealthCheckStartPeriodSeconds);

    public static DatabaseResource Create(
        EnvironmentId environmentId,
        ServerId serverId,
        ResourceName name,
        DatabaseEngine engine,
        VersionLabel version,
        StorageConfiguration storage,
        SecretReferenceId credentialsReferenceId,
        DateTimeOffset now,
        DatabaseImage? image = null)
    {
        return new DatabaseResource(DatabaseResourceId.New(), environmentId, serverId, name, engine, version, storage,
            credentialsReferenceId, now)
        {
            Image = image
        };
    }

    public void ChangeHealth(DatabaseHealthState healthState, DateTimeOffset now)
    {
        HealthState = healthState;
        Touch(now);
    }

    /// <summary>
    ///     Records the result of a runtime health probe. Only running databases take probe results; the lifecycle
    ///     transitions own the health state of stopped, deleted or failed databases.
    /// </summary>
    public bool RecordHealthProbe(DatabaseHealthState observed, DateTimeOffset now)
    {
        if (LifecycleState != DatabaseLifecycleState.Running) return false;
        LastHealthCheckAt = now;
        if (HealthState == observed) return false;
        HealthState = observed;
        Touch(now);
        return true;
    }

    public void ChangeHealthCheck(DatabaseHealthCheckPolicy policy, DateTimeOffset now)
    {
        HealthCheckEnabled = policy.Enabled;
        HealthCheckIntervalSeconds = policy.IntervalSeconds;
        HealthCheckTimeoutSeconds = policy.TimeoutSeconds;
        HealthCheckRetries = policy.Retries;
        HealthCheckStartPeriodSeconds = policy.StartPeriodSeconds;
        Touch(now);
    }

    public void ChangeImage(DatabaseImage image, DateTimeOffset now)
    {
        Image = image;
        Version = new VersionLabel(image.Tag ?? image.Digest ?? Version.Value);
        Touch(now);
    }

    public void MarkProvisioning(DateTimeOffset now)
    {
        EnsureLifecycleState(
            [DatabaseLifecycleState.NotProvisioned, DatabaseLifecycleState.Stopped, DatabaseLifecycleState.Failed],
            DatabaseLifecycleState.Provisioning);
        LifecycleState = DatabaseLifecycleState.Provisioning;
        Touch(now);
    }

    public void MarkRunning(string containerName, string composeSnapshotReference, DateTimeOffset now)
    {
        EnsureLifecycleState(
            [DatabaseLifecycleState.Provisioning, DatabaseLifecycleState.Restarting],
            DatabaseLifecycleState.Running);
        // SEC-10: the name is passed to `docker exec` and backup commands; it must be a valid container name.
        ContainerName = Vessel.Domain.ValueObjects.ContainerName.Parse(containerName).Value;
        ComposeSnapshotReference = DomainValidation.Required(composeSnapshotReference, nameof(composeSnapshotReference),
            512);
        LifecycleState = DatabaseLifecycleState.Running;
        // A started container is not healthy until its probe says so; the health poller records the probe result.
        HealthState = HealthCheckEnabled ? DatabaseHealthState.Starting : DatabaseHealthState.Unknown;
        Touch(now);
    }

    public void MarkStopped(DateTimeOffset now)
    {
        EnsureLifecycleState(
            [DatabaseLifecycleState.Running, DatabaseLifecycleState.Restarting],
            DatabaseLifecycleState.Stopped);
        LifecycleState = DatabaseLifecycleState.Stopped;
        HealthState = DatabaseHealthState.Unknown;
        Touch(now);
    }

    public void MarkRestarting(DateTimeOffset now)
    {
        EnsureLifecycleState([DatabaseLifecycleState.Running], DatabaseLifecycleState.Restarting);
        LifecycleState = DatabaseLifecycleState.Restarting;
        Touch(now);
    }

    public void MarkDeleted(DateTimeOffset now)
    {
        EnsureLifecycleState(
            [DatabaseLifecycleState.NotProvisioned, DatabaseLifecycleState.Provisioning, DatabaseLifecycleState.Running,
                DatabaseLifecycleState.Stopped, DatabaseLifecycleState.Restarting, DatabaseLifecycleState.Failed],
            DatabaseLifecycleState.Deleted);
        ContainerName = null;
        ComposeSnapshotReference = null;
        LifecycleState = DatabaseLifecycleState.Deleted;
        HealthState = DatabaseHealthState.Unknown;
        Touch(now);
    }

    public void MarkFailed(DateTimeOffset now)
    {
        if (LifecycleState is DatabaseLifecycleState.Deleted)
            throw new DomainException("Deleted databases cannot be marked failed.");
        LifecycleState = DatabaseLifecycleState.Failed;
        HealthState = DatabaseHealthState.Unhealthy;
        Touch(now);
    }

    public void UpdateSettings(
        ResourceName name,
        Description? description,
        VersionLabel version,
        StorageConfiguration storage,
        SecretReferenceId credentialsReferenceId,
        DateTimeOffset now)
    {
        Name = name;
        Description = description;
        Version = version;
        Storage = storage;
        CredentialsReferenceId = credentialsReferenceId;
        Touch(now);
    }

    private void EnsureLifecycleState(
        IReadOnlyCollection<DatabaseLifecycleState> allowedSourceStates,
        DatabaseLifecycleState targetState)
    {
        if (!allowedSourceStates.Contains(LifecycleState))
            throw new DomainException($"Cannot transition database from {LifecycleState} to {targetState}.");
    }
}
