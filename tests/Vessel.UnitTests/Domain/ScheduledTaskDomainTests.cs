using Vessel.Domain;
using Vessel.Domain.Common;
using Vessel.Domain.ScheduledTasks;
using Vessel.Domain.Scheduling;

namespace Vessel.UnitTests.Domain;

/// <summary>NTF-07/NTF-08 domain rules: failure tracking, auto-disable, argument splitting, claim-once runs.</summary>
public sealed class ScheduledTaskDomainTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private static ScheduledTask NewTask(string command = "php artisan schedule:run", bool useShell = true)
    {
        return ScheduledTask.Create(new TeamId(Guid.NewGuid()), ScheduledTaskOwnerKind.Application, Guid.NewGuid(),
            "scheduler", command, "* * * * *", null, null, 300, useShell, true, null, Start);
    }

    [Fact]
    public void TenQuickFailures_DoNotDisableTheTask()
    {
        ScheduledTask task = NewTask();
        for (var i = 0; i < 15; i++) Assert.False(task.RecordFailure(Start.AddMinutes(i)));

        Assert.True(task.Enabled);
        Assert.Equal(15, task.ConsecutiveFailures);
        Assert.Equal(Start, task.FailingSince);
    }

    [Fact]
    public void TenFailuresOverTwentyFourHours_DisableTheTaskOnceWithAReason()
    {
        ScheduledTask task = NewTask();
        for (var i = 0; i < 9; i++) task.RecordFailure(Start.AddHours(i));
        Assert.True(task.Enabled);

        Assert.True(task.RecordFailure(Start.AddHours(25)));
        Assert.False(task.Enabled);
        Assert.Contains("10 failed runs", task.DisabledReason, StringComparison.Ordinal);
        Assert.False(task.RecordFailure(Start.AddHours(26)));
    }

    [Fact]
    public void Success_EndsTheStreak_AndEnablingAgainStartsANewOne()
    {
        ScheduledTask task = NewTask();
        task.RecordFailure(Start);
        Assert.True(task.RecordSuccess(Start.AddMinutes(1)));
        Assert.False(task.RecordSuccess(Start.AddMinutes(2)));
        Assert.Equal(0, task.ConsecutiveFailures);

        for (var i = 0; i < 10; i++) task.RecordFailure(Start.AddHours(i * 3));
        Assert.False(task.Enabled);
        task.Enable(Start.AddDays(3));
        Assert.True(task.Enabled);
        Assert.Equal(0, task.ConsecutiveFailures);
        Assert.Null(task.FailingSince);
        Assert.Null(task.DisabledReason);
    }

    [Theory]
    [InlineData("--name=x")]
    [InlineData("web;rm")]
    [InlineData("a b")]
    public void ContainerName_MustBeAPlainDockerName(string container)
    {
        Assert.Throws<DomainException>(() => ScheduledTask.Create(new TeamId(Guid.NewGuid()),
            ScheduledTaskOwnerKind.Service, Guid.NewGuid(), "x", "true", "@hourly", null, container, 300, true, true,
            null, Start));
    }

    [Fact]
    public void InvalidCronOrTimeZone_IsRejected()
    {
        Assert.Throws<DomainException>(() => ScheduledTask.Create(new TeamId(Guid.NewGuid()),
            ScheduledTaskOwnerKind.Application, Guid.NewGuid(), "x", "true", "61 * * * *", null, null, 300, true,
            true, null, Start));
        Assert.Throws<DomainException>(() => ScheduledTask.Create(new TeamId(Guid.NewGuid()),
            ScheduledTaskOwnerKind.Application, Guid.NewGuid(), "x", "true", "* * * * *", "Nowhere/City", null, 300,
            true, true, null, Start));
    }

    [Theory]
    [InlineData("php artisan migrate --force", new[] { "php", "artisan", "migrate", "--force" })]
    [InlineData("echo 'a b' \"c d\"", new[] { "echo", "a b", "c d" })]
    [InlineData("echo $HOME | cat", new[] { "echo", "$HOME", "|", "cat" })]
    [InlineData("say \"q\\\"uote\" back\\ slash", new[] { "say", "q\"uote", "back slash" })]
    [InlineData("one \\\ntwo", new[] { "one", "two" })]
    [InlineData("empty '' arg", new[] { "empty", "", "arg" })]
    public void CommandLine_SplitsLikeAShellWithoutExpansion(string command, string[] expected)
    {
        Assert.Equal(expected, ScheduledTaskCommandLine.Split(command));
    }

    [Fact]
    public void CommandLine_RejectsAnUnclosedQuote()
    {
        Assert.Throws<DomainException>(() => ScheduledTaskCommandLine.Split("echo 'oops"));
        Assert.Throws<DomainException>(() => NewTask("echo 'oops", useShell: false));
    }

    [Fact]
    public void Run_IsClaimedOnlyOnce()
    {
        ScheduledJobRun run = ScheduledJobRun.CreateDue(ScheduledJobKind.ScheduledTask, Guid.NewGuid(), null, Start,
            ScheduledJobTrigger.Scheduled, 300, Start);

        Assert.True(run.TryClaim(Guid.NewGuid(), "host-a", Start));
        Assert.False(run.TryClaim(Guid.NewGuid(), "host-b", Start));
        Assert.Equal("host-a", run.WorkerHost);
        run.Succeed(null, null, Start.AddSeconds(5));
        run.Fail("late", null, Start.AddSeconds(6));
        Assert.Equal(ScheduledJobRunStatus.Succeeded, run.Status);
    }

    [Fact]
    public void Execution_OutputIsBounded()
    {
        var execution = ScheduledTaskExecution.Start(ScheduledTaskId.New(), new TeamId(Guid.NewGuid()), null,
            ScheduledJobTrigger.Manual, "host", Start);
        execution.AppendOutput(new string('x', ScheduledTaskExecution.MaxOutputLength - 10));
        Assert.False(execution.AppendOutput(new string('y', 100)));
        Assert.True(execution.OutputTruncated);
        Assert.EndsWith(ScheduledTaskExecution.TruncationNotice, execution.Output, StringComparison.Ordinal);
        Assert.False(execution.AppendOutput("more"));
    }
}
