using Vessel.Domain;
using Vessel.Domain.Backups;
using Vessel.Domain.Common;
using Vessel.Domain.Storage;

namespace Vessel.UnitTests.Domain;

public sealed class PersistentStorageDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("../outside")]
    [InlineData("config/../../x")]
    [InlineData("~/data")]
    [InlineData("$HOME/data")]
    [InlineData("C:/data")]
    [InlineData("-rf")]
    [InlineData("a b")]
    public void RelativePath_RejectsHostPathsAndTraversal(string path)
    {
        Assert.Throws<DomainException>(() => StoragePaths.ValidateRelativePath(path));
    }

    [Fact]
    public void RelativePath_NormalizesDotSlashPrefix()
    {
        Assert.Equal("config/app.conf", StoragePaths.ValidateRelativePath("./config/app.conf"));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("data")]
    [InlineData("/data/../etc")]
    [InlineData("/data:/etc")]
    [InlineData("/da ta")]
    public void MountPath_RejectsInvalidContainerPaths(string mountPath)
    {
        Assert.Throws<DomainException>(() => StoragePaths.ValidateMountPath(mountPath));
    }

    [Fact]
    public void RuntimeVolumeName_IsNamespacedPerOwnerAndPreview()
    {
        var owner = Guid.NewGuid();
        var production = StoragePaths.RuntimeVolumeName(owner, "data");
        var preview = StoragePaths.RuntimeVolumeName(owner, "data", "pr-7");
        Assert.Equal($"vessel-{owner:N}-data", production);
        Assert.Equal($"vessel-{owner:N}-data-pr-7", preview);
        Assert.NotEqual(StoragePaths.ResourceDirectory(StorageOwnerKind.Application, owner),
            StoragePaths.ResourceDirectory(StorageOwnerKind.Application, owner, "pr-7"));
    }

    [Fact]
    public void Volume_RenameRequiresConfirmation()
    {
        var volume = PersistentVolume.Create(TeamId.New(), StorageOwnerKind.Application, Guid.NewGuid(), "data",
            "/data", null, Now);
        Assert.Throws<DomainException>(() => volume.Update("other", "/data", null, false, Now));
        volume.Update("data", "/srv", null, false, Now);
        volume.Update("other", "/srv", null, true, Now);
        Assert.Equal("other", volume.Name);
    }

    [Fact]
    public void FileMount_DirectoriesAndHostFilesNeverHoldContent()
    {
        var directory = FileMount.CreateDirectory(TeamId.New(), StorageOwnerKind.Service, Guid.NewGuid(), "data",
            "/data", null, Now);
        Assert.Throws<DomainException>(() => directory.ReplaceContent("enc", 3, Now));
        var host = FileMount.CreateHostFile(TeamId.New(), StorageOwnerKind.Service, Guid.NewGuid(), "app.conf",
            "/etc/app.conf", null, Now);
        Assert.Throws<DomainException>(() => host.ReplaceContent("enc", 3, Now));
        var file = FileMount.CreateFile(TeamId.New(), StorageOwnerKind.Service, Guid.NewGuid(), "app.conf",
            "/etc/app.conf", null, null, 0, Now);
        Assert.Throws<DomainException>(() => file.ReplaceContent("enc", FileMount.MaxContentBytes + 1L, Now));
        file.ReplaceContent("enc", 3, Now);
        Assert.True(file.ContentPendingWrite);
        file.MarkContentWritten(Now);
        Assert.False(file.ContentPendingWrite);
    }

    [Fact]
    public void Schedule_RequiresACopyLocationAndThrottlesFailures()
    {
        var schedule = ScheduledVolumeBackup.Create(TeamId.New(), StorageOwnerKind.Application, Guid.NewGuid(),
            VolumeBackupTargetKind.PersistentVolume, Guid.NewGuid(), Now);
        Assert.Throws<DomainException>(() => schedule.Configure("0 0 * * *", false,
            new BackupRetentionPolicy(7, 0, 0), new BackupRetentionPolicy(7, 0, 0), [], true, 3600, 0, Now));
        Assert.True(schedule.RecordFailure(Now));
        Assert.False(schedule.RecordFailure(Now.AddHours(1)));
        Assert.True(schedule.RecordFailure(Now.AddDays(1)));
        Assert.True(schedule.RecordSuccess(Now.AddDays(2)));
    }

    [Fact]
    public void Schedule_MissingAlertOncePerIdlePeriod()
    {
        var schedule = ScheduledVolumeBackup.Create(TeamId.New(), StorageOwnerKind.Application, Guid.NewGuid(),
            VolumeBackupTargetKind.DirectoryMount, Guid.NewGuid(), Now);
        schedule.Configure("0 0 * * *", false, new BackupRetentionPolicy(7, 0, 0), new BackupRetentionPolicy(7, 0, 0),
            [], false, 3600, 2, Now);
        Assert.False(schedule.IsBackupMissing(Now.AddDays(1)));
        Assert.True(schedule.IsBackupMissing(Now.AddDays(3)));
        schedule.RecordMissingAlertSent(Now.AddDays(3));
        Assert.False(schedule.IsBackupMissing(Now.AddDays(4)));
    }

    [Fact]
    public void Execution_RecoveryNeedsAttentionAfterBoundedAttempts()
    {
        var execution = VolumeBackupExecution.Queue(TeamId.New(), ScheduledVolumeBackupId.New(), false, Now);
        execution.Start(Now);
        execution.RecordContainersToStop(["abc123def456"], Now);
        Assert.True(execution.StopRecoveryPending);
        for (var attempt = 1; attempt < VolumeBackupExecution.MaxRecoveryAttempts; attempt++)
        {
            execution.RecordRecoveryFailure("ssh failed", Now);
            Assert.False(execution.RecoveryNeedsAttention);
        }

        execution.RecordRecoveryFailure("ssh failed", Now);
        Assert.True(execution.RecoveryNeedsAttention);
        execution.ResetRecovery(Now);
        Assert.False(execution.RecoveryNeedsAttention);
        Assert.Equal(0, execution.RecoveryAttempts);
        execution.RecordContainersRestarted(Now);
        Assert.False(execution.StopRecoveryPending);
    }

    [Fact]
    public void Execution_RejectsContainerIdsThatCouldBeOptions()
    {
        var execution = VolumeBackupExecution.Queue(TeamId.New(), ScheduledVolumeBackupId.New(), false, Now);
        Assert.Throws<DomainException>(() => execution.RecordContainersToStop(["--rm"], Now));
    }

    [Theory]
    [InlineData("../dump.sql")]
    [InlineData("/tmp/dump.sql")]
    [InlineData("dump.exe")]
    [InlineData("dir/dump.sql")]
    public void ImportUpload_RejectsUnsafeFileNames(string name)
    {
        Assert.Throws<DomainException>(() => DatabaseImportUpload.ValidateFileName(name));
    }

    [Fact]
    public void ImportUpload_ChunksAreSequentialAndBounded()
    {
        var upload = DatabaseImportUpload.Start(TeamId.New(), DatabaseResourceId.New(), UserId.New(), "dump.sql.gz",
            10, DatabaseImportUpload.DefaultMaxBytes, Now);
        Assert.Throws<DomainException>(() => upload.RecordChunk(4, 4, Now));
        upload.RecordChunk(0, 6, Now);
        Assert.Throws<DomainException>(() => upload.RecordChunk(6, 5, Now));
        Assert.Throws<DomainException>(() => upload.Complete(Now));
        upload.RecordChunk(6, 4, Now);
        upload.Complete(Now);
        Assert.Throws<DomainException>(() => upload.RecordChunk(10, 0, Now));
        Assert.True(upload.IsExpired(Now + DatabaseImportUpload.Retention));
        Assert.Throws<DomainException>(() => DatabaseImportUpload.Start(TeamId.New(), DatabaseResourceId.New(),
            UserId.New(), "dump.sql", 11, 10, Now));
    }

    [Fact]
    public void DatabaseRestore_QueueImportHasNoBackupExecution()
    {
        var restore = DatabaseRestore.QueueImport(TeamId.New(), DatabaseResourceId.New(), UserId.New(),
            DatabaseImportSourceKind.Upload, Guid.NewGuid().ToString("D"), null, null, false, false, false, Now);
        Assert.True(restore.IsImport);
        Assert.Null(restore.BackupExecutionId);
        Assert.Throws<DomainException>(() => DatabaseRestore.QueueImport(TeamId.New(), DatabaseResourceId.New(),
            UserId.New(), DatabaseImportSourceKind.Destination, "key", null, null, false, false, false, Now));
    }
}
