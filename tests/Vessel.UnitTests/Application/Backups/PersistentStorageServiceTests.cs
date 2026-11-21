using System.Text;
using Vessel.Application.Backups;
using Vessel.Application.Backups.Volumes;
using Vessel.Application.ManagedServices;
using Vessel.Application.ManagedServices.DatabaseImports;
using Vessel.Application.Servers;
using Vessel.Application.Storage.Persistent;
using Vessel.Domain;
using Vessel.Domain.Backups;
using Vessel.Domain.Common;
using Vessel.Domain.Storage;
using Vessel.UnitTests.Application.SecurityHardening;

namespace Vessel.UnitTests.Application.Backups;

public sealed class ResourceStorageServiceTests
{
    private readonly PersistentStorageKit _k = new();
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<ResourceStorageSummary> CreateAsync(CreateResourceStorageRequest request) =>
        _k.Storage().CreateAsync(_k.F.Owner.Id, _k.F.Team.Id, StorageOwnerKind.Application, _k.Application.Id.Value,
            request, Ct);

    [Fact]
    public async Task Create_VolumeAndContentFile_ContentIsEncryptedAtRest()
    {
        ResourceStorageSummary volume = await CreateAsync(new CreateResourceStorageRequest(ResourceStorageType.Volume,
            "/data", "data"));
        Assert.Equal($"vessel-{_k.Application.Id.Value:N}-data", volume.RuntimeName);
        ResourceStorageSummary file = await CreateAsync(new CreateResourceStorageRequest(ResourceStorageType.File,
            "/etc/app.conf", Path: "./config/app.conf", Content: "secret=1"));
        Assert.Equal("config/app.conf", file.Path);
        FileMount stored = Assert.Single(_k.F.Db.FileMountItems);
        Assert.DoesNotContain("secret=1", stored.ProtectedContent!, StringComparison.Ordinal);
        Assert.True(stored.ContentPendingWrite);
    }

    [Theory]
    [InlineData("/var/run/docker.sock")]
    [InlineData("../../etc")]
    public async Task Create_RejectsHostPaths(string path)
    {
        await Assert.ThrowsAsync<DomainException>(() => CreateAsync(
            new CreateResourceStorageRequest(ResourceStorageType.Directory, "/data", Path: path)));
        Assert.Empty(_k.F.Db.FileMountItems);
    }

    [Fact]
    public async Task Create_RejectsDuplicateMountPathsOverlappingPathsAndServiceNamesOnDockerfileApps()
    {
        await CreateAsync(new CreateResourceStorageRequest(ResourceStorageType.Volume, "/data", "data"));
        await Assert.ThrowsAsync<DomainException>(() => CreateAsync(
            new CreateResourceStorageRequest(ResourceStorageType.Directory, "/data", Path: "data")));
        await CreateAsync(new CreateResourceStorageRequest(ResourceStorageType.Directory, "/uploads", Path: "files"));
        await Assert.ThrowsAsync<DomainException>(() => CreateAsync(
            new CreateResourceStorageRequest(ResourceStorageType.File, "/x.conf", Path: "files/x.conf")));
        await Assert.ThrowsAsync<DomainException>(() => CreateAsync(
            new CreateResourceStorageRequest(ResourceStorageType.Volume, "/cache", "cache", ServiceName: "worker")));
    }

    [Fact]
    public async Task ReadOnlyMemberAndOutsiderCannotWrite_ContentNeedsSecretsRead()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _k.Storage().CreateAsync(_k.F.Member.Id,
            _k.F.Team.Id, StorageOwnerKind.Application, _k.Application.Id.Value,
            new CreateResourceStorageRequest(ResourceStorageType.Volume, "/data", "data"), Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _k.Storage().CreateAsync(_k.F.Outsider.Id,
            _k.F.OtherTeam.Id, StorageOwnerKind.Application, _k.Application.Id.Value,
            new CreateResourceStorageRequest(ResourceStorageType.Volume, "/data", "data"), Ct));
        ResourceStorageSummary file = await CreateAsync(new CreateResourceStorageRequest(ResourceStorageType.File,
            "/etc/app.conf", Path: "app.conf", Content: "token=abc"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _k.Storage().GetAsync(_k.F.Member.Id,
            _k.F.Team.Id, StorageOwnerKind.Application, _k.Application.Id.Value, file.Id, true, Ct));
        ResourceStorageDetail detail = await _k.Storage().GetAsync(_k.F.Owner.Id, _k.F.Team.Id,
            StorageOwnerKind.Application, _k.Application.Id.Value, file.Id, true, Ct);
        Assert.Equal("token=abc", detail.Content);
    }

