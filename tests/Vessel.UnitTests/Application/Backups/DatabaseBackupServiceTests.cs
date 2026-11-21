using Vessel.Application.Auditing;
using Vessel.Application.Backups;
using Vessel.Application.Common;
using Vessel.Application.Docker;
using Vessel.Application.ManagedServices;
using Vessel.Application.Notifications;
using Vessel.Application.Redis;
using Vessel.Domain;
using Vessel.Domain.Backups;
using Vessel.Domain.Common;
using Vessel.Domain.Databases;
using Vessel.UnitTests.Application.SecurityHardening;

namespace Vessel.UnitTests.Application.Backups;

/// <summary>DATA-03 / DATA-06 / DATA-07 / DATA-08 / DATA-14: backup runs, schedules, retention, notifications and API rules.</summary>
public sealed class DatabaseBackupServiceTests
{
    private readonly BackupFixture _f = new();

    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Backup_IsSkippedWhileAnotherDatabaseOperationHoldsTheLock()
    {
        DatabaseBackupService backups = _f.Backups();
        BackupExecutionSummary queued = await backups.QueueBackupAsync(_f.Owner.Id, _f.Team.Id, _f.Database.Id,
            cancellationToken: Token);
        await using DistributedLockHandle? held = await _f.Locks.TryAcquireAsync(
            DatabaseOperationLocks.KeyFor(_f.Database.Id), TimeSpan.FromMinutes(5), TimeSpan.Zero, Token);
        Assert.NotNull(held);

        BackupExecutionSummary result = await backups.RunBackupExecutionAsync(new BackupExecutionId(queued.Id), Token);

        Assert.Equal(BackupExecutionStatus.Skipped, result.Status);
        Assert.Contains("another lifecycle, backup or restore", result.FailureReason, StringComparison.Ordinal);
        Assert.Empty(_f.Provider.Requests);
        Assert.Empty(_f.Notifications.Published);
        Assert.Contains(_f.Audit.Records, record => record.Action == AuditActions.BackupSkipped);
    }

    [Fact]
    public async Task Backup_IsSkippedWhenTheDatabaseIsNotRunning()
    {
        _f.Database.MarkStopped(_f.Now);
        DatabaseBackupService backups = _f.Backups();
        BackupExecutionSummary queued = await backups.QueueBackupAsync(_f.Owner.Id, _f.Team.Id, _f.Database.Id,
            cancellationToken: Token);

        BackupExecutionSummary result = await backups.RunBackupExecutionAsync(new BackupExecutionId(queued.Id), Token);

        Assert.Equal(BackupExecutionStatus.Skipped, result.Status);
        Assert.Empty(_f.Provider.Requests);
    }

    [Fact]
    public async Task Backup_ReleasesTheLockSoTheNextOperationCanRun()
    {
        DatabaseBackupService backups = _f.Backups();
        BackupExecutionSummary queued = await backups.QueueBackupAsync(_f.Owner.Id, _f.Team.Id, _f.Database.Id,
            cancellationToken: Token);

        await backups.RunBackupExecutionAsync(new BackupExecutionId(queued.Id), Token);

        await using DistributedLockHandle? next = await _f.Locks.TryAcquireAsync(
            DatabaseOperationLocks.KeyFor(_f.Database.Id), TimeSpan.FromMinutes(1), TimeSpan.Zero, Token);
        Assert.NotNull(next);
    }

