using Hangfire;
using Hangfire.Common;
using Vessel.Application.Deployments;
using Vessel.Application.Jobs.Scheduling;
using Vessel.Application.ManagedServices;
using Vessel.Infrastructure.Jobs;

namespace Vessel.IntegrationTests.Jobs;

/// <summary>NTF-08: Hangfire never retries ledger jobs into duplicate executions; other jobs keep the default.</summary>
public sealed class RetryPolicyFilterTests
{
    [Fact]
    public void LedgerJobs_AreNeverRetried_OtherJobsKeepTheDefault()
    {
        Assert.Equal(0, VesselRetryPolicyFilter.AttemptsFor(typeof(RunScheduledJobOccurrenceJob),
            typeof(RunScheduledJobOccurrenceJob).GetMethod(nameof(RunScheduledJobOccurrenceJob.RunAsync))));
        Assert.Equal(0, VesselRetryPolicyFilter.AttemptsFor(typeof(ScheduledJobDispatchJob), null));
        Assert.Equal(0, VesselRetryPolicyFilter.AttemptsFor(typeof(RunBackupJob), null));
        Assert.Equal(VesselRetryPolicyFilter.DefaultAttempts,
            VesselRetryPolicyFilter.AttemptsFor(typeof(RunDeploymentJob), null));
    }

    [Fact]
    public void Install_ReplacesTheGlobalAutomaticRetryFilter()
    {
        var filters = new JobFilterCollection();
        filters.Add(new AutomaticRetryAttribute());

        VesselRetryPolicyFilter.Install(filters);
        VesselRetryPolicyFilter.Install(filters);

        JobFilter filter = Assert.Single(filters);
        Assert.IsType<VesselRetryPolicyFilter>(filter.Instance);
    }
}