    [Fact]
    public async Task Provisioner_BuildsPreviewIsolatedMountsAndWritesPendingContentOnce()
    {
        await CreateAsync(new CreateResourceStorageRequest(ResourceStorageType.Volume, "/data", "data"));
        await CreateAsync(new CreateResourceStorageRequest(ResourceStorageType.File, "/etc/app.conf",
            Path: "config/app.conf", Content: "v1"));
        ServerExecutionTarget target = ServerExecutionTarget.From(_k.F.Server);
        ResourceStorageProvisioner provisioner = _k.Provisioner();
        var preview = provisioner.BuildSpec(StorageOwnerKind.Application, _k.Application.Id.Value, target, "pr-3",
            "web");
        Assert.Contains(preview.Mounts, mount => mount.Source.EndsWith("-data-pr-3", StringComparison.Ordinal));
        Assert.Contains(preview.Mounts, mount => mount.Source.Contains($"{_k.Application.Id.Value:N}-pr-3/config",
            StringComparison.Ordinal));

        var dir = StoragePaths.ResourceDirectory(StorageOwnerKind.Application, _k.Application.Id.Value);
        await provisioner.EnsureOnServerAsync(StorageOwnerKind.Application, _k.Application.Id.Value, target, null,
            (_, _, _) => Task.CompletedTask, Ct);
        Assert.Equal("v1", _k.Channel.Files[$"{dir}/config/app.conf"]);
        Assert.False(_k.F.Db.FileMountItems.Single().ContentPendingWrite);
        // A file changed on the server is kept when the content did not change in Vessel.
        _k.Channel.Files[$"{dir}/config/app.conf"] = "edited";
        await provisioner.EnsureOnServerAsync(StorageOwnerKind.Application, _k.Application.Id.Value, target, null,
            (_, _, _) => Task.CompletedTask, Ct);
        Assert.Equal("edited", _k.Channel.Files[$"{dir}/config/app.conf"]);
    }

    [Fact]
    public async Task Delete_IsBlockedWhileABackupScheduleExists()
    {
        ResourceStorageSummary volume = await CreateAsync(new CreateResourceStorageRequest(ResourceStorageType.Volume,
            "/data", "data"));
        await _k.VolumeBackups().CreateScheduleAsync(_k.F.Owner.Id, _k.F.Team.Id, StorageOwnerKind.Application,
            _k.Application.Id.Value, volume.Id, new CreateVolumeBackupScheduleRequest("0 3 * * *"), Ct);
        await Assert.ThrowsAsync<DomainException>(() => _k.Storage().DeleteAsync(_k.F.Owner.Id, _k.F.Team.Id,
            StorageOwnerKind.Application, _k.Application.Id.Value, volume.Id, Ct));
    }
}

public sealed class VolumeBackupServiceTests
{
    private readonly PersistentStorageKit _k = new();
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(Guid StorageId, VolumeBackupScheduleSummary Schedule)> ScheduleAsync(
        bool stopDuringBackup = false)
    {
        ResourceStorageSummary volume = await _k.Storage().CreateAsync(_k.F.Owner.Id, _k.F.Team.Id,
            StorageOwnerKind.Application, _k.Application.Id.Value,
            new CreateResourceStorageRequest(ResourceStorageType.Volume, "/data", "data"), Ct);
        VolumeBackupScheduleSummary schedule = await _k.VolumeBackups().CreateScheduleAsync(_k.F.Owner.Id,
            _k.F.Team.Id, StorageOwnerKind.Application, _k.Application.Id.Value, volume.Id,
            new CreateVolumeBackupScheduleRequest("0 3 * * *", StopDuringBackup: stopDuringBackup), Ct);
        return (volume.Id, schedule);
    }