    [Fact]
    public async Task ScheduledBackup_StoresLocalCopyAndOneReplicaPerDestination()
    {
        BackupDestination destination = await _f.AddDestinationAsync();
        DatabaseBackupService backups = _f.Backups();
        BackupScheduleSummary schedule = await backups.CreateScheduleAsync(_f.Owner.Id, _f.Team.Id,
            new CreateBackupScheduleRequest(_f.Database.Id.Value, "nightly", "0 2 * * *", DumpAll: false,
                Databases: ["app"], DestinationIds: [destination.Id.Value]), Token);
        Assert.Equal(BackupStorageKind.ObjectStorage, schedule.StorageKind);
        // NTF-08: schedules are dispatched by the scheduled job ledger, never by a per-schedule Hangfire job.
        Assert.False(_f.Jobs.Recurring.ContainsKey(DatabaseBackupService.RecurringJobId(new BackupScheduleId(schedule.Id))));

        IReadOnlyList<BackupExecutionSummary> results =
            await backups.RunScheduledBackupAsync(new BackupScheduleId(schedule.Id), Token);

        BackupExecutionSummary result = Assert.Single(results);
        Assert.Equal(BackupExecutionStatus.Succeeded, result.Status);
        Assert.Equal("app", result.DatabaseName);
        Assert.Equal("app", Assert.Single(_f.Provider.Requests).DatabaseName);
        Assert.True(result.HasLocalCopy);
        Assert.Single(_f.Storage.LocalStorage.Objects);
        Assert.Contains(_f.Storage.Remote.Objects.Keys, key => key.Bucket == "vessel-backups");
        Assert.True(Assert.Single(result.Replicas!).Uploaded);
        Assert.Equal("AKIA", Assert.Single(_f.Storage.Connections).AccessKeyId);
        Assert.Contains(_f.Notifications.Published,
            item => item.Notification.EventType == NotificationEventTypes.BackupSucceeded);
    }

    [Fact]
    public async Task ScheduledBackup_AllDatabasesExpandsToOneExecutionPerPostgresDatabase()
    {
        _f.Provider.Databases = ["app", "postgres", "bad name"];
        DatabaseBackupService backups = _f.Backups();
        BackupScheduleSummary schedule = await backups.CreateScheduleAsync(_f.Owner.Id, _f.Team.Id,
            new CreateBackupScheduleRequest(_f.Database.Id.Value, "nightly", "0 2 * * *"), Token);

        IReadOnlyList<BackupExecutionSummary> results =
            await backups.RunScheduledBackupAsync(new BackupScheduleId(schedule.Id), Token);

        Assert.Equal(["app", "postgres"], results.Select(result => result.DatabaseName).ToArray());
        Assert.All(results, result => Assert.Equal(BackupExecutionStatus.Succeeded, result.Status));
    }

    [Fact]
    public async Task RemoteOnlySchedule_RequiresADestination()
    {
        DatabaseBackupService backups = _f.Backups();

        await Assert.ThrowsAsync<DomainException>(() => backups.CreateScheduleAsync(_f.Owner.Id, _f.Team.Id,
            new CreateBackupScheduleRequest(_f.Database.Id.Value, "remote", "0 2 * * *", DisableLocalCopy: true),
            Token));
    }

    [Fact]
    public async Task Schedule_RejectsAnotherTeamsDestination()
    {
        BackupDestination foreign = await _f.AddDestinationAsync("foreign", _f.OtherTeam);
        DatabaseBackupService backups = _f.Backups();

        DomainException error = await Assert.ThrowsAsync<DomainException>(() => backups.CreateScheduleAsync(
            _f.Owner.Id, _f.Team.Id,
            new CreateBackupScheduleRequest(_f.Database.Id.Value, "nightly", "0 2 * * *",
                DestinationIds: [foreign.Id.Value]), Token));
        Assert.Equal("Backup destination was not found.", error.Message);
    }

    [Fact]
    public async Task DisabledSchedule_NeverFiresAndItsRecurringJobIsRemoved()
    {
        DatabaseBackupService backups = _f.Backups();
        BackupScheduleSummary schedule = await backups.CreateScheduleAsync(_f.Owner.Id, _f.Team.Id,
            new CreateBackupScheduleRequest(_f.Database.Id.Value, "nightly", "0 2 * * *"), Token);
        var jobId = DatabaseBackupService.RecurringJobId(new BackupScheduleId(schedule.Id));

        BackupScheduleSummary disabled = await backups.UpdateScheduleAsync(_f.Owner.Id, _f.Team.Id,
            new BackupScheduleId(schedule.Id), new UpdateBackupScheduleRequest(Enabled: false), Token);
        IReadOnlyList<BackupExecutionSummary> results =
            await backups.RunScheduledBackupAsync(new BackupScheduleId(schedule.Id), Token);

        Assert.False(disabled.Enabled);
        Assert.False(_f.Jobs.Recurring.ContainsKey(jobId));
        Assert.Contains(jobId, _f.Jobs.Removed);
        Assert.Empty(results);
        Assert.Empty(_f.Db.ExecutionItems);
    }

