using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vessel.Application.Backups;
using Vessel.Application.Jobs;
using Vessel.Application.Jobs.Scheduling;
using Vessel.Application.Operations;
using Vessel.Application.Operations.Draining;
using Vessel.Application.ScheduledTasks;
using Vessel.Application.Settings;

namespace Vessel.Web.Extensions;

/// <summary>Application services of the scheduled job ledger and user scheduled tasks (NTF-07/NTF-08).</summary>
public static class SchedulingServiceCollectionExtensions
{
    public static IServiceCollection AddVesselSchedulingServices(this IServiceCollection services)
    {
        services.AddScoped<SystemSettingsStore>();
        services.TryAddSingleton<IWorkerHostIdentity, MachineWorkerHostIdentity>();

        // Ledger (NTF-08).
        services.AddScoped<SchedulingSettingsService>();
        services.AddScoped<ScheduledJobDispatcher>();
        services.AddScoped<ScheduledJobRunner>();
        services.AddScoped<ScheduledJobRunQueryService>();
        services.AddScoped<ScheduledJobDispatchJob>();
        services.AddScoped<RunScheduledJobOccurrenceJob>();
        services.AddScoped<IInFlightWorkProbe, ScheduledJobRunsInFlightProbe>();

        // Schedule sources: every kind of Vessel schedule is dispatched by the ledger.
        services.AddScoped<IScheduledJobSource, DatabaseBackupScheduleSource>();
        services.AddScoped<ScheduledTaskRunner>();
        services.AddScoped<IScheduledJobSource>(provider => provider.GetRequiredService<ScheduledTaskRunner>());
        services.AddScoped<IScheduledJobSource, ControlPlaneBackupScheduleSource>();
        services.AddScoped<IScheduledJobSource, UpdateCheckScheduleSource>();

        // Scheduled tasks (NTF-07).
        services.AddScoped<ScheduledTaskOwners>();
        services.AddScoped<ScheduledTaskService>();
        return services;
    }
}
