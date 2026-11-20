using Vessel.Domain;
using Vessel.Domain.Backups;
using Vessel.Domain.Common;
using Vessel.Domain.ValueObjects;

namespace Vessel.UnitTests.Domain;

public sealed class BackupRestoreDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BackupSchedule_DefaultsToOneHourTimeoutAndValidatesRange()
    {
        BackupSchedule schedule = CreateSchedule();

        Assert.Equal(3600, schedule.TimeoutSeconds);
        schedule.ChangeTimeout(36000, Now);
        Assert.Equal(36000, schedule.TimeoutSeconds);
        Assert.Throws<DomainException>(() => schedule.ChangeTimeout(10, Now));
        Assert.Throws<DomainException>(() => schedule.ChangeTimeout(BackupSchedule.MaxTimeoutSeconds + 1, Now));
        Assert.Throws<DomainException>(() => BackupSchedule.Create(TeamId.New(), DatabaseResourceId.New(),
            new ResourceName("daily"), "0 2 * * *", 7, BackupStorageKind.ObjectStorage, Now, 0));
    }

    [Fact]
    public void BackupExecution_CanBeRestoredRepeatedly()
    {
        BackupExecution execution = CreateSucceededBackup();

        execution.MarkRestoreValidated(Now);
        execution.MarkRestoreSucceeded(Now);
        Assert.True(execution.HasRestorableArtifact);

        execution.MarkRestoreValidated(Now);
        execution.MarkRestoreValidated(Now);
        execution.MarkRestoreFailed("boom", Now);
        Assert.Equal(BackupExecutionStatus.RestoreValidated, execution.Status);
        Assert.True(execution.HasRestorableArtifact);
    }

    [Fact]
    public void BackupExecution_FailedBackupIsNotRestorable()
    {
        var execution = BackupExecution.Queue(TeamId.New(), DatabaseResourceId.New(), null,
            BackupStorageKind.ObjectStorage, Now);
        execution.Start(Now);
        execution.Fail("dump failed", Now);

        Assert.False(execution.HasRestorableArtifact);
        Assert.Throws<DomainException>(() => execution.MarkRestoreValidated(Now));
    }

    [Fact]
    public void DatabaseRestore_TracksStateTransitions()
    {
        DatabaseRestore restore = CreateRestore();

        Assert.True(restore.IsActive);
        Assert.Throws<DomainException>(() => restore.Succeed(Now));
        restore.Start(Now);
        restore.RecordDetectedFormat("postgresql-archive", Now);
        restore.Succeed(Now.AddMinutes(1));

        Assert.Equal(DatabaseRestoreStatus.Succeeded, restore.Status);
        Assert.False(restore.IsActive);
        Assert.Equal(Now.AddMinutes(1), restore.FinishedAt);
        Assert.Throws<DomainException>(() => restore.Fail("late", Now));
        Assert.Throws<DomainException>(() => restore.Start(Now));
    }

    [Fact]
    public void DatabaseRestore_QueuedRestoreCanFailAndBoundsReason()
    {
        DatabaseRestore restore = CreateRestore();

        restore.Fail(new string('x', 5000), Now);

        Assert.Equal(DatabaseRestoreStatus.Failed, restore.Status);
        Assert.Equal(DatabaseRestore.MaxFailureReasonLength, restore.FailureReason?.Length);
    }

    [Fact]
    public void DatabaseRestore_LogKeepsMostRecentLines()
    {
        DatabaseRestore restore = CreateRestore();

        for (var index = 0; index < 2000; index++) restore.AppendLog($"line {index} {new string('y', 60)}", Now);

        Assert.True(restore.Log.Length <= DatabaseRestore.MaxLogLength);
        Assert.StartsWith("[earlier log lines truncated]", restore.Log, StringComparison.Ordinal);
        Assert.Contains("line 1999 ", restore.Log, StringComparison.Ordinal);
    }

    private static DatabaseRestore CreateRestore()
    {
        return DatabaseRestore.Queue(TeamId.New(), BackupExecutionId.New(), DatabaseResourceId.New(), UserId.New(),
            false, false, false, Now);
    }

    private static BackupSchedule CreateSchedule()
    {
        return BackupSchedule.Create(TeamId.New(), DatabaseResourceId.New(), new ResourceName("daily"), "0 2 * * *",
            7, BackupStorageKind.ObjectStorage, Now);
    }

    private static BackupExecution CreateSucceededBackup()
    {
        var execution = BackupExecution.Queue(TeamId.New(), DatabaseResourceId.New(), null,
            BackupStorageKind.ObjectStorage, Now);
        execution.Start(Now);
        execution.Succeed("vessel-backups", "database/x.pgdump", 42, new string('a', 64), Now);
        return execution;
    }
}
