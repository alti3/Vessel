using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vessel.Application.Operations;
using Vessel.Application.Operations.ControlPlaneBackups;
using Vessel.Application.Operations.Draining;
using Vessel.Application.Operations.Updates;
using Vessel.Application.Operations.Upgrades;

namespace Vessel.Web.Extensions;

/// <summary>Application services of control-plane operations: update policy, backups, upgrades (OPS-01/OPS-02/DATA-13).</summary>
public static class OperationsServiceCollectionExtensions
{
    public static IServiceCollection AddVesselOperationsServices(this IServiceCollection services)
    {
        services.AddScoped<PlatformNotificationPublisher>();
        services.AddScoped<UpdatePolicyService>();
        services.AddScoped<UpdateCheckService>();
        services.TryAddSingleton<IManifestSignatureVerifier, NotConfiguredManifestSignatureVerifier>();
        services.TryAddSingleton<IControlPlaneDatabaseDumper, UnavailableControlPlaneDatabaseDumper>();
        services.AddScoped<ControlPlaneBackupService>();
        services.AddScoped<RunManualControlPlaneBackupJob>();
        services.AddScoped<DrainService>();
        services.AddScoped<IInFlightWorkProbe, DeploymentInFlightWorkProbe>();
        services.AddScoped<UpgradeCoordinator>();
        return services;
    }
}
