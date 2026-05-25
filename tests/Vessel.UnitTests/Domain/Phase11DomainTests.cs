using Vessel.Domain;
using Vessel.Domain.Backups;
using Vessel.Domain.Common;
using Vessel.Domain.Databases;
using Vessel.Domain.ValueObjects;

namespace Vessel.UnitTests.Domain;

public sealed class Phase11DomainTests
{
    [Fact]
    public void DatabaseLifecycle_TracksProvisioningRunningStopAndFailure()
    {
        DateTimeOffset now = new(2026, 5, 25, 12, 0, 0, TimeSpan.Zero);
        DatabaseResource database = CreateDatabase(now);

        database.MarkProvisioning(now);
        database.MarkRunning("vessel-db", "docker-compose.yml", now.AddMinutes(1));
        database.MarkStopped(now.AddMinutes(2));
        database.MarkFailed(now.AddMinutes(3));

        Assert.Equal(DatabaseLifecycleState.Failed, database.LifecycleState);
        Assert.Equal(DatabaseHealthState.Unhealthy, database.HealthState);
        Assert.Equal("vessel-db", database.ContainerName);
        Assert.Equal("docker-compose.yml", database.ComposeSnapshotReference);
    }

    [Fact]
    public void BackupSchedule_ValidatesCronAndRetention()
    {
        DateTimeOffset now = new(2026, 5, 25, 12, 0, 0, TimeSpan.Zero);

        BackupSchedule schedule = BackupSchedule.Create(
            TeamId.New(),
            DatabaseResourceId.New(),
            new ResourceName("daily"),
            "0 2 * * *",
            7,
            BackupStorageKind.ObjectStorage,
            now);

        Assert.True(schedule.Enabled);
        Assert.Equal(7, schedule.RetentionCount);
        Assert.Throws<DomainException>(() => BackupSchedule.Create(
            TeamId.New(),
            DatabaseResourceId.New(),
            new ResourceName("bad"),
            "* *",
            7,
            BackupStorageKind.Local,
            now));
        Assert.Throws<DomainException>(() => schedule.Update(
            new ResourceName("daily"),
            "0 2 * * *",
            0,
            BackupStorageKind.Local,
            true,
            now));
    }

    [Fact]
    public void BackupExecution_ProtectsArtifactsAndRequiresValidationBeforeRestore()
    {
        DateTimeOffset now = new(2026, 5, 25, 12, 0, 0, TimeSpan.Zero);
        BackupExecution execution = BackupExecution.Queue(
            TeamId.New(),
            DatabaseResourceId.New(),
            BackupScheduleId.New(),
            BackupStorageKind.ObjectStorage,
            now);

        execution.Start(now);
        execution.Succeed("backups", "db.dump", 120, new string('a', 64), now.AddSeconds(1));
        execution.Protect(now.AddSeconds(2));

        Assert.Throws<DomainException>(() => execution.MarkPruned(now.AddSeconds(3)));
        execution.MarkRestoreValidated(now.AddSeconds(4));
        execution.MarkRestoreSucceeded(now.AddSeconds(5));

        Assert.Equal(BackupExecutionStatus.RestoreSucceeded, execution.Status);
    }

    private static DatabaseResource CreateDatabase(DateTimeOffset now)
    {
        return DatabaseResource.Create(
            EnvironmentId.New(),
            ServerId.New(),
            new ResourceName("postgres"),
            DatabaseEngine.PostgreSql,
            new VersionLabel("16"),
            new StorageConfiguration("pg-data", "/var/lib/postgresql/data"),
            SecretReferenceId.New(),
            now);
    }
}
