using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Vessel.Infrastructure.Jobs;

/// <summary>Infrastructure of the scheduled job ledger and user scheduled tasks (NTF-07/NTF-08).</summary>
public static class SchedulingInfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddVesselSchedulingInfrastructure(this IServiceCollection services,
        IConfiguration configuration)
    {
        return services;
    }
}
