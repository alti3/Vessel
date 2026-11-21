using Microsoft.Extensions.DependencyInjection;

namespace Vessel.Web.Extensions;

/// <summary>Application services of control-plane operations: update policy, backups, upgrades (OPS-01/OPS-02/DATA-13).</summary>
public static class OperationsServiceCollectionExtensions
{
    public static IServiceCollection AddVesselOperationsServices(this IServiceCollection services)
    {
        return services;
    }
}
