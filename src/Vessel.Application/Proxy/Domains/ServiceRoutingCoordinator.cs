using Vessel.Application.Deployments;
using Vessel.Application.Docker;
using Vessel.Application.Persistence;
using Vessel.Domain;
using Vessel.Domain.Servers;
using Vessel.Domain.Services;

namespace Vessel.Application.Proxy.Domains;

/// <summary>Result of preparing a service-template stack for routing (log-safe message, never secrets).</summary>
public sealed record ServiceRoutingPreparation(string ComposeYaml, bool Routed, string Message);

/// <summary>
///     Makes service-template resources routable: before the stack starts, the Compose services that have domains join
///     the server network under per-service upstream aliases and the proxy is attached; after it starts, the server's
///     routes are applied. Services without domains are left untouched.
/// </summary>
public sealed class ServiceRoutingCoordinator(
    IVesselDbContext dbContext,
    IComposeDocumentBuilder composeDocuments,
    ProxyLifecycleService proxyLifecycle,
    ProxyConfigurationService proxyConfigurations)
{
    public async Task<ServiceRoutingPreparation> PrepareAsync(
        ServiceResource service,
        Server server,
        string composeYaml,
        Func<string, CancellationToken, Task> ensureNetwork,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(ensureNetwork);
        var aliases = dbContext.ServiceDomains
            .Where(domain => domain.ServiceResourceId == service.Id)
            .Select(domain => domain.ComposeService)
            .AsEnumerable()
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(name => name, name => ProxyNaming.ServiceUpstreamAlias(service.Id, name),
                StringComparer.Ordinal);
        if (aliases.Count == 0 || !proxyLifecycle.IsRoutingEnabled(server.Id))
            return new ServiceRoutingPreparation(composeYaml, false, "Service has no routed domains.");

        var network = ProxyNaming.ServerNetwork(server.Id);
        var yaml = composeDocuments.AttachServicesToNetwork(composeYaml, network, aliases);
        await ensureNetwork(network, cancellationToken);
        ProxyEnsureResult proxy = await proxyLifecycle.EnsureForDeploymentAsync(server.Id, network, cancellationToken);
        return new ServiceRoutingPreparation(yaml, true, proxy.Message);
    }

    /// <summary>Publishes the server's routes (including this service's) after the stack is up.</summary>
    public async Task PublishRoutesAsync(ServiceResource service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (!dbContext.ServiceDomains.Any(domain => domain.ServiceResourceId == service.Id)) return;
        await proxyConfigurations.ApplyForDeploymentAsync(null, service.TeamId, service.ServerId, cancellationToken);
    }
}
