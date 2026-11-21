using Vessel.Application.Jobs;
using Vessel.Application.Proxy.Certificates;

namespace Vessel.Application.Proxy;

/// <summary>
///     Recurring proxy jobs: the certificate sync from <c>acme.json</c> (replaces the former Vessel-side renewal job;
///     Traefik renews certificates itself) and the proxy configuration history cleanup.
/// </summary>
public static class CertificateRecurringJobs
{
    /// <summary>
    ///     Keeps the id of the former renewal job so registration replaces its stored definition (which referenced a
    ///     job type that no longer exists) instead of leaving an orphan that fails on every run.
    /// </summary>
    public const string SyncJobId = "certificates.renew-due";
    public const string SyncCronExpression = "*/30 * * * *";

    public const string HistoryCleanupJobId = "proxy.configuration-history-cleanup";
    public const string HistoryCleanupCronExpression = "40 3 * * *";

    public static void Register(IRecurringJobScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(scheduler);

        scheduler.AddOrUpdate<CertificateSyncJob>(
            SyncJobId,
            job => job.RunAsync(CancellationToken.None),
            SyncCronExpression);
        scheduler.AddOrUpdate<ProxyConfigurationHistoryCleanupJob>(
            HistoryCleanupJobId,
            job => job.RunAsync(CancellationToken.None),
            HistoryCleanupCronExpression);
    }
}
