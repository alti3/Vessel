using Vessel.Application.Backups;
using Vessel.Application.Jobs.Scheduling;
using Vessel.Application.Notifications;
using Vessel.Application.Operations;
using Vessel.Domain;
using Vessel.Domain.Backups;
using Vessel.Domain.Scheduling;

namespace Vessel.UnitTests.Application.Scheduling;

/// <summary>NTF-08: durable runs, missed-run detection and catch-up, stale/interrupted runs, claim-once execution.</summary>
public sealed class ScheduledJobLedgerTests
{
    private readonly SchedulingFixture _f = new();

    private CancellationToken Token => TestContext.Current.CancellationToken;

    private List<ScheduledJobRun> Runs => _f.B.Db.RunItems;

    [Fact]
    public async Task Dispatch_RecordsEachOccurrenceOnce()
    {
        var source = new FakeSource(ScheduledJobKind.ScheduledTask, "* * * * *");
        ScheduledJobDispatcher dispatcher = _f.Dispatcher(source);

        ScheduledJobDispatchResult first = await dispatcher.DispatchAsync(Token);
        ScheduledJobDispatchResult again = await dispatcher.DispatchAsync(Token);

        Assert.Equal(1, first.Enqueued);
        Assert.Equal(0, again.Enqueued);
        ScheduledJobRun run = Assert.Single(Runs);
        Assert.Equal(ScheduledJobRunStatus.Due, run.Status);
        Assert.Equal(_f.B.Now, run.ScheduledFor);
        Assert.Equal(nameof(RunScheduledJobOccurrenceJob), Assert.Single(_f.B.Jobs.Enqueued));

        _f.B.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, (await dispatcher.DispatchAsync(Token)).Enqueued);
        Assert.Equal(2, Runs.Count);
    }

    [Fact]
    public async Task Downtime_WithSkipPolicy_RecordsOneMissedRunAndNotifies()
    {
        var source = new FakeSource(ScheduledJobKind.DatabaseBackup, "0 * * * *", MissedRunPolicy.Skip)
        {
            TeamId = _f.B.Team.Id
        };
        // History: the 08:00 occurrence ran; the control plane was down from 08:30 to 12:20.
        Runs.Add(ScheduledJobRun.CreateDue(source.Kind, source.ScheduleId, source.TeamId,
            _f.B.Now.AddHours(-4), ScheduledJobTrigger.Scheduled, 300, _f.B.Now.AddHours(-4)));
        _f.B.Time.Advance(TimeSpan.FromMinutes(20));

        ScheduledJobDispatchResult result = await _f.Dispatcher(source).DispatchAsync(Token);

        Assert.Equal(0, result.Enqueued);
        ScheduledJobRun missed = Assert.Single(Runs, run => run.Status == ScheduledJobRunStatus.Missed);
        Assert.Contains("4 occurrences", missed.Message, StringComparison.Ordinal);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero), missed.ScheduledFor);
        TeamNotification notification = Assert.Single(_f.B.Notifications.Published);
        Assert.Equal(NotificationEventTypes.ScheduledJobMissed, notification.Notification.EventType);
        Assert.Equal(_f.B.Team.Id, notification.TeamId);

        // The next pass does not report the same downtime again.
        await _f.Dispatcher(source).DispatchAsync(Token);
        Assert.Single(Runs, run => run.Status == ScheduledJobRunStatus.Missed);
    }

    [Fact]
    public async Task Downtime_WithRunOncePolicy_RunsTheNewestMissedOccurrenceOnce()
    {
        var source = new FakeSource(ScheduledJobKind.DatabaseBackup, "0 * * * *", MissedRunPolicy.RunOnce);
        Runs.Add(ScheduledJobRun.CreateDue(source.Kind, source.ScheduleId, null, _f.B.Now.AddHours(-3),
            ScheduledJobTrigger.Scheduled, 300, _f.B.Now.AddHours(-3)));
        _f.B.Time.Advance(TimeSpan.FromMinutes(30));

        ScheduledJobDispatchResult result = await _f.Dispatcher(source).DispatchAsync(Token);

        Assert.Equal(1, result.Enqueued);
        ScheduledJobRun catchUp = Assert.Single(Runs, run => run.Trigger == ScheduledJobTrigger.CatchUp);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero), catchUp.ScheduledFor);
        ScheduledJobRun missed = Assert.Single(Runs, run => run.Status == ScheduledJobRunStatus.Missed);
        Assert.Contains("2 occurrences", missed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OccurrencesInsideTheCatchUpWindow_RunOnlyTheNewest()
    {
        var source = new FakeSource(ScheduledJobKind.ScheduledTask, "* * * * *");
        Runs.Add(ScheduledJobRun.CreateDue(source.Kind, source.ScheduleId, null, _f.B.Now,
            ScheduledJobTrigger.Scheduled, 300, _f.B.Now));
        _f.B.Time.Advance(TimeSpan.FromMinutes(5));

        ScheduledJobDispatchResult result = await _f.Dispatcher(source).DispatchAsync(Token);

        Assert.Equal(1, result.Enqueued);
        Assert.Equal(0, result.Missed);
        Assert.Contains(Runs, run => run.ScheduledFor == _f.B.Now && run.Status == ScheduledJobRunStatus.Due);
    }

    [Fact]
    public async Task StaleDueRuns_AreRequeuedOnceForBackupsAndMissedForTasks()
    {
        var backup = ScheduledJobRun.CreateDue(ScheduledJobKind.DatabaseBackup, Guid.NewGuid(), null,
            _f.B.Now, ScheduledJobTrigger.Scheduled, 300, _f.B.Now);
        backup.MarkEnqueued(_f.B.Now);
        var task = ScheduledJobRun.CreateDue(ScheduledJobKind.ScheduledTask, Guid.NewGuid(), null, _f.B.Now,
            ScheduledJobTrigger.Scheduled, 300, _f.B.Now);
        task.MarkEnqueued(_f.B.Now);
        Runs.AddRange([backup, task]);
        _f.B.Time.Advance(TimeSpan.FromMinutes(61));

        ScheduledJobDispatchResult result = await _f.Dispatcher().DispatchAsync(Token);

        Assert.Equal(1, result.Requeued);
        Assert.Equal(ScheduledJobRunStatus.Due, backup.Status);
        Assert.Equal(2, backup.EnqueueCount);
        Assert.Equal(ScheduledJobRunStatus.Missed, task.Status);

        _f.B.Time.Advance(TimeSpan.FromMinutes(61));
        await _f.Dispatcher().DispatchAsync(Token);
        Assert.Equal(ScheduledJobRunStatus.Missed, backup.Status);
    }

    [Fact]
    public async Task InterruptedRuns_AreFailedAndNeverRunAgain()
    {
        var source = new FakeSource(ScheduledJobKind.ScheduledTask, "0 0 1 1 *");
        var run = ScheduledJobRun.CreateDue(source.Kind, source.ScheduleId, null, _f.B.Now,
            ScheduledJobTrigger.Scheduled, 300, _f.B.Now);
        run.TryClaim(Guid.NewGuid(), "dead-host", _f.B.Now);
        Runs.Add(run);
        _f.B.Time.Advance(TimeSpan.FromMinutes(25));

        ScheduledJobDispatchResult result = await _f.Dispatcher(source).DispatchAsync(Token);

        Assert.Equal(1, result.Interrupted);
        Assert.Equal(ScheduledJobRunStatus.Failed, run.Status);
        Assert.Same(run, Assert.Single(source.Interrupted));
        Assert.Equal(ScheduledJobRunStatus.Failed, await _f.Runner(source).RunAsync(run.Id, Token));
        Assert.Empty(source.Executed);
    }

    [Fact]
    public async Task Runner_ExecutesARunOnce_RecordingTheWorkerHost()
    {
        var source = new FakeSource(ScheduledJobKind.ScheduledTask, "* * * * *");
        await _f.Dispatcher(source).DispatchAsync(Token);
        ScheduledJobRun run = Assert.Single(Runs);

        Assert.Equal(ScheduledJobRunStatus.Succeeded, await _f.Runner(source).RunAsync(run.Id, Token));
        // A duplicate delivery or a retry of the same job does nothing.
        Assert.Equal(ScheduledJobRunStatus.Succeeded, await _f.Runner(source).RunAsync(run.Id, Token));
        Assert.Single(source.Executed);
        Assert.Equal("worker-1", run.WorkerHost);
    }

    [Fact]
    public async Task Runner_LosingTheClaimRace_DoesNotRun()
    {
        var source = new FakeSource(ScheduledJobKind.ScheduledTask, "* * * * *");
        await _f.Dispatcher(source).DispatchAsync(Token);
        _f.B.Db.FailNextSave = new InvalidOperationException("concurrency conflict");

        Assert.Null(await _f.Runner(source).RunAsync(Assert.Single(Runs).Id, Token));
        Assert.Empty(source.Executed);
    }

    [Fact]
    public async Task Runner_SourceExceptions_BecomeAFailedRunWithoutRethrowing()
    {
        var source = new FakeSource(ScheduledJobKind.ScheduledTask, "* * * * *")
        {
            Behavior = _ => throw new InvalidOperationException("boom")
        };
        await _f.Dispatcher(source).DispatchAsync(Token);
        ScheduledJobRun run = Assert.Single(Runs);

        Assert.Equal(ScheduledJobRunStatus.Failed, await _f.Runner(source).RunAsync(run.Id, Token));
        Assert.Equal("boom", run.Message);
    }

    [Fact]
    public async Task ScheduledBackup_RunViaTheLedger_CreatesItsExecutionsOnlyOnce()
    {
        DatabaseBackupService backups = _f.B.Backups();
        BackupScheduleSummary schedule = await backups.CreateScheduleAsync(_f.B.Owner.Id, _f.B.Team.Id,
            new CreateBackupScheduleRequest(_f.B.Database.Id.Value, "hourly", "* * * * *", TimeZone: "Europe/Berlin"),
            Token);
        Assert.Equal("Europe/Berlin", schedule.TimeZone);
        var source = new DatabaseBackupScheduleSource(_f.B.Db, backups);
        _f.B.Time.Advance(TimeSpan.FromMinutes(1));
        await _f.Dispatcher(source).DispatchAsync(Token);
        ScheduledJobRun run = Assert.Single(Runs);
        Assert.Equal(ScheduledJobKind.DatabaseBackup, run.Kind);

        await _f.Runner(source).RunAsync(run.Id, Token);
        await _f.Runner(source).RunAsync(run.Id, Token);

        Assert.Single(_f.B.Db.ExecutionItems);
        Assert.Equal(ScheduledJobRunStatus.Succeeded, run.Status);
        Assert.Equal(_f.B.Db.ExecutionItems[0].Id.Value, run.ExecutionId);
    }

    [Fact]
    public async Task LegacyRecurringBackupJob_OnlyRemovesItself()
    {
        DatabaseBackupService backups = _f.B.Backups();
        BackupScheduleSummary schedule = await backups.CreateScheduleAsync(_f.B.Owner.Id, _f.B.Team.Id,
            new CreateBackupScheduleRequest(_f.B.Database.Id.Value, "nightly", "0 2 * * *"), Token);

        await new Vessel.Application.ManagedServices.RunBackupJob(backups).RunAsync(schedule.Id, Token);

        Assert.Empty(_f.B.Db.ExecutionItems);
        Assert.Contains(DatabaseBackupService.RecurringJobId(new BackupScheduleId(schedule.Id)), _f.B.Jobs.Removed);
    }

    [Fact]
    public async Task SchedulingSettings_RequireAnInstanceAdministrator()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _f.Settings.UpdateAsync(_f.B.Member.Id,
            new UpdateSchedulingSettingsRequest("Europe/Berlin", MissedRunPolicy.RunOnce), Token));
        Assert.Throws<UnauthorizedAccessException>(() => _f.Settings.Get(_f.B.Member.Id));

        SchedulingSettings updated = await _f.Settings.UpdateAsync(_f.B.Owner.Id,
            new UpdateSchedulingSettingsRequest("Europe/Berlin", MissedRunPolicy.RunOnce), Token);
        Assert.Equal(MissedRunPolicy.RunOnce, _f.Settings.Current().MissedRunPolicy);
        Assert.Equal("Europe/Berlin", updated.TimeZone);
        await Assert.ThrowsAsync<Vessel.Domain.Common.DomainException>(() => _f.Settings.UpdateAsync(_f.B.Owner.Id,
            new UpdateSchedulingSettingsRequest("Nowhere/City", null), Token));
    }

    [Fact]
    public void InstanceSchedules_AreOffUntilConfigured()
    {
        var updateSource = new UpdateCheckScheduleSource(_f.Store, null!);
        Assert.Empty(updateSource.ListActiveSchedules());
    }
}
