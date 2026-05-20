using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Persistence;
using Vessel.Application.Redis;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Certificates;
using Vessel.Domain.Common;
using Vessel.Domain.Proxy;
using Vessel.Domain.Servers;
using AppEntity = Vessel.Domain.Applications.Application;
using EnvironmentEntity = Vessel.Domain.Projects.Environment;

namespace Vessel.Application.Proxy;

public sealed class ProxyConfigurationService(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    IProxyProvider proxyProvider,
    IDistributedLockManager locks,
    IAuditWriter auditWriter,
    ISecretRedactor redactor,
    TimeProvider timeProvider)
{
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
        return await ApplyCoreAsync(actorUserId, teamId, serverId, cancellationToken);
    }

    public async Task<ProxyConfigurationSummary> ApplyForDeploymentAsync(
        UserId? actorUserId,
        TeamId teamId,
        ServerId serverId,
        CancellationToken cancellationToken = default)
    {
        if (actorUserId.HasValue && !authorization.HasPermission(actorUserId.Value, teamId, VesselPermissions.DeploymentsStart))
            throw new UnauthorizedAccessException($"Missing required permission '{VesselPermissions.DeploymentsStart}'.");
        return await ApplyCoreAsync(actorUserId, teamId, serverId, cancellationToken);
    }

    private async Task<ProxyConfigurationSummary> ApplyCoreAsync(
        UserId? actorUserId,
        TeamId teamId,
        ServerId serverId,
        CancellationToken cancellationToken)
    {
        Server server = dbContext.Servers.SingleOrDefault(item => item.Id == serverId)
            ?? throw new InvalidOperationException("Server was not found.");
        if (server.Status == ServerStatus.Unreachable)
            throw new DomainException("Cannot apply proxy configuration while server is unreachable.");

        string lockKey = $"proxy:{serverId.Value:D}";
        await using DistributedLockHandle? handle = await locks.TryAcquireAsync(
            lockKey,
            TimeSpan.FromMinutes(5),
            TimeSpan.Zero,
            cancellationToken);
        if (handle is null)
            throw new DomainException("A proxy configuration operation is already running for this server.");

        DateTimeOffset now = timeProvider.GetUtcNow();
        ProxyConfigurationVersion? previous = dbContext.ProxyConfigurationVersions
            .Where(version => version.ServerId == serverId && version.Status == ProxyConfigurationStatus.Applied)
            .OrderByDescending(version => version.AppliedAt)
            .FirstOrDefault();
        ProxyConfigurationDocument document = proxyProvider.Generate(serverId, RoutesForServer(serverId));
        var version = ProxyConfigurationVersion.Create(serverId, document.Provider, document.Version, document.Sha256Hash,
            document.Contents, previous?.Id, now);
        await dbContext.ProxyConfigurationVersionRepository.AddAsync(version, cancellationToken);

        ProxyValidationResult validation = proxyProvider.Validate(document);
        if (!validation.Succeeded)
        {
            version.MarkValidationFailed(string.Join("; ", validation.Errors.Select(error => redactor.Redact(error))), now);
            await dbContext.SaveChangesAsync(cancellationToken);
            throw new DomainException(version.ValidationError ?? "Proxy configuration validation failed.");
        }

        version.MarkValidated(now);
        await dbContext.SaveChangesAsync(cancellationToken);

        ProxyConfigurationDocument? previousDocument = previous is null
            ? null
            : new ProxyConfigurationDocument(previous.ServerId, previous.Provider, previous.Version, previous.Configuration,
                previous.ConfigurationHash, []);

        ProxyApplyResult apply = await proxyProvider.ApplyAsync(document, previousDocument, cancellationToken);
        if (!apply.Succeeded)
        {
            version.MarkApplyFailed(redactor.Redact(apply.Message), timeProvider.GetUtcNow());
            if (previousDocument is not null)
            {
                await proxyProvider.RollbackAsync(previousDocument, cancellationToken);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
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
                ["configurationHash"] = document.Sha256Hash
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
        ProxyConfigurationVersion version = await dbContext.ProxyConfigurationVersionRepository.GetByIdAsync(versionId, cancellationToken)
            ?? throw new InvalidOperationException("Proxy configuration version was not found.");
        if (version.ServerId != serverId)
            throw new UnauthorizedAccessException("Proxy configuration version is outside the requested server.");
        RequireServer(actorUserId, teamId, serverId, VesselPermissions.ServersWrite);

        string lockKey = $"proxy:{serverId.Value:D}";
        await using DistributedLockHandle? handle = await locks.TryAcquireAsync(
            lockKey,
            TimeSpan.FromMinutes(5),
            TimeSpan.Zero,
            cancellationToken);
        if (handle is null)
            throw new DomainException("A proxy configuration operation is already running for this server.");

        var document = new ProxyConfigurationDocument(serverId, version.Provider, version.Version,
            version.Configuration, version.ConfigurationHash, []);
        ProxyValidationResult validation = proxyProvider.Validate(document);
        if (!validation.Succeeded)
            throw new DomainException(string.Join("; ", validation.Errors));

        ProxyApplyResult result = await proxyProvider.RollbackAsync(document, cancellationToken);
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
            int defaultPort = application.RuntimeConfiguration.ExposedPort?.Value ?? 8080;
            string serviceName = SanitizeName(application.Name.Value);
            var domains = dbContext.ApplicationDomains
                .Where(domain => domain.ApplicationId == application.Id)
                .OrderBy(domain => domain.DomainName.Value)
                .ToArray();
            foreach (var domain in domains.OrderBy(domain => domain.DomainName.Value, StringComparer.OrdinalIgnoreCase))
            {
                routes.Add(new ProxyRoute(application.Id, application.ServerId, serviceName, domain.DomainName.Value,
                    domain.TargetPort ?? defaultPort, domain.TlsEnabled, domain.Canonical, domain.RedirectToCanonical));
            }
        }

        return routes.OrderBy(route => route.Host, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void RequireServer(UserId actorUserId, TeamId teamId, ServerId serverId, string permission)
    {
        if (!authorization.HasPermission(actorUserId, teamId, permission))
            throw new UnauthorizedAccessException($"Missing required permission '{permission}'.");
        if (!authorization.CanAccessServer(actorUserId, serverId))
            throw new UnauthorizedAccessException("Server is outside the active team.");
    }

    private static ProxyConfigurationSummary ToSummary(ProxyConfigurationVersion version)
    {
        return new ProxyConfigurationSummary(version.Id.Value, version.ServerId.Value, version.Provider,
            version.Version, version.ConfigurationHash, version.Status, version.PreviousVersionId?.Value,
            version.ValidationError, version.ApplyError, version.CreatedAt, version.AppliedAt);
    }

    private static string SanitizeName(string value)
    {
        string normalized = new(value.ToLowerInvariant().Select(character =>
            char.IsLetterOrDigit(character) ? character : '-').ToArray());
        normalized = normalized.Trim('-');
        if (string.IsNullOrWhiteSpace(normalized))
            normalized = "app";
        return normalized[..Math.Min(40, normalized.Length)];
    }
}
