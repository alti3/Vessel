namespace Vessel.Application.Monitoring;

public sealed class ServerHealthPollingJob(ServerHealthPollingService pollingService)
{
    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        return pollingService.PollAllAsync(cancellationToken);
    }
}

public static class ServerHealthRecurringJobs
{
    public const string PollAllJobId = "servers.health.poll";
    public const string PollAllCronExpression = "*/5 * * * *";

    public static void Register(Jobs.IRecurringJobScheduler scheduler)
    {
        scheduler.AddOrUpdate<ServerHealthPollingJob>(
            PollAllJobId,
            job => job.RunAsync(CancellationToken.None),
            PollAllCronExpression);
    }
}
