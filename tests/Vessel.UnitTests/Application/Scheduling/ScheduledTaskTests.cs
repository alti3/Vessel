using Vessel.Application.Jobs.Scheduling;
using Vessel.Application.Notifications;
using Vessel.Application.ScheduledTasks;
using Vessel.Domain;
using Vessel.Domain.ScheduledTasks;
using Vessel.Domain.Scheduling;
using Vessel.Domain.Services;
using Vessel.Domain.Teams;
using Vessel.Domain.Users;
using Vessel.Domain.ValueObjects;

namespace Vessel.UnitTests.Application.Scheduling;

/// <summary>NTF-07: scheduled task CRUD, authorization, container selection, exec argv, failure tracking.</summary>
public sealed class ScheduledTaskTests
{
    private readonly SchedulingFixture _f = new();

    private CancellationToken Token => TestContext.Current.CancellationToken;

    private Guid DatabaseId => _f.B.Database.Id.Value;

    private async Task<ScheduledTaskSummary> CreateDatabaseTaskAsync(string command = "pg_isready -U $PGUSER",
        bool useShell = true, string? container = null)
    {
        return await _f.Tasks().CreateAsync(_f.B.Owner.Id, _f.B.Team.Id, ScheduledTaskOwnerKind.Database, DatabaseId,
            new CreateScheduledTaskRequest("check", command, "*/5 * * * *", container, 120, useShell), Token);
    }

    private async Task<(ScheduledJobRunStatus? Status, ScheduledTaskExecution Execution)> RunNowAsync(Guid taskId)
    {
        ScheduledTaskRunner runner = _f.TaskRunner();
        ScheduledTaskRunQueued queued =
            await _f.Tasks(runner).RunNowAsync(_f.B.Owner.Id, _f.B.Team.Id, new ScheduledTaskId(taskId), Token);
        ScheduledJobRunStatus? status = await _f.Runner(runner).RunAsync(new ScheduledJobRunId(queued.RunId), Token);
        return (status, _f.B.Db.TaskExecutionItems.Last());
    }

