using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Notifications;
using Vessel.Application.Persistence;
using Vessel.Application.Redis;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Applications;
using Vessel.Domain.Auditing;
using Vessel.Domain.Common;
using Vessel.Domain.Proxy;
using Vessel.Domain.Servers;
using AppEntity = Vessel.Domain.Applications.Application;

namespace Vessel.Application.Proxy;

public sealed class ProxyConfigurationService(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    IProxyProvider proxyProvider,
    ProxyLifecycleService proxyLifecycle,
    IDistributedLockManager locks,
    IAuditWriter auditWriter,
    ISecretRedactor redactor,
    TimeProvider timeProvider,
    INotificationEventPublisher notifications)
{
    private static readonly TimeSpan DeploymentLockWait = TimeSpan.FromSeconds(60);

    public IReadOnlyList<ProxyConfigurationSummary> ListVersions(UserId actorUserId, TeamId teamId, ServerId serverId)
    {
        RequireServer(actorUserId, teamId, serverId, VesselPermissions.ServersRead);
        return dbContext.ProxyConfigurationVersions
            .Where(version => version.ServerId == serverId)
            .OrderByDescending(version => version.CreatedAt)
            .Select(ToSummary)
            .ToArray();
    }

    public async Task<ProxyConfigurationSummary> GenerateValidateAndApplyAsync(
        UserId actorUserId,
        TeamId teamId,
        ServerId serverId,
        CancellationToken cancellationToken = default)
    {
        RequireServer(actorUserId, teamId, serverId, VesselPermissions.ServersWrite);
        RequireHostLocalAllowed(actorUserId, serverId);
        if (!proxyLifecycle.IsRoutingEnabled(serverId))
            throw new DomainException("Routing is disabled because the server proxy type is None.");
        return await ApplyCoreAsync(actorUserId, teamId, serverId, TimeSpan.Zero, cancellationToken);
    }

    /// <summary>
    ///     Applies routes for a deployment or certificate job. Returns <see langword="null" /> without generating a
    ///     version when the server proxy type is None (routing is skipped entirely).
    /// </summary>
    public async Task<ProxyConfigurationSummary?> ApplyForDeploymentAsync(
        UserId? actorUserId,
        TeamId teamId,
        ServerId serverId,
        CancellationToken cancellationToken = default)
    {
        if (actorUserId.HasValue)
            authorization.Demand(actorUserId.Value, teamId, ResourceRef.Server(serverId),
                VesselPermissions.DeploymentsStart);
        if (!proxyLifecycle.IsRoutingEnabled(serverId))
            return null;
        // Concurrent deployments on one server queue briefly for the proxy lock instead of failing outright.
        return await ApplyCoreAsync(actorUserId, teamId, serverId, DeploymentLockWait, cancellationToken);
    }

    private async Task<ProxyConfigurationSummary> ApplyCoreAsync(
        UserId? actorUserId,
        TeamId teamId,
        ServerId serverId,
        TimeSpan lockWait,
        CancellationToken cancellationToken)
    {
        Server server = dbContext.Servers.SingleOrDefault(item => item.Id == serverId)
                        ?? throw new InvalidOperationException("Server was not found.");
        if (server.Status == ServerStatus.Unreachable)
            throw new DomainException("Cannot apply proxy configuration while server is unreachable.");

        await using DistributedLockHandle? handle = await locks.TryAcquireAsync(
            ProxyLifecycleService.LockKey(serverId),
            ProxyLifecycleService.LockLease,
            lockWait,
            cancellationToken);
        if (handle is null)
            throw new DomainException("A proxy configuration operation is already running for this server.");

        DateTimeOffset now = timeProvider.GetUtcNow();
        ProxyConfigurationVersion? previous = dbContext.ProxyConfigurationVersions
            .Where(version => version.ServerId == serverId && version.Status == ProxyConfigurationStatus.Applied)
            .OrderByDescending(version => version.AppliedAt)
            .FirstOrDefault();
        IReadOnlyList<ProxyRoute> candidateRoutes = RoutesForServer(serverId);
        // Invalid routes are quarantined individually so one bad domain cannot take down routing for the server.
        IReadOnlyList<ProxyRouteRejection> rejections = proxyProvider.ValidateRoutes(candidateRoutes);
        var rejectedRoutes = rejections.Select(rejection => rejection.Route).ToHashSet();
        ProxyRoute[] acceptedRoutes = candidateRoutes.Where(route => !rejectedRoutes.Contains(route)).ToArray();
        ProxyConfigurationDocument document = proxyProvider.Generate(serverId, acceptedRoutes);
        var version = ProxyConfigurationVersion.Create(serverId, document.Provider, document.Version,
            document.Sha256Hash,
            document.Contents, previous?.Id, now);
        await dbContext.ProxyConfigurationVersionRepository.AddAsync(version, cancellationToken);

        ProxyValidationResult validation = proxyProvider.Validate(document);
        if (!validation.Succeeded)
        {
            version.MarkValidationFailed(string.Join("; ", validation.Errors.Select(error => redactor.Redact(error))),
                now);
            await dbContext.SaveChangesAsync(cancellationToken);
            await notifications.PublishAsync(teamId,
                OperationalNotifications.ProxyApplyFailed(serverId.Value, server.Name.Value, "validation"),
                CancellationToken.None);
            throw new DomainException(version.ValidationError ?? "Proxy configuration validation failed.");
        }

        version.MarkValidated(now);
        await dbContext.SaveChangesAsync(cancellationToken);

        ProxyConfigurationDocument? previousDocument = previous is null
            ? null
            : new ProxyConfigurationDocument(previous.ServerId, previous.Provider, previous.Version,
                previous.Configuration,
                previous.ConfigurationHash, []);

        // Write-then-verify on the server's runtime host; Traefik's file provider watch picks the change up.
        ProxyApplyResult apply =
            await proxyLifecycle.DeliverDynamicConfigurationAsync(server, document, cancellationToken);
        if (!apply.Succeeded)
        {
            version.MarkApplyFailed(redactor.Redact(apply.Message), timeProvider.GetUtcNow());
            if (previousDocument is not null)
                await proxyLifecycle.DeliverDynamicConfigurationAsync(server, previousDocument, cancellationToken);

            await dbContext.SaveChangesAsync(cancellationToken);
            await notifications.PublishAsync(teamId,
                OperationalNotifications.ProxyApplyFailed(serverId.Value, server.Name.Value, "apply"),
                CancellationToken.None);
            throw new DomainException(version.ApplyError ?? "Proxy configuration apply failed.");
        }

        version.MarkApplied(timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.ProxyConfigurationApplied,
            new AuditTarget("server", serverId.Value.ToString("D")), null,
            new Dictionary<string, object?>
            {
                ["provider"] = document.Provider.ToString(),
                ["version"] = document.Version,
                ["configurationHash"] = document.Sha256Hash,
                ["routeCount"] = acceptedRoutes.Length,
                ["delivery"] = redactor.Redact(apply.Message),
                ["rejectedRoutes"] = rejections
                    .Select(rejection => new { host = rejection.Route.Host, reason = redactor.Redact(rejection.Reason) })
                    .ToArray()
            }, cancellationToken);

        return ToSummary(version);
    }

    public async Task<ProxyConfigurationSummary> RollbackAsync(
        UserId actorUserId,
        TeamId teamId,
        ServerId serverId,
        ProxyConfigurationVersionId versionId,
        CancellationToken cancellationToken = default)
    {
        ProxyConfigurationVersion version =
            await dbContext.ProxyConfigurationVersionRepository.GetByIdAsync(versionId, cancellationToken)
            ?? throw new InvalidOperationException("Proxy configuration version was not found.");
        if (version.ServerId != serverId)
            throw new UnauthorizedAccessException("Proxy configuration version is outside the requested server.");
        RequireServer(actorUserId, teamId, serverId, VesselPermissions.ServersWrite);
        Server server = dbContext.Servers.SingleOrDefault(item => item.Id == serverId)
                        ?? throw new InvalidOperationException("Server was not found.");
        HostLocalExecutionGuard.RequireInstanceAdmin(authorization, actorUserId, server);
        if (!proxyLifecycle.IsRoutingEnabled(serverId))
            throw new DomainException("Routing is disabled because the server proxy type is None.");

        await using DistributedLockHandle? handle = await locks.TryAcquireAsync(
            ProxyLifecycleService.LockKey(serverId),
            ProxyLifecycleService.LockLease,
            TimeSpan.Zero,
            cancellationToken);
        if (handle is null)
            throw new DomainException("A proxy configuration operation is already running for this server.");

        var document = new ProxyConfigurationDocument(serverId, version.Provider, version.Version,
            version.Configuration, version.ConfigurationHash, []);
        ProxyValidationResult validation = proxyProvider.Validate(document);
        if (!validation.Succeeded)
            throw new DomainException(string.Join("; ", validation.Errors));

        ProxyApplyResult result =
            await proxyLifecycle.DeliverDynamicConfigurationAsync(server, document, cancellationToken);
        if (!result.Succeeded)
            throw new DomainException(redactor.Redact(result.Message));

        version.MarkRolledBack(timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.ProxyConfigurationRolledBack,
            new AuditTarget("proxy-configuration", version.Id.Value.ToString("D")), null,
            new Dictionary<string, object?>(), cancellationToken);
        return ToSummary(version);
    }

    private IReadOnlyList<ProxyRoute> RoutesForServer(ServerId serverId)
    {
        var applicationRows =
            from application in dbContext.Applications
            join environment in dbContext.Environments on application.EnvironmentId equals environment.Id
            join project in dbContext.Projects on environment.ProjectId equals project.Id
            where application.ServerId == serverId
            select new { Application = application, project.TeamId };

        var routes = new List<ProxyRoute>();
        foreach (var row in applicationRows)
        {
            AppEntity application = row.Application;
            var defaultPort = application.RuntimeConfiguration.ExposedPort?.Value ?? 8080;
            var upstream = ProxyNaming.UpstreamAlias(application.Id);
            ApplicationDomain[] domains = dbContext.ApplicationDomains
                .Where(domain => domain.ApplicationId == application.Id)
                .OrderBy(domain => domain.DomainName.Value)
                .ToArray();
            foreach (ApplicationDomain domain in domains.OrderBy(domain => domain.DomainName.Value,
                         StringComparer.OrdinalIgnoreCase))
                routes.Add(new ProxyRoute(application.Id, application.ServerId, upstream, domain.DomainName.Value,
                    domain.TargetPort ?? defaultPort, domain.TlsEnabled, domain.Canonical, domain.RedirectToCanonical));
        }

        return routes.OrderBy(route => route.Host, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void RequireServer(UserId actorUserId, TeamId teamId, ServerId serverId, string permission)
    {
        authorization.Demand(actorUserId, teamId, ResourceRef.Server(serverId), permission);
    }

    /// <summary>Manual proxy changes on a Local/DockerSocket server act on the control-plane host.</summary>
    private void RequireHostLocalAllowed(UserId actorUserId, ServerId serverId)
    {
        Server server = dbContext.Servers.SingleOrDefault(item => item.Id == serverId)
                        ?? throw new InvalidOperationException("Server was not found.");
        HostLocalExecutionGuard.RequireInstanceAdmin(authorization, actorUserId, server);
    }

    private static ProxyConfigurationSummary ToSummary(ProxyConfigurationVersion version)
    {
        return new ProxyConfigurationSummary(version.Id.Value, version.ServerId.Value, version.Provider,
            version.Version, version.ConfigurationHash, version.Status, version.PreviousVersionId?.Value,
            version.ValidationError, version.ApplyError, version.CreatedAt, version.AppliedAt);
    }
}
