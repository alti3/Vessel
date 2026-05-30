using Vessel.Domain.Common;
using Vessel.Domain.ValueObjects;

namespace Vessel.Domain.Databases;

public sealed class DatabaseResource : Entity<DatabaseResourceId>
{
    private readonly List<BackupPolicy> _backupPolicies = [];

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

    public IReadOnlyCollection<BackupPolicy> BackupPolicies => _backupPolicies.AsReadOnly();

    public static DatabaseResource Create(
        EnvironmentId environmentId,
        ServerId serverId,
        ResourceName name,
        DatabaseEngine engine,
        VersionLabel version,
        StorageConfiguration storage,
        SecretReferenceId credentialsReferenceId,
        DateTimeOffset now)
    {
        return new DatabaseResource(DatabaseResourceId.New(), environmentId, serverId, name, engine, version, storage,
            credentialsReferenceId, now);
    }

    public void ChangeHealth(DatabaseHealthState healthState, DateTimeOffset now)
    {
        HealthState = healthState;
        Touch(now);
    }

    public void MarkProvisioning(DateTimeOffset now)
    {
        LifecycleState = DatabaseLifecycleState.Provisioning;
        Touch(now);
    }

    public void MarkRunning(string containerName, string composeSnapshotReference, DateTimeOffset now)
    {
        ContainerName = DomainValidation.Required(containerName, nameof(containerName), 160);
        ComposeSnapshotReference = DomainValidation.Required(composeSnapshotReference, nameof(composeSnapshotReference),
            512);
        LifecycleState = DatabaseLifecycleState.Running;
        HealthState = DatabaseHealthState.Healthy;
        Touch(now);
    }

    public void MarkStopped(DateTimeOffset now)
    {
        LifecycleState = DatabaseLifecycleState.Stopped;
        HealthState = DatabaseHealthState.Unknown;
        Touch(now);
    }

    public void MarkRestarting(DateTimeOffset now)
    {
        LifecycleState = DatabaseLifecycleState.Restarting;
        Touch(now);
    }

    public void MarkDeleted(DateTimeOffset now)
    {
        ContainerName = null;
        ComposeSnapshotReference = null;
        LifecycleState = DatabaseLifecycleState.Deleted;
        HealthState = DatabaseHealthState.Unknown;
        Touch(now);
    }

    public void MarkFailed(DateTimeOffset now)
    {
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

    public void AddBackupPolicy(string cronExpression, int retentionCount, DateTimeOffset now)
    {
        _backupPolicies.Add(new BackupPolicy(Id, cronExpression, retentionCount));
        Touch(now);
    }
}
