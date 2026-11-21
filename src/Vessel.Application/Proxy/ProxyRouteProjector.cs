using Vessel.Application.Deployments;
using Vessel.Application.Persistence;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Applications;
using Vessel.Domain.Proxy;
using Vessel.Domain.Services;
using Vessel.Domain.Webhooks;
using AppEntity = Vessel.Domain.Applications.Application;

namespace Vessel.Application.Proxy;

/// <summary>
///     Projects every routable resource on a server (applications, open previews, service-template resources) into
///     <see cref="ProxyRoute" />s, each targeting a <see cref="RoutableEndpoint" />. Basic-auth passwords are resolved
///     from the secret vault only for rendering (<see cref="ResolveCredentialsAsync" />).
/// </summary>
public sealed class ProxyRouteProjector(IVesselDbContext dbContext, ISecretVault secretVault)
{
    public IReadOnlyList<ProxyRoute> RoutesForServer(ServerId serverId)
    {
        return OwnedRoutesForServer(serverId).Select(item => item.Route).ToArray();
    }

    /// <summary>Hosts (lower case) routed on the server, for certificate in-use checks.</summary>
    public IReadOnlySet<string> HostsForServer(ServerId serverId)
    {
        return RoutesForServer(serverId).Select(route => route.Host).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    ///     Routes with basic-auth credentials resolved. A route whose secret cannot be read is returned with a
    ///     <see langword="null" /> credential and listed in <paramref name="unresolved" /> so the caller can quarantine it
    ///     (it is never published without its protection).
    /// </summary>
    public async Task<IReadOnlyList<ProxyRoute>> ResolveCredentialsAsync(
        ServerId serverId,
        ICollection<ProxyRoute> unresolved,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unresolved);
        var routes = new List<ProxyRoute>();
        foreach ((ProxyRoute route, TeamId teamId) in OwnedRoutesForServer(serverId))
        {
            RouteBasicAuth? basicAuth = route.Policy?.BasicAuth;
            if (basicAuth is null)
            {
                routes.Add(route);
                continue;
            }

            try
            {
                var password = await secretVault.RevealForDeploymentAsync(teamId, basicAuth.PasswordSecretReferenceId,
                    cancellationToken);
                routes.Add(route with { BasicAuth = new ProxyBasicAuthCredentials(basicAuth.Username, password) });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                unresolved.Add(route);
                routes.Add(route);
            }
        }

        return routes;
    }

    private (ProxyRoute Route, TeamId TeamId)[] OwnedRoutesForServer(ServerId serverId)
    {
        var applicationRows = (
                from application in dbContext.Applications
                join environment in dbContext.Environments on application.EnvironmentId equals environment.Id
                join project in dbContext.Projects on environment.ProjectId equals project.Id
                where application.ServerId == serverId
                select new { Application = application, project.TeamId })
            .ToArray();

        var routes = new List<(ProxyRoute, TeamId)>();
        foreach (var row in applicationRows)
        {
            AppEntity application = row.Application;
            var defaultPort = application.RuntimeConfiguration.ExposedPort?.Value ?? DeploymentPlanBuilder.DefaultPort;
            ApplicationDomain[] domains = dbContext.ApplicationDomains
                .Where(domain => domain.ApplicationId == application.Id)
                .ToArray();
            foreach (ApplicationDomain domain in domains
                         .OrderBy(domain => domain.Host, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(domain => domain.PathPrefix, StringComparer.Ordinal))
                routes.Add((new ProxyRoute(
                    RoutableEndpoint.ForApplication(application.Id, domain.TargetPort ?? defaultPort),
                    application.ServerId, domain.Host, domain.PathPrefix, domain.TlsEnabled, domain.Canonical,
                    domain.RedirectToCanonical, domain.Policy), row.TeamId));

            // Open previews with a recorded route get their own host, routed to the preview alias (never production).
            ApplicationPreview[] previews = dbContext.ApplicationPreviews
                .Where(preview => preview.ApplicationId == application.Id &&
                                  preview.Status == ApplicationPreviewStatus.Open &&
                                  preview.PreviewUrl != null)
                .ToArray();
            foreach (ApplicationPreview preview in previews)
            {
                PreviewRoute? route =
                    DeploymentPlanBuilder.PreviewRoute(domains, preview.PullRequestNumber, defaultPort);
                if (route is null || !string.Equals(route.Url, preview.PreviewUrl, StringComparison.OrdinalIgnoreCase))
                    continue;
                var endpoint = new RoutableEndpoint(RouteOwnerKind.Preview, application.Id.Value,
                    ProxyNaming.PreviewUpstreamAlias(application.Id, preview.PullRequestNumber), route.TargetPort);
                routes.Add((new ProxyRoute(endpoint, application.ServerId, route.Host, route.PathPrefix,
                    route.TlsEnabled, false, false, route.Policy), row.TeamId));
            }
        }

        var serviceRows = (
                from domain in dbContext.ServiceDomains
                join service in dbContext.ServiceResources on domain.ServiceResourceId equals service.Id
                where service.ServerId == serverId
                select new { Domain = domain, Service = service })
            .ToArray();
        foreach (var row in serviceRows
                     .OrderBy(item => item.Domain.Host, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.Domain.PathPrefix, StringComparer.Ordinal))
        {
            ServiceDomain domain = row.Domain;
            var endpoint = new RoutableEndpoint(RouteOwnerKind.Service, row.Service.Id.Value,
                ProxyNaming.ServiceUpstreamAlias(row.Service.Id, domain.ComposeService), domain.TargetPort,
                domain.ComposeService);
            routes.Add((new ProxyRoute(endpoint, row.Service.ServerId, domain.Host, domain.PathPrefix,
                domain.TlsEnabled, false, false, domain.Policy), row.Service.TeamId));
        }

        return routes
            .OrderBy(item => item.Item1.Host, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Item1.PathPrefix, StringComparer.Ordinal)
            .ToArray();
    }
}