    [Fact]
    public async Task CreateSchedule_RegistersRecurringJob_OnePerTarget_NotForContentFiles()
    {
        (Guid storageId, VolumeBackupScheduleSummary schedule) = await ScheduleAsync();
        Assert.True(_k.F.Jobs.Recurring.ContainsKey($"volume-backup:{schedule.Id:D}"));
        await Assert.ThrowsAsync<DomainException>(() => _k.VolumeBackups().CreateScheduleAsync(_k.F.Owner.Id,
            _k.F.Team.Id, StorageOwnerKind.Application, _k.Application.Id.Value, storageId,
            new CreateVolumeBackupScheduleRequest("0 3 * * *"), Ct));
        ResourceStorageSummary file = await _k.Storage().CreateAsync(_k.F.Owner.Id, _k.F.Team.Id,
            StorageOwnerKind.Application, _k.Application.Id.Value,
            new CreateResourceStorageRequest(ResourceStorageType.File, "/a.conf", Path: "a.conf", Content: "x"), Ct);
        await Assert.ThrowsAsync<DomainException>(() => _k.VolumeBackups().CreateScheduleAsync(_k.F.Owner.Id,
            _k.F.Team.Id, StorageOwnerKind.Application, _k.Application.Id.Value, file.Id,
            new CreateVolumeBackupScheduleRequest("0 3 * * *"), Ct));
    }

    [Fact]
    public async Task HostLocalServer_RequiresInstanceAdmin()
    {
        ResourceStorageSummary volume = await _k.Storage().CreateAsync(_k.Admin.Id, _k.F.Team.Id,
            StorageOwnerKind.Application, _k.Application.Id.Value,
            new CreateResourceStorageRequest(ResourceStorageType.Volume, "/data", "data"), Ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _k.VolumeBackups().CreateScheduleAsync(
            _k.Admin.Id, _k.F.Team.Id, StorageOwnerKind.Application, _k.Application.Id.Value, volume.Id,
            new CreateVolumeBackupScheduleRequest("0 3 * * *"), Ct));
    }

    [Fact]
    public async Task Run_StopsAndRestartsContainers_StoresLocalCopy_AndNotifies()
    {
        (_, VolumeBackupScheduleSummary schedule) = await ScheduleAsync(true);
        _k.Runner.Containers = ["abcdef123456"];
        VolumeBackupExecutionSummary result =
            (await _k.VolumeBackups().RunScheduledAsync(new ScheduledVolumeBackupId(schedule.Id), Ct))!;
        Assert.Equal(VolumeBackupStatus.Succeeded, result.Status);
        Assert.Equal(["stop:abcdef123456", "archive", "start:abcdef123456"], _k.Runner.Calls);
        Assert.False(result.StopRecoveryPending);
        Assert.Single(_k.F.Storage.LocalStorage.Objects);
        Assert.Equal($"vessel-{_k.Application.Id.Value:N}-data", _k.Runner.LastSource!.Source);
        Assert.Contains(_k.F.Notifications.Published, item => item.Notification.EventType == "backup.succeeded");
    }

    [Fact]
    public async Task FailedRestart_IsRecoveredWithBoundedAttempts()
    {
        (_, VolumeBackupScheduleSummary schedule) = await ScheduleAsync(true);
        _k.Runner.Containers = ["abcdef123456"];
        _k.Runner.FailStart = true;
        VolumeBackupExecutionSummary result =
            (await _k.VolumeBackups().RunScheduledAsync(new ScheduledVolumeBackupId(schedule.Id), Ct))!;
        Assert.True(result.StopRecoveryPending);
        for (var attempt = 0; attempt < VolumeBackupExecution.MaxRecoveryAttempts + 2; attempt++)
        {
            await _k.VolumeBackups().RecoverAsync(Ct);
            _k.F.Time.Advance(VolumeBackupService.RecoveryRetryInterval);
        }

        VolumeBackupExecution execution = _k.F.Db.VolumeExecutionItems.Single();
        Assert.True(execution.RecoveryNeedsAttention);
        Assert.Equal(VolumeBackupExecution.MaxRecoveryAttempts, execution.RecoveryAttempts);
        _k.Runner.FailStart = false;
        await _k.VolumeBackups().RetryRecoveryAsync(_k.F.Owner.Id, _k.F.Team.Id, execution.Id, Ct);
        await _k.VolumeBackups().RecoverAsync(Ct);
        Assert.False(execution.StopRecoveryPending);
    }