    [Fact]
    public async Task Create_RequiresWriteAndDeployOnTheResourceInTheActiveTeam()
    {
        var request = new CreateScheduledTaskRequest("x", "true", "@hourly");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _f.Tasks().CreateAsync(_f.B.Member.Id,
            _f.B.Team.Id, ScheduledTaskOwnerKind.Database, DatabaseId, request, Token));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _f.Tasks().CreateAsync(_f.B.Outsider.Id,
            _f.B.OtherTeam.Id, ScheduledTaskOwnerKind.Database, DatabaseId, request, Token));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _f.Tasks().CreateAsync(_f.B.Owner.Id,
            _f.B.Team.Id, ScheduledTaskOwnerKind.Database, Guid.NewGuid(), request, Token));
        Assert.Empty(_f.B.Db.TaskItems);
    }

    [Fact]
    public async Task HostLocalServer_RequiresAnInstanceAdministrator()
    {
        User admin = User.Create(new DisplayName("admin"), new EmailAddress("admin@example.com"), _f.B.Now);
        _f.B.Db.UserItems.Add(admin);
        _f.B.Team.AddMember(admin.Id, TeamRole.Admin, _f.B.Now);

        UnauthorizedAccessException denied = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _f.Tasks().CreateAsync(admin.Id, _f.B.Team.Id, ScheduledTaskOwnerKind.Database, DatabaseId,
                new CreateScheduledTaskRequest("x", "true", "@hourly"), Token));
        Assert.Contains("instance administrator", denied.Message, StringComparison.Ordinal);

        ScheduledTaskSummary task = await CreateDatabaseTaskAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _f.Tasks().RunNowAsync(admin.Id, _f.B.Team.Id, new ScheduledTaskId(task.Id), Token));
    }

    [Fact]
    public async Task OtherTeams_CannotSeeOrRunATask()
    {
        ScheduledTaskSummary task = await CreateDatabaseTaskAsync();

        Assert.Throws<UnauthorizedAccessException>(() =>
            _f.Tasks().Get(_f.B.Outsider.Id, _f.B.OtherTeam.Id, new ScheduledTaskId(task.Id)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _f.Tasks().RunNowAsync(_f.B.Outsider.Id, _f.B.OtherTeam.Id, new ScheduledTaskId(task.Id), Token));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _f.Tasks().RunNowAsync(_f.B.Member.Id, _f.B.Team.Id, new ScheduledTaskId(task.Id), Token));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _f.Tasks().DeleteAsync(_f.B.Member.Id, _f.B.Team.Id, new ScheduledTaskId(task.Id), Token));
    }

    [Fact]
    public async Task Run_ExecsWithShellAsStructuredArguments_AndRedactsSecrets()
    {
        ScheduledTaskSummary task = await CreateDatabaseTaskAsync("echo $PGPASSWORD; rm -rf /tmp/x");
        _f.Commands.Containers.Add("vessel-db-main\t");
        _f.Commands.OutputLines.Clear();
        _f.Commands.OutputLines.AddRange(["password is db-password", "done"]);

        (ScheduledJobRunStatus? status, ScheduledTaskExecution execution) = await RunNowAsync(task.Id);

        Assert.Equal(ScheduledJobRunStatus.Succeeded, status);
        Assert.Equal(["docker", "exec", "vessel-db-main", "sh", "-c", "echo $PGPASSWORD; rm -rf /tmp/x"],
            Assert.Single(_f.Commands.Streams));
        Assert.Equal(ScheduledTaskExecutionStatus.Succeeded, execution.Status);
        Assert.DoesNotContain("db-password", execution.Output, StringComparison.Ordinal);
        Assert.Contains("<REDACTED>", execution.Output, StringComparison.Ordinal);
        Assert.Equal("vessel-db-main", execution.ContainerName);
        Assert.Equal("worker-1", execution.WorkerHost);
        Assert.All(_f.B.Notifications.Published,
            notification => Assert.DoesNotContain("db-password", notification.Notification.Message,
                StringComparison.Ordinal));
        Assert.All(_f.Realtime.Published, item => Assert.Equal(_f.B.Team.Id.Value.ToString("D"), item.Group.Id));
    }

    [Fact]
    public async Task Run_WithoutShell_SplitsTheCommandIntoArguments()
    {
        ScheduledTaskSummary task = await CreateDatabaseTaskAsync("psql -c 'select 1' --quiet", useShell: false);
        _f.Commands.Containers.Add("vessel-db-main\t");

        await RunNowAsync(task.Id);

        Assert.Equal(["docker", "exec", "vessel-db-main", "psql", "-c", "select 1", "--quiet"],
            Assert.Single(_f.Commands.Streams));
    }

    [Fact]
    public async Task StoppedResource_IsSkippedWithoutCountingAFailure()
    {
        ScheduledTaskSummary task = await CreateDatabaseTaskAsync();
        _f.B.Database.MarkStopped(_f.B.Now);

        (ScheduledJobRunStatus? status, ScheduledTaskExecution execution) = await RunNowAsync(task.Id);

        Assert.Equal(ScheduledJobRunStatus.Skipped, status);
        Assert.Equal(ScheduledTaskExecutionStatus.Skipped, execution.Status);
        Assert.Empty(_f.Commands.Streams);
        Assert.Equal(0, _f.B.Db.TaskItems.Single().ConsecutiveFailures);
        Assert.DoesNotContain(_f.B.Notifications.Published,
            item => item.Notification.EventType == NotificationEventTypes.ScheduledTaskFailed);
    }

    [Fact]
    public async Task Failures_NotifyAndDisableTheTaskAfterTenFailuresOverADay()
    {
        ScheduledTaskSummary task = await CreateDatabaseTaskAsync();
        _f.Commands.Containers.Add("vessel-db-main\t");
        _f.Commands.ExitCode = 3;

        (ScheduledJobRunStatus? status, ScheduledTaskExecution execution) = await RunNowAsync(task.Id);
        Assert.Equal(ScheduledJobRunStatus.Failed, status);
        Assert.Equal(3, execution.ExitCode);
        Assert.Contains("exited with code 3", execution.Message, StringComparison.Ordinal);
        Assert.Contains(_f.B.Notifications.Published,
            item => item.Notification.EventType == NotificationEventTypes.ScheduledTaskFailed);

        for (var i = 0; i < 9; i++)
        {
            _f.B.Time.Advance(TimeSpan.FromHours(3));
            await RunNowAsync(task.Id);
        }

        ScheduledTask stored = _f.B.Db.TaskItems.Single();
        Assert.False(stored.Enabled);
        Assert.NotNull(stored.DisabledReason);

        _f.Commands.ExitCode = 0;
        await _f.Tasks().UpdateAsync(_f.B.Owner.Id, _f.B.Team.Id, stored.Id, new UpdateScheduledTaskRequest(Enabled: true),
            Token);
        (status, _) = await RunNowAsync(task.Id);
        Assert.Equal(ScheduledJobRunStatus.Succeeded, status);
        Assert.Equal(0, stored.ConsecutiveFailures);
    }

    [Fact]
    public async Task ServiceTask_SelectsItsOwnContainerOnly()
    {
        var service = ServiceResource.Create(_f.B.Team.Id, _f.B.Database.EnvironmentId, _f.B.Server.Id,
            new ResourceName("stack"), "template", "1", "{}", _f.B.Now);
        _f.B.Db.ServiceItems.Add(service);
        _f.Commands.Containers.AddRange(["stack-web-1\tweb", "stack-worker-1\tworker"]);
        ScheduledTaskService tasks = _f.Tasks();
        ScheduledTaskSummary ambiguous = await tasks.CreateAsync(_f.B.Owner.Id, _f.B.Team.Id,
            ScheduledTaskOwnerKind.Service, service.Id.Value,
            new CreateScheduledTaskRequest("a", "true", "@hourly"), Token);
        ScheduledTaskSummary foreign = await tasks.CreateAsync(_f.B.Owner.Id, _f.B.Team.Id,
            ScheduledTaskOwnerKind.Service, service.Id.Value,
            new CreateScheduledTaskRequest("b", "true", "@hourly", "vessel-db-main"), Token);
        ScheduledTaskSummary named = await tasks.CreateAsync(_f.B.Owner.Id, _f.B.Team.Id,
            ScheduledTaskOwnerKind.Service, service.Id.Value,
            new CreateScheduledTaskRequest("c", "true", "@hourly", "worker"), Token);

        Assert.Contains("More than one container", (await RunNowAsync(ambiguous.Id)).Execution.Message,
            StringComparison.Ordinal);
        Assert.Contains("No running container named", (await RunNowAsync(foreign.Id)).Execution.Message,
            StringComparison.Ordinal);
        Assert.Equal(ScheduledJobRunStatus.Succeeded, (await RunNowAsync(named.Id)).Status);
        Assert.Equal("stack-worker-1", Assert.Single(_f.Commands.Streams)[2]);
        Assert.Contains(_f.Commands.Runs, run => run.Contains(
            $"label=com.docker.compose.project={$"vessel-svc-{service.Id.Value:N}"[..39]}"));
    }

    [Fact]
    public async Task Executions_HideOutputFromReadOnlyMembers()
    {
        ScheduledTaskSummary task = await CreateDatabaseTaskAsync();
        _f.Commands.Containers.Add("vessel-db-main\t");
        await RunNowAsync(task.Id);

        PagedScheduledTaskExecutions member = _f.Tasks().ListExecutions(_f.B.Member.Id, _f.B.Team.Id,
            new ScheduledTaskId(task.Id), 1, 10);
        PagedScheduledTaskExecutions owner = _f.Tasks().ListExecutions(_f.B.Owner.Id, _f.B.Team.Id,
            new ScheduledTaskId(task.Id), 1, 10);

        Assert.Null(Assert.Single(member.Items).Output);
        Assert.True(member.Items[0].OutputHidden);
        Assert.Equal("ok", Assert.Single(owner.Items).Output);
    }

    [Fact]
    public async Task ScheduledRunsOfADisabledTask_AreSkipped()
    {
        ScheduledTaskSummary task = await CreateDatabaseTaskAsync();
        await _f.Tasks().UpdateAsync(_f.B.Owner.Id, _f.B.Team.Id, new ScheduledTaskId(task.Id),
            new UpdateScheduledTaskRequest(Enabled: false), Token);
        ScheduledTaskRunner runner = _f.TaskRunner();
        Assert.Empty(runner.ListActiveSchedules());
        var run = ScheduledJobRun.CreateDue(ScheduledJobKind.ScheduledTask, task.Id, _f.B.Team.Id, _f.B.Now,
            ScheduledJobTrigger.Scheduled, 300, _f.B.Now);
        _f.B.Db.RunItems.Add(run);

        Assert.Equal(ScheduledJobRunStatus.Skipped, await _f.Runner(runner).RunAsync(run.Id, Token));
        Assert.Empty(_f.Commands.Streams);
    }
}
