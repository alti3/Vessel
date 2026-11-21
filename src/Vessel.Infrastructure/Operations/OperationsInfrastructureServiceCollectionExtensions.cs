using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vessel.Application.Operations.ControlPlaneBackups;
using Vessel.Application.Operations.Updates;
using Vessel.Application.OutboundRequests;
using Vessel.Infrastructure.Configuration;
using Vessel.Infrastructure.OutboundRequests;

namespace Vessel.Infrastructure.Operations;

/// <summary>Infrastructure of control-plane operations: pg_dump, version manifest client (OPS-01/OPS-02/DATA-13).</summary>
public static class OperationsInfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddVesselOperationsInfrastructure(this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<ControlPlaneBackupOptions>()
            .Bind(configuration.GetSection(ControlPlaneBackupOptions.SectionName));
        services.TryAddSingleton<IVesselVersionProvider, AssemblyVesselVersionProvider>();

        DatabaseOptions database = configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>()
                                   ?? new DatabaseOptions();
        if (database.Enabled && !string.IsNullOrWhiteSpace(database.ConnectionString))
            services.AddSingleton<IControlPlaneDatabaseDumper, PgDumpControlPlaneDatabaseDumper>();

        // The manifest URL is administrator-supplied: same SSRF guard as notification webhooks.
        services.TryAddSingleton<IVersionManifestClient, HttpVersionManifestClient>();
        services.AddHttpClient(HttpVersionManifestClient.HttpClientName,
                client => client.Timeout = TimeSpan.FromSeconds(20))
            .RemoveAllLoggers()
            .AddHttpMessageHandler<OutboundUrlGuardHandler>()
            .ConfigurePrimaryHttpMessageHandler(provider => GuardedOutboundHttpHandler.Create(
                provider.GetRequiredService<IOutboundUrlPolicy>(),
                provider.GetRequiredService<IHostAddressResolver>()));
        return services;
    }
}
