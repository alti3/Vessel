using Vessel.Application.Jobs;

namespace Vessel.Application.Proxy;

public static class CertificateRecurringJobs
{
    public const string RenewDueJobId = "certificates.renew-due";
    public const string RenewDueCronExpression = "*/15 * * * *";

    public static void Register(IRecurringJobScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(scheduler);

        scheduler.AddOrUpdate<CertificateRenewalJob>(
            RenewDueJobId,
            job => job.RunAsync(CancellationToken.None),
            RenewDueCronExpression);
    }
}