    [Fact]
    public async Task Run_SkipsWhenAnotherOperationHoldsTheTarget_AndFailsWhenVolumeMissing()
    {
        (Guid storageId, VolumeBackupScheduleSummary schedule) = await ScheduleAsync();
        await using (await _k.F.Locks.TryAcquireAsync(
                         VolumeBackupService.LockKey(VolumeBackupTargetKind.PersistentVolume, storageId),
                         TimeSpan.FromMinutes(5), TimeSpan.Zero, Ct))
        {
            VolumeBackupExecutionSummary skipped =
                (await _k.VolumeBackups().RunScheduledAsync(new ScheduledVolumeBackupId(schedule.Id), Ct))!;
            Assert.Equal(VolumeBackupStatus.Skipped, skipped.Status);
        }

        _k.Runner.Exists = false;
        VolumeBackupExecutionSummary failed =
            (await _k.VolumeBackups().RunScheduledAsync(new ScheduledVolumeBackupId(schedule.Id), Ct))!;
        Assert.Equal(VolumeBackupStatus.Failed, failed.Status);
    }

    [Fact]
    public async Task Restore_RequiresConfirmation_AndStreamsTheArchive()
    {
        (_, VolumeBackupScheduleSummary schedule) = await ScheduleAsync();
        VolumeBackupExecutionSummary run =
            (await _k.VolumeBackups().RunScheduledAsync(new ScheduledVolumeBackupId(schedule.Id), Ct))!;
        await Assert.ThrowsAsync<DomainException>(() => _k.VolumeBackups().QueueRestoreAsync(_k.F.Owner.Id,
            _k.F.Team.Id, new VolumeBackupExecutionId(run.Id), new QueueVolumeRestoreRequest("yes"), Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _k.VolumeBackups().QueueRestoreAsync(
            _k.F.Member.Id, _k.F.Team.Id, new VolumeBackupExecutionId(run.Id),
            new QueueVolumeRestoreRequest("RESTORE"), Ct));
        VolumeBackupRestoreSummary queued = await _k.VolumeBackups().QueueRestoreAsync(_k.F.Owner.Id, _k.F.Team.Id,
            new VolumeBackupExecutionId(run.Id), new QueueVolumeRestoreRequest("RESTORE"), Ct);
        VolumeBackupRestoreSummary done =
            await _k.VolumeBackups().RunRestoreAsync(new VolumeBackupRestoreId(queued.Id), Ct);
        Assert.Equal(VolumeRestoreStatus.Succeeded, done.Status);
        Assert.Equal("tarball", Encoding.UTF8.GetString(_k.Runner.RestoredBytes!));
    }

    [Fact]
    public async Task ListExecutions_HidesFailureMessagesWithoutSecretsRead()
    {
        (_, VolumeBackupScheduleSummary schedule) = await ScheduleAsync();
        _k.Runner.Exists = false;
        await _k.VolumeBackups().RunScheduledAsync(new ScheduledVolumeBackupId(schedule.Id), Ct);
        PagedVolumeBackupExecutions forMember = await _k.VolumeBackups().ListExecutionsAsync(_k.F.Member.Id,
            _k.F.Team.Id, new ScheduledVolumeBackupId(schedule.Id), 1, 10, Ct);
        Assert.Null(forMember.Items.Single().FailureReason);
        Assert.True(forMember.Items.Single().MessageHidden);
        // Another team's schedule is reported as missing, so IDs cannot be probed.
        await Assert.ThrowsAsync<InvalidOperationException>(() => _k.VolumeBackups().ListExecutionsAsync(
            _k.F.Outsider.Id, _k.F.OtherTeam.Id, new ScheduledVolumeBackupId(schedule.Id), 1, 10, Ct));
    }
}

public sealed class DatabaseImportServiceTests
{
    private readonly PersistentStorageKit _k = new();
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private DatabaseResourceId Db => _k.F.Database.Id;

