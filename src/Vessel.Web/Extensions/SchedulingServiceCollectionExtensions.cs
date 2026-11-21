using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vessel.Application.Jobs;
using Vessel.Application.Settings;

namespace Vessel.Web.Extensions;

/// <summary>Application services of the scheduled job ledger and user scheduled tasks (NTF-07/NTF-08).</summary>
public static class SchedulingServiceCollectionExtensions
{
    public static IServiceCollection AddVesselSchedulingServices(this IServiceCollection services)
    {
        services.AddScoped<SystemSettingsStore>();
        services.TryAddSingleton<IWorkerHostIdentity, MachineWorkerHostIdentity>();
        return services;
    }
}
