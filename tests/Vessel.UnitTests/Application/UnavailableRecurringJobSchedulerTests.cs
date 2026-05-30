using Vessel.Application.Jobs;

namespace Vessel.UnitTests.Application;

public sealed class UnavailableRecurringJobSchedulerTests
{
    [Fact]
    public void AddOrUpdate_WhenHangfireUnavailable_ThrowsDetectableExceptionWithSchedulingIntent()
    {
        var scheduler = new UnavailableRecurringJobScheduler();

        BackgroundJobsUnavailableException exception = Assert.Throws<BackgroundJobsUnavailableException>(() =>
            scheduler.AddOrUpdate<TestJob>(
                "test.recurring",
                job => job.RunAsync(CancellationToken.None),
                "*/5 * * * *"));

        Assert.Equal("test.recurring", exception.RecurringJobId);
        Assert.Contains(nameof(TestJob.RunAsync), exception.MethodCall, StringComparison.Ordinal);
        Assert.Equal("*/5 * * * *", exception.CronExpression);
        Assert.Contains("test.recurring", exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(TestJob.RunAsync), exception.Message, StringComparison.Ordinal);
        Assert.Contains("*/5 * * * *", exception.Message, StringComparison.Ordinal);
    }

    private sealed class TestJob
    {
        public Task RunAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