    private async Task<DatabaseImportUploadSummary> UploadAsync(string content)
    {
        DatabaseImportService imports = _k.Imports();
        var bytes = Encoding.UTF8.GetBytes(content);
        DatabaseImportUploadSummary upload = await imports.StartUploadAsync(_k.F.Owner.Id, _k.F.Team.Id, Db,
            new StartDatabaseImportUploadRequest("dump.sql", bytes.Length), Ct);
        await imports.AppendChunkAsync(_k.F.Owner.Id, _k.F.Team.Id, Db, new DatabaseImportUploadId(upload.Id), 0,
            new MemoryStream(bytes[..3]), Ct);
        await imports.AppendChunkAsync(_k.F.Owner.Id, _k.F.Team.Id, Db, new DatabaseImportUploadId(upload.Id), 3,
            new MemoryStream(bytes[3..]), Ct);
        return await imports.CompleteUploadAsync(_k.F.Owner.Id, _k.F.Team.Id, Db,
            new DatabaseImportUploadId(upload.Id), Ct);
    }

    [Fact]
    public async Task ChunkedUpload_IsSequential_AndBoundedBySize()
    {
        DatabaseImportService imports = _k.Imports();
        await Assert.ThrowsAsync<DomainException>(() => imports.StartUploadAsync(_k.F.Owner.Id, _k.F.Team.Id, Db,
            new StartDatabaseImportUploadRequest("dump.sql", 4096), Ct));
        DatabaseImportUploadSummary upload = await imports.StartUploadAsync(_k.F.Owner.Id, _k.F.Team.Id, Db,
            new StartDatabaseImportUploadRequest("dump.sql", 6), Ct);
        await Assert.ThrowsAsync<DomainException>(() => imports.AppendChunkAsync(_k.F.Owner.Id, _k.F.Team.Id, Db,
            new DatabaseImportUploadId(upload.Id), 2, new MemoryStream([1, 2]), Ct));
        await Assert.ThrowsAsync<DomainException>(() => imports.AppendChunkAsync(_k.F.Owner.Id, _k.F.Team.Id, Db,
            new DatabaseImportUploadId(upload.Id), 0, new MemoryStream(new byte[7]), Ct));
        Assert.Equal(0, _k.F.Db.ImportUploadItems.Single().ReceivedBytes);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => imports.StartUploadAsync(_k.F.Member.Id,
            _k.F.Team.Id, Db, new StartDatabaseImportUploadRequest("dump.sql", 6), Ct));
    }

