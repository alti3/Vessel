using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Persistence;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Certificates;
using Vessel.Domain.Common;
using Vessel.Domain.Proxy;
using Vessel.Domain.Secrets;
using Vessel.Domain.Services;
using Vessel.Domain.ValueObjects;

namespace Vessel.Application.Proxy.Domains;

public sealed record ServiceDomainSummary(
    Guid Id,
    Guid ServiceId,
    string ComposeService,
    string Host,
    string PathPrefix,
    RouteHostKind HostKind,
    int TargetPort,
    bool TlsEnabled,
    string Url,
    RoutePolicySummary Policy);

/// <summary>
///     Routes a domain to one Compose service of a service-template resource. <see cref="ComposeService" /> and
///     <see cref="TargetPort" /> form the per-service port map; the internal port defaults to the template's known web
///     port when one is registered in <see cref="ServiceRoutingDefaults" />.
/// </summary>
public sealed record ConfigureServiceDomainRequest(
    string Host,
    string? ComposeService,
    int? TargetPort,
    bool TlsEnabled = true,
    RoutePolicyRequest? Policy = null,
    bool ForceDomainOverride = false);

/// <summary>
///     Domains for service-template resources (upstream: service applications have their own domains). Routes are
///     published by the next proxy apply (service provisioning or a manual apply).
/// </summary>
public sealed class ServiceDomainService(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    IAuditWriter auditWriter,
    TimeProvider timeProvider,
    RoutePolicyWriter policyWriter)
{
    public IReadOnlyList<ServiceDomainSummary> List(UserId actorUserId, TeamId teamId, ServiceResourceId serviceId)
    {
        ServiceResource service = LoadAuthorized(actorUserId, teamId, serviceId, VesselPermissions.ProjectsRead);
        return dbContext.ServiceDomains
            .Where(domain => domain.ServiceResourceId == service.Id)
            .AsEnumerable()
            .OrderBy(domain => domain.Host, StringComparer.OrdinalIgnoreCase)
            .ThenBy(domain => domain.PathPrefix, StringComparer.Ordinal)
            .Select(ToSummary)
            .ToArray();
    }

    public async Task<ServiceDomainSummary> ConfigureAsync(
        UserId actorUserId,
        TeamId teamId,
        ServiceResourceId serviceId,
        ConfigureServiceDomainRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ServiceResource service = LoadAuthorized(actorUserId, teamId, serviceId, VesselPermissions.ProjectsWrite);
        RouteAddress address = RouteAddress.Parse(request.Host);
        var tlsEnabled = address.Scheme switch
        {
            RouteScheme.Https => true,
            RouteScheme.Http => false,
            _ => request.TlsEnabled
        };
        if (request.TargetPort is not null && address.Port is not null && request.TargetPort != address.Port)
            throw new DomainException("The port in the domain URL does not match the target port.");
        (var defaultService, int? defaultPort) = ServiceRoutingDefaults.For(service.TemplateKey);
        var composeService = string.IsNullOrWhiteSpace(request.ComposeService)
            ? defaultService ?? throw new DomainException("Compose service is required for this template.")
            : request.ComposeService.Trim();
        var targetPort = request.TargetPort ?? address.Port ??
            (string.Equals(composeService, defaultService, StringComparison.Ordinal) ? defaultPort : null) ??
            throw new DomainException("Target port is required for this Compose service.");
        address = address.WithoutPort();

        IReadOnlyList<DomainConflict> conflicts = DomainConflicts.Find(dbContext, teamId, address,
            RouteOwnerKind.Service, service.Id.Value);
        if (conflicts.Count > 0 && !request.ForceDomainOverride)
            throw new DomainException(DomainConflicts.Describe(conflicts));

        DateTimeOffset now = timeProvider.GetUtcNow();
        ServiceDomain? existing = dbContext.ServiceDomains.SingleOrDefault(domain =>
            domain.ServiceResourceId == service.Id && domain.Host == address.Host &&
            domain.PathPrefix == address.PathPrefix);
        RoutePolicy policy = await policyWriter.MergeAsync(existing?.Policy ?? RoutePolicy.Default, request.Policy,
            service.TeamId, SecretScope.Environment, DomainRoutingService.BasicAuthSecretKey(address),
            new SecretTarget(EnvironmentId: service.EnvironmentId), cancellationToken);
        ServiceDomain domain;
        if (existing is null)
        {
            domain = ServiceDomain.Create(service.Id, address, composeService, targetPort, tlsEnabled, policy, now);
            await dbContext.ServiceDomainRepository.AddAsync(domain, cancellationToken);
        }
        else
        {
            existing.Update(composeService, targetPort, tlsEnabled, policy, now);
            domain = existing;
        }

        if (domain.TlsEnabled && !dbContext.Certificates.Any(certificate =>
                certificate.ServerId == service.ServerId && certificate.Host == domain.Host))
            await dbContext.CertificateRepository.AddAsync(
                Certificate.CreatePending(service.TeamId, service.ServerId, null, domain.Host, now), cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.ServiceDomainConfigured,
            new AuditTarget("service", service.Id.Value.ToString("D")), null,
            new Dictionary<string, object?>
            {
                ["host"] = domain.Host,
                ["path"] = domain.PathPrefix,
                ["composeService"] = domain.ComposeService,
                ["targetPort"] = domain.TargetPort,
                ["tlsEnabled"] = domain.TlsEnabled,
                ["basicAuth"] = policy.BasicAuth is not null
            }, cancellationToken);
        if (conflicts.Count > 0)
            await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.DomainConflictOverridden,
                new AuditTarget("service", service.Id.Value.ToString("D")), null,
                new Dictionary<string, object?>
                {
                    ["identity"] = address.Identity,
                    ["conflicts"] = conflicts
                        .Select(conflict => new { kind = conflict.OwnerKind.ToString(), id = conflict.OwnerId })
                        .ToArray()
                }, cancellationToken);
        return ToSummary(domain);
    }

    public async Task RemoveAsync(
        UserId actorUserId,
        TeamId teamId,
        ServiceResourceId serviceId,
        string address,
        CancellationToken cancellationToken = default)
    {
        ServiceResource service = LoadAuthorized(actorUserId, teamId, serviceId, VesselPermissions.ProjectsWrite);
        RouteAddress route = RouteAddress.Parse(address).WithoutPort();
        ServiceDomain? existing = dbContext.ServiceDomains.SingleOrDefault(domain =>
            domain.ServiceResourceId == service.Id && domain.Host == route.Host &&
            domain.PathPrefix == route.PathPrefix);
        if (existing is null) return;
        dbContext.ServiceDomainRepository.Remove(existing);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.ServiceDomainRemoved,
            new AuditTarget("service", service.Id.Value.ToString("D")), null,
            new Dictionary<string, object?> { ["host"] = route.Identity }, cancellationToken);
    }

    private ServiceResource LoadAuthorized(UserId actorUserId, TeamId teamId, ServiceResourceId serviceId,
        string permission)
    {
        ServiceResource service = dbContext.ServiceResources.SingleOrDefault(item => item.Id == serviceId)
                                  ?? throw new InvalidOperationException("Service was not found.");
        // The environment resolves the owning team; a service is never authorized through the caller's active team.
        authorization.Demand(actorUserId, teamId, ResourceRef.Environment(service.EnvironmentId), permission);
        return service;
    }

    private static ServiceDomainSummary ToSummary(ServiceDomain domain)
    {
        return new ServiceDomainSummary(domain.Id.Value, domain.ServiceResourceId.Value, domain.ComposeService,
            domain.Host, domain.PathPrefix, domain.HostKind, domain.TargetPort, domain.TlsEnabled,
            domain.Address.ToUrl(domain.TlsEnabled), RoutePolicySummary.From(domain.Policy));
    }
}

/// <summary>
///     Known web endpoints (Compose service and internal port) of built-in service templates, used when a service domain
///     does not name them explicitly.
/// </summary>
public static class ServiceRoutingDefaults
{
    private static readonly Dictionary<string, (string Service, int Port)> Defaults = new(StringComparer.Ordinal)
    {
        ["pgadmin"] = ("pgadmin", 80),
        ["redis-insight"] = ("redis-insight", 5540),
        ["qdrant"] = ("qdrant", 6333),
        ["rabbitmq"] = ("rabbitmq", 15672),
        ["portainer"] = ("portainer", 9000),
        ["open-webui"] = ("open-webui", 8080),
        ["n8n"] = ("n8n", 5678),
        ["gitlab"] = ("gitlab", 80),
        ["gitea"] = ("gitea", 3000),
        ["grafana"] = ("grafana", 3000),
        ["grafana-postgres"] = ("grafana", 3000),
        ["metabase"] = ("metabase", 3000),
        ["excalidraw"] = ("excalidraw", 80)
    };

    public static (string? Service, int? Port) For(string templateKey)
    {
        return Defaults.TryGetValue(templateKey, out (string Service, int Port) value)
            ? (value.Service, value.Port)
            : (null, null);
    }
}