    [Fact]
    public async Task DeleteSchedule_RemovesItsExecutionsArtifactsAndRecurringJob()
    {
        DatabaseBackupService backups = _f.Backups();
        BackupScheduleSummary schedule = await backups.CreateScheduleAsync(_f.Owner.Id, _f.Team.Id,
            new CreateBackupScheduleRequest(_f.Database.Id.Value, "nightly", "0 2 * * *", DumpAll: false), Token);
        await backups.RunScheduledBackupAsync(new BackupScheduleId(schedule.Id), Token);
        Assert.Single(_f.Storage.LocalStorage.Objects);

        await backups.DeleteScheduleAsync(_f.Owner.Id, _f.Team.Id, new BackupScheduleId(schedule.Id), true, Token);

        Assert.Empty(_f.Db.ScheduleItems);
        Assert.Empty(_f.Db.ExecutionItems);
        Assert.Empty(_f.Storage.LocalStorage.Objects);
        Assert.Contains(DatabaseBackupService.RecurringJobId(new BackupScheduleId(schedule.Id)), _f.Jobs.Removed);
    }

    [Fact]
    public async Task Member_CannotEditOrDeleteSchedules()
    {
        DatabaseBackupService backups = _f.Backups();
        BackupScheduleSummary schedule = await backups.CreateScheduleAsync(_f.Owner.Id, _f.Team.Id,
            new CreateBackupScheduleRequest(_f.Database.Id.Value, "nightly", "0 2 * * *"), Token);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => backups.UpdateScheduleAsync(_f.Member.Id,
            _f.Team.Id, new BackupScheduleId(schedule.Id), new UpdateBackupScheduleRequest(Enabled: false), Token));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => backups.DeleteScheduleAsync(_f.Member.Id,
            _f.Team.Id, new BackupScheduleId(schedule.Id), false, Token));
        Assert.Single(_f.Db.ScheduleItems);
    }

    [Fact]
    public async Task LocalRetention_PrunesOldestCopiesBeyondTheCount()
    {
        DatabaseBackupService backups = _f.Backups();
        BackupScheduleSummary schedule = await backups.CreateScheduleAsync(_f.Owner.Id, _f.Team.Id,
            new CreateBackupScheduleRequest(_f.Database.Id.Value, "nightly", "0 2 * * *", RetentionCount: 2,
                DumpAll: false), Token);
        for (var run = 0; run < 3; run++)
        {
            await backups.RunScheduledBackupAsync(new BackupScheduleId(schedule.Id), Token);
            _f.Time.Advance(TimeSpan.FromHours(1));
        }

        Assert.Equal(2, _f.Storage.LocalStorage.Objects.Count);
        BackupExecution oldest = _f.Db.ExecutionItems.OrderBy(item => item.CreatedAt).First();
        Assert.Equal(BackupExecutionStatus.Pruned, oldest.Status);
        Assert.False(oldest.HasLocalCopy);
    }

    [Fact]
    public async Task FailedRuns_NotifyOnFirstFailureThenAtMostDaily_AndRecoveryOnce()
    {
        DatabaseBackupService backups = _f.Backups();
        BackupScheduleSummary schedule = await backups.CreateScheduleAsync(_f.Owner.Id, _f.Team.Id,
            new CreateBackupScheduleRequest(_f.Database.Id.Value, "hourly", "0 * * * *", DumpAll: false), Token);
        var id = new BackupScheduleId(schedule.Id);
        _f.Provider.Failure = new InvalidOperationException("pg_dump failed with exit code 1.");

        await backups.RunScheduledBackupAsync(id, Token);
        _f.Time.Advance(TimeSpan.FromHours(1));
        await backups.RunScheduledBackupAsync(id, Token);
        Assert.Single(_f.Notifications.Published, item => item.Notification.EventType == NotificationEventTypes.BackupFailed);

        _f.Time.Advance(TimeSpan.FromDays(1));
        await backups.RunScheduledBackupAsync(id, Token);
        Assert.Equal(2, _f.Notifications.Published.Count(item =>
            item.Notification.EventType == NotificationEventTypes.BackupFailed));

        _f.Provider.Failure = null;
        await backups.RunScheduledBackupAsync(id, Token);
        await backups.RunScheduledBackupAsync(id, Token);
        Assert.Single(_f.Notifications.Published,
            item => item.Notification.EventType == NotificationEventTypes.BackupRecovered);
    }

    [Fact]
    public async Task MissingBackupAlert_IsSentOncePerIdlePeriod()
    {
        DatabaseBackupService backups = _f.Backups();
        await backups.CreateScheduleAsync(_f.Owner.Id, _f.Team.Id,
            new CreateBackupScheduleRequest(_f.Database.Id.Value, "nightly", "0 2 * * *", MissingAlertDays: 2),
            Token);

        Assert.Equal(0, await backups.CheckMissingBackupsAsync(Token));
        _f.Time.Advance(TimeSpan.FromDays(3));
        Assert.Equal(1, await backups.CheckMissingBackupsAsync(Token));
        Assert.Equal(0, await backups.CheckMissingBackupsAsync(Token));
        TeamNotification alert = Assert.Single(_f.Notifications.Published);
        Assert.Equal(NotificationEventTypes.BackupMissing, alert.Notification.EventType);
        Assert.Equal(_f.Team.Id, alert.TeamId);
    }

    [Fact]
    public async Task ExecutionMessages_AreHiddenWithoutSensitiveReadAccess()
    {
        DatabaseBackupService backups = _f.Backups();
        _f.Provider.Failure = new InvalidOperationException("pg_dump: connection to server failed: secret detail");
        BackupExecutionSummary queued = await backups.QueueBackupAsync(_f.Owner.Id, _f.Team.Id, _f.Database.Id,
            cancellationToken: Token);
        await backups.RunBackupExecutionAsync(new BackupExecutionId(queued.Id), Token);

        BackupExecutionSummary forOwner = Assert.Single(backups.ListExecutions(_f.Owner.Id, _f.Team.Id,
            _f.Database.Id, null, 1, 10).Items);
        BackupExecutionSummary forMember = Assert.Single(backups.ListExecutions(_f.Member.Id, _f.Team.Id,
            _f.Database.Id, null, 1, 10).Items);

        Assert.Equal(BackupExecutionStatus.Failed, forOwner.Status);
        Assert.Contains("secret detail", forOwner.FailureReason, StringComparison.Ordinal);
        Assert.False(forOwner.MessageHidden);
        Assert.Null(forMember.FailureReason);
        Assert.True(forMember.MessageHidden);
    }

    [Fact]
    public async Task Download_RequiresSensitiveReadAndStaysInsideTheTeam()
    {
        DatabaseBackupService backups = _f.Backups();
        BackupExecutionSummary queued = await backups.QueueBackupAsync(_f.Owner.Id, _f.Team.Id, _f.Database.Id,
            cancellationToken: Token);
        await backups.RunBackupExecutionAsync(new BackupExecutionId(queued.Id), Token);
        var executionId = new BackupExecutionId(queued.Id);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            backups.OpenDownloadAsync(_f.Member.Id, _f.Team.Id, executionId, Token));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            backups.OpenDownloadAsync(_f.Outsider.Id, _f.OtherTeam.Id, executionId, Token));

        await using BackupArtifactDownload download =
            await backups.OpenDownloadAsync(_f.Owner.Id, _f.Team.Id, executionId, Token);
        using var reader = new StreamReader(download.Content);
        Assert.Equal("dump of default", await reader.ReadToEndAsync(Token));
        Assert.EndsWith(".pgdump", download.FileName, StringComparison.Ordinal);
        Assert.Contains(_f.Audit.Records, record => record.Action == AuditActions.BackupDownloaded);
    }

    [Fact]
    public async Task Download_FallsBackToARemoteReplicaWhenTheLocalCopyIsGone()
    {
        BackupDestination destination = await _f.AddDestinationAsync();
        DatabaseBackupService backups = _f.Backups();
        BackupScheduleSummary schedule = await backups.CreateScheduleAsync(_f.Owner.Id, _f.Team.Id,
            new CreateBackupScheduleRequest(_f.Database.Id.Value, "remote", "0 2 * * *", DumpAll: false,
                DestinationIds: [destination.Id.Value], DisableLocalCopy: true), Token);
        BackupExecutionSummary result =
            Assert.Single(await backups.RunScheduledBackupAsync(new BackupScheduleId(schedule.Id), Token));
        Assert.False(result.HasLocalCopy);
        Assert.Empty(_f.Storage.LocalStorage.Objects);

        await using BackupArtifactDownload download = await backups.OpenDownloadAsync(_f.Owner.Id, _f.Team.Id,
            new BackupExecutionId(result.Id), Token);
        using var reader = new StreamReader(download.Content);
        Assert.Equal("dump of default", await reader.ReadToEndAsync(Token));
    }

    [Fact]
    public async Task DeleteExecution_IsRefusedWhileARestoreUsesIt()
    {
        DatabaseBackupService backups = _f.Backups();
        BackupExecutionSummary queued = await backups.QueueBackupAsync(_f.Owner.Id, _f.Team.Id, _f.Database.Id,
            cancellationToken: Token);
        await backups.RunBackupExecutionAsync(new BackupExecutionId(queued.Id), Token);
        _f.Db.RestoreItems.Add(DatabaseRestore.Queue(_f.Team.Id, new BackupExecutionId(queued.Id), _f.Database.Id,
            _f.Owner.Id, true, false, false, _f.Now));

        await Assert.ThrowsAsync<DatabaseOperationConflictException>(() => backups.DeleteExecutionAsync(_f.Owner.Id,
            _f.Team.Id, new BackupExecutionId(queued.Id), false, Token));
        Assert.Single(_f.Db.ExecutionItems);
    }

    [Fact]
    public async Task LifecycleAction_IsRejectedWithConflictWhileTheDatabaseIsLocked()
    {
        ManagedDatabaseService managed = ManagedDatabases();
        await using DistributedLockHandle? held = await _f.Locks.TryAcquireAsync(
            DatabaseOperationLocks.KeyFor(_f.Database.Id), TimeSpan.FromMinutes(5), TimeSpan.Zero, Token);

        await Assert.ThrowsAsync<DatabaseOperationConflictException>(() => managed.QueueLifecycleActionAsync(
            _f.Owner.Id, _f.Team.Id, _f.Database.Id, DatabaseLifecycleAction.Restart, Token));
        Assert.Empty(_f.Jobs.Enqueued);
    }

    [Fact]
    public async Task LifecycleAction_IsRejectedWhileABackupRuns()
    {
        ManagedDatabaseService managed = ManagedDatabases();
        var running = BackupExecution.Queue(_f.Team.Id, _f.Database.Id, null, BackupStorageKind.Local, _f.Now);
        running.Start(_f.Now);
        _f.Db.ExecutionItems.Add(running);

        await Assert.ThrowsAsync<DatabaseOperationConflictException>(() => managed.QueueLifecycleActionAsync(
            _f.Owner.Id, _f.Team.Id, _f.Database.Id, DatabaseLifecycleAction.Delete, Token));
    }

    [Fact]
    public async Task LifecycleJob_SkipsWithoutMarkingTheDatabaseFailedWhenLocked()
    {
        ManagedDatabaseService managed = ManagedDatabases();
        await using DistributedLockHandle? held = await _f.Locks.TryAcquireAsync(
            DatabaseOperationLocks.KeyFor(_f.Database.Id), TimeSpan.FromMinutes(5), TimeSpan.Zero, Token);

        DatabaseLifecycleResult result =
            await managed.RunLifecycleActionAsync(_f.Database.Id, DatabaseLifecycleAction.Stop, Token);

        Assert.Equal(DatabaseLifecycleState.Running, result.State);
        Assert.Equal(DatabaseLifecycleState.Running, _f.Database.LifecycleState);
        Assert.Contains("skipped", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InspectAction_RecordsTheObservedContainerHealth()
    {
        ManagedDatabaseService managed = ManagedDatabases(new InspectRuntime(
            """[{"State":{"Running":true,"Health":{"Status":"unhealthy"}}}]"""));

        DatabaseLifecycleResult result =
            await managed.RunLifecycleActionAsync(_f.Database.Id, DatabaseLifecycleAction.Inspect, Token);

        Assert.Equal(DatabaseHealthState.Unhealthy, result.HealthState);
        Assert.Equal(DatabaseHealthState.Unhealthy, _f.Database.HealthState);
    }

    private ManagedDatabaseService ManagedDatabases(IContainerRuntimeClient? runtime = null)
    {
        IContainerRuntimeClient client = runtime ?? new CountingContainerRuntime();
        // Service-stack routing is only used when provisioning service templates, never by database lifecycle or
        // backup paths exercised here.
        return new ManagedDatabaseService(_f.Db, _f.Authorization, client, new ThrowingWorkspace(), _f.Jobs,
            _f.Locks, _f.Vault, new ServiceTemplateCatalog(), _f.Audit, _f.Time, serviceRouting: null!,
            new DatabaseHealthProbe(client));
    }

    private sealed class ThrowingWorkspace : IManagedServiceWorkspace
    {
        public Task<ManagedServiceWorkspace> PrepareDatabaseAsync(DatabaseResourceId databaseId,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("no workspace");

        public Task<ManagedServiceWorkspace> PrepareServiceAsync(ServiceResourceId serviceId,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("no workspace");

        public Task WriteTextAsync(string rootDirectory, string relativePath, string contents, bool restrictToOwner,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("no workspace");
    }

    private sealed class InspectRuntime(string inspectJson) : IContainerRuntimeClient
    {
        private readonly CountingContainerRuntime _inner = new();

        public Task<ContainerRuntimeInfo> GetInfoAsync(ContainerRuntimeTarget target,
            CancellationToken cancellationToken = default) => _inner.GetInfoAsync(target, cancellationToken);

        public Task<IReadOnlyList<ContainerSummary>> ListContainersAsync(ContainerRuntimeTarget target, bool all,
            CancellationToken cancellationToken = default) => _inner.ListContainersAsync(target, all, cancellationToken);

        public Task<string> InspectContainerAsync(ContainerRuntimeTarget target, string containerId,
            CancellationToken cancellationToken = default) => Task.FromResult(inspectJson);

        public Task<IReadOnlyList<ImageSummary>> ListImagesAsync(ContainerRuntimeTarget target,
            CancellationToken cancellationToken = default) => _inner.ListImagesAsync(target, cancellationToken);

        public Task<IReadOnlyList<NetworkSummary>> ListNetworksAsync(ContainerRuntimeTarget target,
            CancellationToken cancellationToken = default) => _inner.ListNetworksAsync(target, cancellationToken);

        public Task<IReadOnlyList<VolumeSummary>> ListVolumesAsync(ContainerRuntimeTarget target,
            CancellationToken cancellationToken = default) => _inner.ListVolumesAsync(target, cancellationToken);

        public Task EnsureNetworkAsync(ContainerRuntimeTarget target, string name,
            IReadOnlyDictionary<string, string> labels, CancellationToken cancellationToken = default) =>
            _inner.EnsureNetworkAsync(target, name, labels, cancellationToken);

        public IAsyncEnumerable<Vessel.Application.Processes.ProcessOutputLine> BuildImageAsync(
            ContainerRuntimeTarget target, DockerBuildCommand command, CancellationToken cancellationToken = default) =>
            _inner.BuildImageAsync(target, command, cancellationToken);

        public IAsyncEnumerable<ContainerEvent> StreamEventsAsync(ContainerRuntimeTarget target,
            CancellationToken cancellationToken = default) => _inner.StreamEventsAsync(target, cancellationToken);

        public IAsyncEnumerable<Vessel.Application.Processes.ProcessOutputLine> RunComposeAsync(
            ContainerRuntimeTarget target, ComposeCommand command, CancellationToken cancellationToken = default) =>
            _inner.RunComposeAsync(target, command, cancellationToken);
    }
}