    [Fact]
    public async Task ServerPath_RequiresInstanceAdmin_BeforeThePathIsChecked()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _k.Imports().QueueImportAsync(_k.Admin.Id,
            _k.F.Team.Id, Db,
            new QueueDatabaseImportRequest(DatabaseImportSourceRequestKind.Server, Path: "/etc/shadow",
                Confirmation: "RESTORE"), Ct));
        Assert.Equal(0, _k.Staging.PathValidations);
        await Assert.ThrowsAsync<DomainException>(() => _k.Imports().QueueImportAsync(_k.F.Owner.Id, _k.F.Team.Id,
            Db, new QueueDatabaseImportRequest(DatabaseImportSourceRequestKind.Server, Path: "/etc/shadow",
                Confirmation: "RESTORE"), Ct));
        DatabaseRestoreSummary queued = await _k.Imports().QueueImportAsync(_k.F.Owner.Id, _k.F.Team.Id, Db,
            new QueueDatabaseImportRequest(DatabaseImportSourceRequestKind.Server, Path: "/imports/dump.sql",
                Confirmation: "RESTORE"), Ct);
        Assert.Equal(DatabaseImportSourceKind.ServerPath, queued.ImportSource);
        Assert.Null(queued.BackupExecutionId);
    }

    [Fact]
    public async Task Destination_OfAnotherTeamIsRejected()
    {
        BackupDestination foreign = await _k.F.AddDestinationAsync("foreign", _k.F.OtherTeam);
        await Assert.ThrowsAsync<DomainException>(() => _k.Imports().QueueImportAsync(_k.F.Owner.Id, _k.F.Team.Id,
            Db, new QueueDatabaseImportRequest(DatabaseImportSourceRequestKind.Destination, Path: "dump.sql",
                DestinationId: foreign.Id.Value, Confirmation: "RESTORE"), Ct));
        BackupDestination own = await _k.F.AddDestinationAsync();
        await Assert.ThrowsAsync<DomainException>(() => _k.Imports().QueueImportAsync(_k.F.Owner.Id, _k.F.Team.Id,
            Db, new QueueDatabaseImportRequest(DatabaseImportSourceRequestKind.Destination, Path: "../x.sql",
                DestinationId: own.Id.Value, Confirmation: "RESTORE"), Ct));
    }

    [Fact]
    public async Task UploadImport_RunsThroughTheRestorePipeline_AndConsumesTheUpload()
    {
        DatabaseImportUploadSummary upload = await UploadAsync("CREATE TABLE t();");
        await Assert.ThrowsAsync<DomainException>(() => _k.Imports().QueueImportAsync(_k.F.Owner.Id, _k.F.Team.Id,
            Db, new QueueDatabaseImportRequest(DatabaseImportSourceRequestKind.Upload, upload.Id), Ct));
        DatabaseRestoreSummary queued = await _k.Imports().QueueImportAsync(_k.F.Owner.Id, _k.F.Team.Id, Db,
            new QueueDatabaseImportRequest(DatabaseImportSourceRequestKind.Upload, upload.Id,
                Confirmation: "RESTORE"), Ct);
        var provider = new RecordingRestoreProvider();
        var restores = new DatabaseRestoreService(_k.F.Db, _k.F.Authorization, _k.F.Jobs, _k.F.Locks, _k.F.Vault,
            new PassthroughRedactor(), provider, new BackupArtifactReader(_k.F.Db, _k.F.Storage, _k.F.Connector),
            _k.F.Audit, new NullRealtime(), _k.F.Notifications, _k.F.Time, _k.ImportSource());
        DatabaseRestoreSummary done = await restores.RunRestoreAsync(new DatabaseRestoreId(queued.Id), Ct);
        Assert.Equal(DatabaseRestoreStatus.Succeeded, done.Status);
        Assert.Equal("CREATE TABLE t();", provider.LastArtifact);
        Assert.Null(provider.Restores.Single().ExpectedSha256);
        Assert.NotNull(_k.F.Db.ImportUploadItems.Single().ConsumedAt);
        Assert.Empty(_k.Staging.Uploads);
        DatabaseRestoreSummary status = await _k.Imports().GetImportAsync(_k.F.Member.Id, _k.F.Team.Id, Db,
            new DatabaseRestoreId(queued.Id), Ct);
        Assert.Equal(DatabaseRestoreStatus.Succeeded, status.Status);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _k.Imports().GetImportAsync(_k.F.Outsider.Id,
            _k.F.OtherTeam.Id, Db, new DatabaseRestoreId(queued.Id), Ct));
    }

    [Fact]
    public async Task Cleanup_DeletesExpiredUploads()
    {
        await UploadAsync("SELECT 1;");
        _k.F.Time.Advance(DatabaseImportUpload.Retention + TimeSpan.FromMinutes(1));
        Assert.Equal(1, await _k.Imports().CleanupUploadsAsync(Ct));
        Assert.Empty(_k.F.Db.ImportUploadItems);
        Assert.Empty(_k.Staging.Uploads);
    }
}

internal sealed class NullRealtime : Vessel.Application.Realtime.IRealtimeNotifier
{
    public Task PublishAsync(Vessel.Application.Realtime.RealtimeGroup group,
        Vessel.Application.Realtime.RealtimeMessage message, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
