using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Vessel.Infrastructure.Operations;

/// <summary>Infrastructure of control-plane operations: pg_dump, version manifest client (OPS-01/OPS-02/DATA-13).</summary>
public static class OperationsInfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddVesselOperationsInfrastructure(this IServiceCollection services,
        IConfiguration configuration)
    {
        return services;
    }
}
