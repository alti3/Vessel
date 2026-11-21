using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Notifications;
using Vessel.Application.Persistence;
using Vessel.Application.Redis;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Common;
using Vessel.Domain.Proxy;
using Vessel.Domain.Servers;

namespace Vessel.Application.Proxy;

/// <summary>
///     Generates, validates, applies and keeps the history of a server's proxy configuration (the generated routes file
///     and user dynamic configuration files). Stored configuration text is encrypted; restores create new versions;
///     history is bounded by <see cref="PruneHistoryAsync" />.
/// </summary>
public sealed class ProxyConfigurationService(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    IProxyProvider proxyProvider,
    ProxyLifecycleService proxyLifecycle,
    IDistributedLockManager locks,
    IAuditWriter auditWriter,
    ISecretRedactor redactor,
    TimeProvider timeProvider,
    INotificationEventPublisher notifications,
    ProxyRouteProjector routeProjector,
    ProxyConfigurationProtector protector)
{
    /// <summary>Newest versions kept per server by the history cleanup (applied versions are always kept as well).</summary>
    public const int HistoryRetention = 20;

    private static readonly TimeSpan DeploymentLockWait = TimeSpan.FromSeconds(60);

    public IReadOnlyList<ProxyConfigurationSummary> ListVersions(UserId actorUserId, TeamId teamId, ServerId serverId)
    {
        RequireServer(actorUserId, teamId, serverId, VesselPermissions.ServersRead);
        return dbContext.ProxyConfigurationVersions
            .Where(version => version.ServerId == serverId)
            .OrderByDescending(version => version.CreatedAt)
            .AsEnumerable()
            .Select(ToSummary)
            .ToArray();
    }

    /// <summary>
    ///     Returns the decrypted text of a version. Configuration text is sensitive (basic-auth hashes, backend topology),
    ///     so this requires write access to the server and is audited.
    /// </summary>
    public async Task<ProxyConfigurationContent> GetVersionContentAsync(
        UserId actorUserId,
        TeamId teamId,
        ServerId serverId,
        ProxyConfigurationVersionId versionId,
        CancellationToken cancellationToken = default)
    {
        RequireServer(actorUserId, teamId, serverId, VesselPermissions.ServersWrite);
        ProxyConfigurationVersion version = LoadVersion(serverId, versionId);
        var configuration = protector.Reveal(serverId, version.Configuration);
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.ProxyConfigurationRevealed,
            new AuditTarget("proxy-configuration", version.Id.Value.ToString("D")), null,
            new Dictionary<string, object?> { ["serverId"] = serverId.Value.ToString("D") }, cancellationToken);
        return new ProxyConfigurationContent(ToSummary(version), configuration);
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
        Server server = LoadReachableServer(serverId);
        await using DistributedLockHandle handle = await AcquireAsync(serverId, lockWait, cancellationToken);

        DateTimeOffset now = timeProvider.GetUtcNow();
        ProxyConfigurationVersion? previous = CurrentGeneratedVersion(serverId);
        var unresolved = new List<ProxyRoute>();
        IReadOnlyList<ProxyRoute> candidateRoutes =
            await routeProjector.ResolveCredentialsAsync(serverId, unresolved, cancellationToken);
        // Invalid routes are quarantined individually so one bad domain cannot take down routing for the server. A
        // protected route whose credentials cannot be read is never published unprotected.
        List<ProxyRouteRejection> rejections = proxyProvider.ValidateRoutes(candidateRoutes).ToList();
        rejections.AddRange(unresolved.Select(route => new ProxyRouteRejection(route,
            $"Domain host '{route.Host}' has basic auth enabled but its password secret could not be read.")));
        var rejectedRoutes = rejections.Select(rejection => rejection.Route).ToHashSet();
        ProxyRoute[] acceptedRoutes = candidateRoutes.Where(route => !rejectedRoutes.Contains(route)).ToArray();
        ProxyConfigurationDocument document = proxyProvider.Generate(serverId, acceptedRoutes);

        if (previous is not null &&
            string.Equals(previous.ConfigurationHash, document.Sha256Hash, StringComparison.Ordinal))
            return await RedeliverUnchangedAsync(actorUserId, teamId, server, previous, document, rejections,
                cancellationToken);

        var version = ProxyConfigurationVersion.Create(serverId, document.Provider, document.Version,
            document.Sha256Hash, protector.Protect(serverId, document.Contents), previous?.Id, now);
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

        // Write-then-verify on the server's runtime host; Traefik's file provider watch picks the change up.
        ProxyApplyResult apply =
            await proxyLifecycle.DeliverDynamicConfigurationAsync(server, document, cancellationToken);
        if (!apply.Succeeded)
        {
            version.MarkApplyFailed(redactor.Redact(apply.Message), timeProvider.GetUtcNow());
            ProxyConfigurationDocument? previousDocument = RevealDocument(previous);
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
        await AuditAppliedAsync(teamId, actorUserId, serverId, document, acceptedRoutes.Length, apply, rejections,
            cancellationToken);
        return ToSummary(version);
    }

    /// <summary>
    ///     Unchanged routes: the file is delivered again (so a missing or edited file on the server is repaired) without
    ///     adding a duplicate history entry.
    /// </summary>
    private async Task<ProxyConfigurationSummary> RedeliverUnchangedAsync(
        UserId? actorUserId,
        TeamId teamId,
        Server server,
        ProxyConfigurationVersion current,
        ProxyConfigurationDocument document,
        IReadOnlyList<ProxyRouteRejection> rejections,
        CancellationToken cancellationToken)
    {
        ProxyApplyResult apply =
            await proxyLifecycle.DeliverDynamicConfigurationAsync(server, document, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        if (!apply.Succeeded)
        {
            await notifications.PublishAsync(teamId,
                OperationalNotifications.ProxyApplyFailed(server.Id.Value, server.Name.Value, "apply"),
                CancellationToken.None);
            throw new DomainException(redactor.Redact(apply.Message));
        }

        await AuditAppliedAsync(teamId, actorUserId, server.Id, document, document.Routes.Count, apply, rejections,
            cancellationToken);
        return ToSummary(current);
    }

    /// <summary>
    ///     Restores an earlier version by creating a NEW version (<see cref="ProxyConfigurationSource.Restore" />) linked
    ///     through <c>RestoredFromId</c>; history is never rewritten. Versions of user dynamic files restore that file.
    /// </summary>
    public async Task<ProxyConfigurationSummary> RestoreAsync(
        UserId actorUserId,
        TeamId teamId,
        ServerId serverId,
        ProxyConfigurationVersionId versionId,
        CancellationToken cancellationToken = default)
    {
        ProxyConfigurationVersion source =
            await dbContext.ProxyConfigurationVersionRepository.GetByIdAsync(versionId, cancellationToken)
            ?? throw new InvalidOperationException("Proxy configuration version was not found.");
        if (source.ServerId != serverId)
            throw new UnauthorizedAccessException("Proxy configuration version is outside the requested server.");
        RequireServer(actorUserId, teamId, serverId, VesselPermissions.ServersWrite);
        RequireHostLocalAllowed(actorUserId, serverId);
        if (!proxyLifecycle.IsRoutingEnabled(serverId))
            throw new DomainException("Routing is disabled because the server proxy type is None.");
        Server server = LoadReachableServer(serverId);

        await using DistributedLockHandle handle = await AcquireAsync(serverId, TimeSpan.Zero, cancellationToken);
        var contents = protector.Reveal(serverId, source.Configuration);
        DateTimeOffset now = timeProvider.GetUtcNow();
        ProxyApplyResult result;
        ProxyConfigurationVersion restored;
        if (source.FileName is null)
        {
            var document = new ProxyConfigurationDocument(serverId, source.Provider, source.Version, contents,
                source.ConfigurationHash, []);
            ProxyValidationResult validation = proxyProvider.Validate(document);
            if (!validation.Succeeded)
                throw new DomainException(string.Join("; ", validation.Errors.Select(error => redactor.Redact(error))));
            ProxyConfigurationVersion? current = CurrentGeneratedVersion(serverId);
            restored = ProxyConfigurationVersion.Create(serverId, source.Provider, source.Version,
                source.ConfigurationHash, protector.Protect(serverId, contents), current?.Id, now,
                ProxyConfigurationSource.Restore, null, source.Id);
            result = await proxyLifecycle.DeliverDynamicConfigurationAsync(server, document, cancellationToken);
        }
        else
        {
            ProxyDynamicConfiguration.ValidateFileName(source.FileName);
            ProxyValidationResult validation = proxyProvider.ValidateUserDynamicConfiguration(contents);
            if (!validation.Succeeded)
                throw new DomainException(string.Join("; ", validation.Errors.Select(error => redactor.Redact(error))));
            ProxyConfigurationVersion? current = CurrentUserVersion(serverId, source.FileName);
            restored = ProxyConfigurationVersion.Create(serverId, source.Provider, source.Version,
                source.ConfigurationHash, protector.Protect(serverId, contents), current?.Id, now,
                ProxyConfigurationSource.Restore, source.FileName, source.Id);
            result = await proxyLifecycle.DeliverUserDynamicConfigurationAsync(server, source.FileName, contents,
                cancellationToken);
            if (result.Succeeded)
                await UpsertDynamicConfigurationAsync(serverId, source.FileName, contents, now, cancellationToken);
        }

        restored.MarkValidated(now);
        await dbContext.ProxyConfigurationVersionRepository.AddAsync(restored, cancellationToken);
        if (!result.Succeeded)
        {
            restored.MarkApplyFailed(redactor.Redact(result.Message), timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(cancellationToken);
            throw new DomainException(restored.ApplyError ?? "Proxy configuration restore failed.");
        }

        restored.MarkApplied(timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.ProxyConfigurationRestored,
            new AuditTarget("proxy-configuration", restored.Id.Value.ToString("D")), null,
            new Dictionary<string, object?>
            {
                ["serverId"] = serverId.Value.ToString("D"),
                ["restoredFromId"] = source.Id.Value.ToString("D"),
                ["fileName"] = source.FileName,
                ["configurationHash"] = source.ConfigurationHash
            }, cancellationToken);
        return ToSummary(restored);
    }

    /// <summary>Kept for API compatibility: a rollback is a restore (a new version), never a history rewrite.</summary>
    public Task<ProxyConfigurationSummary> RollbackAsync(
        UserId actorUserId,
        TeamId teamId,
        ServerId serverId,
        ProxyConfigurationVersionId versionId,
        CancellationToken cancellationToken = default)
    {
        return RestoreAsync(actorUserId, teamId, serverId, versionId, cancellationToken);
    }

    public IReadOnlyList<ProxyDynamicConfigurationSummary> ListDynamicConfigurations(
        UserId actorUserId,
        TeamId teamId,
        ServerId serverId)
    {
        RequireServer(actorUserId, teamId, serverId, VesselPermissions.ServersRead);
        return dbContext.ProxyDynamicConfigurations
            .Where(configuration => configuration.ServerId == serverId)
            .OrderBy(configuration => configuration.FileName)
            .AsEnumerable()
            .Select(ToSummary)
            .ToArray();
    }

    public async Task<ProxyDynamicConfigurationContent> GetDynamicConfigurationAsync(
        UserId actorUserId,
        TeamId teamId,
        ServerId serverId,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        RequireServer(actorUserId, teamId, serverId, VesselPermissions.ServersWrite);
        var name = ProxyDynamicConfiguration.ValidateFileName(fileName);
        ProxyDynamicConfiguration configuration = FindDynamicConfiguration(serverId, name)
                                                  ?? throw new InvalidOperationException(
                                                      "Dynamic configuration was not found.");
        var content = protector.Reveal(serverId, configuration.Content);
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.ProxyConfigurationRevealed,
            new AuditTarget("proxy-dynamic-configuration", configuration.Id.Value.ToString("D")), null,
            new Dictionary<string, object?> { ["serverId"] = serverId.Value.ToString("D"), ["fileName"] = name },
            cancellationToken);
        return new ProxyDynamicConfigurationContent(ToSummary(configuration), content);
    }

    /// <summary>
    ///     Validates (file-name rule and YAML parse), delivers and records a user dynamic configuration file. Raw proxy
    ///     configuration can route any host on the server, so this requires server write access and, for host-local
    ///     servers, an instance administrator.
    /// </summary>
    public async Task<ProxyDynamicConfigurationSummary> SaveDynamicConfigurationAsync(
        UserId actorUserId,
        TeamId teamId,
        ServerId serverId,
        string fileName,
        SaveProxyDynamicConfigurationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireServer(actorUserId, teamId, serverId, VesselPermissions.ServersWrite);
        RequireHostLocalAllowed(actorUserId, serverId);
        if (!proxyLifecycle.IsRoutingEnabled(serverId))
            throw new DomainException("Routing is disabled because the server proxy type is None.");
        var name = ProxyDynamicConfiguration.ValidateFileName(fileName);
        var content = (request.Content ?? string.Empty).ReplaceLineEndings("\n");
        if (content.Length > ProxyDynamicConfiguration.MaxContentLength / 2)
            throw new DomainException("Dynamic configuration is too large.");
        ProxyValidationResult validation = proxyProvider.ValidateUserDynamicConfiguration(content);
        if (!validation.Succeeded)
            throw new DomainException(string.Join("; ", validation.Errors.Select(error => redactor.Redact(error))));
        Server server = LoadReachableServer(serverId);

        await using DistributedLockHandle handle = await AcquireAsync(serverId, TimeSpan.Zero, cancellationToken);
        DateTimeOffset now = timeProvider.GetUtcNow();
        var hash = ProxyConfigurationProtector.Hash(content);
        ProxyConfigurationVersion? current = CurrentUserVersion(serverId, name);
        var version = ProxyConfigurationVersion.Create(serverId, ProxyProviderKind.Traefik, hash[..16], hash,
            protector.Protect(serverId, content), current?.Id, now, ProxyConfigurationSource.UserDynamic, name);
        version.MarkValidated(now);
        await dbContext.ProxyConfigurationVersionRepository.AddAsync(version, cancellationToken);

        ProxyApplyResult result =
            await proxyLifecycle.DeliverUserDynamicConfigurationAsync(server, name, content, cancellationToken);
        if (!result.Succeeded)
        {
            version.MarkApplyFailed(redactor.Redact(result.Message), timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(cancellationToken);
            throw new DomainException(version.ApplyError ?? "Dynamic configuration could not be delivered.");
        }

        version.MarkApplied(timeProvider.GetUtcNow());
        ProxyDynamicConfiguration configuration =
            await UpsertDynamicConfigurationAsync(serverId, name, content, now, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.ProxyDynamicConfigurationSaved,
            new AuditTarget("server", serverId.Value.ToString("D")), null,
            new Dictionary<string, object?> { ["fileName"] = name, ["contentHash"] = hash }, cancellationToken);
        return ToSummary(configuration);
    }

    public async Task DeleteDynamicConfigurationAsync(
        UserId actorUserId,
        TeamId teamId,
        ServerId serverId,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        RequireServer(actorUserId, teamId, serverId, VesselPermissions.ServersWrite);
        RequireHostLocalAllowed(actorUserId, serverId);
        var name = ProxyDynamicConfiguration.ValidateFileName(fileName);
        ProxyDynamicConfiguration configuration = FindDynamicConfiguration(serverId, name)
                                                  ?? throw new InvalidOperationException(
                                                      "Dynamic configuration was not found.");
        Server server = LoadReachableServer(serverId);

        await using DistributedLockHandle handle = await AcquireAsync(serverId, TimeSpan.Zero, cancellationToken);
        ProxyApplyResult result =
            await proxyLifecycle.RemoveUserDynamicConfigurationAsync(server, name, cancellationToken);
        if (!result.Succeeded)
            throw new DomainException(redactor.Redact(result.Message));
        dbContext.ProxyDynamicConfigurationRepository.Remove(configuration);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.ProxyDynamicConfigurationRemoved,
            new AuditTarget("server", serverId.Value.ToString("D")), null,
            new Dictionary<string, object?> { ["fileName"] = name }, cancellationToken);
    }

    /// <summary>
    ///     History retention: per server keep the newest <see cref="HistoryRetention" /> versions plus the applied version
    ///     of the generated routes and of each user dynamic file. Everything else is deleted.
    /// </summary>
    public async Task<int> PruneHistoryAsync(int retention = HistoryRetention,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retention, 1);
        ServerId[] serverIds = dbContext.ProxyConfigurationVersions
            .Select(version => version.ServerId)
            .Distinct()
            .ToArray();
        var removed = 0;
        foreach (ServerId serverId in serverIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProxyConfigurationVersion[] versions = dbContext.ProxyConfigurationVersions
                .Where(version => version.ServerId == serverId)
                .OrderByDescending(version => version.CreatedAt)
                .ToArray();
            if (versions.Length <= retention) continue;

            var keep = versions.Take(retention).Select(version => version.Id).ToHashSet();
            foreach (IGrouping<string, ProxyConfigurationVersion> file in versions.GroupBy(version =>
                         version.FileName ?? string.Empty))
            {
                ProxyConfigurationVersion? applied = file
                    .Where(version => version.Status == ProxyConfigurationStatus.Applied)
                    .OrderByDescending(version => version.AppliedAt)
                    .FirstOrDefault();
                if (applied is not null) keep.Add(applied.Id);
            }

            ProxyConfigurationVersion[] stale = versions.Where(version => !keep.Contains(version.Id)).ToArray();
            foreach (ProxyConfigurationVersion version in stale)
                dbContext.ProxyConfigurationVersionRepository.Remove(version);
            if (stale.Length == 0) continue;
            await dbContext.SaveChangesAsync(cancellationToken);
            removed += stale.Length;
            await auditWriter.RecordAsync(null, null, AuditActions.ProxyConfigurationPruned,
                new AuditTarget("server", serverId.Value.ToString("D")), null,
                new Dictionary<string, object?> { ["removed"] = stale.Length, ["retention"] = retention },
                cancellationToken);
        }

        return removed;
    }

    private async Task<ProxyDynamicConfiguration> UpsertDynamicConfigurationAsync(
        ServerId serverId,
        string fileName,
        string content,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var hash = ProxyConfigurationProtector.Hash(content);
        ProxyDynamicConfiguration? existing = FindDynamicConfiguration(serverId, fileName);
        if (existing is not null)
        {
            existing.Replace(protector.Protect(serverId, content), hash, now);
            return existing;
        }

        var created = ProxyDynamicConfiguration.Create(serverId, fileName, protector.Protect(serverId, content), hash,
            now);
        await dbContext.ProxyDynamicConfigurationRepository.AddAsync(created, cancellationToken);
        return created;
    }

    private ProxyDynamicConfiguration? FindDynamicConfiguration(ServerId serverId, string fileName)
    {
        return dbContext.ProxyDynamicConfigurations.SingleOrDefault(configuration =>
            configuration.ServerId == serverId && configuration.FileName == fileName);
    }

    private ProxyConfigurationVersion? CurrentGeneratedVersion(ServerId serverId)
    {
        return dbContext.ProxyConfigurationVersions
            .Where(version => version.ServerId == serverId && version.FileName == null &&
                              version.Status == ProxyConfigurationStatus.Applied)
            .OrderByDescending(version => version.AppliedAt)
            .FirstOrDefault();
    }

    private ProxyConfigurationVersion? CurrentUserVersion(ServerId serverId, string fileName)
    {
        return dbContext.ProxyConfigurationVersions
            .Where(version => version.ServerId == serverId && version.FileName == fileName &&
                              version.Status == ProxyConfigurationStatus.Applied)
            .OrderByDescending(version => version.AppliedAt)
            .FirstOrDefault();
    }

    private ProxyConfigurationDocument? RevealDocument(ProxyConfigurationVersion? version)
    {
        if (version is null) return null;
        try
        {
            return new ProxyConfigurationDocument(version.ServerId, version.Provider, version.Version,
                protector.Reveal(version.ServerId, version.Configuration), version.ConfigurationHash, []);
        }
        catch (SecretDecryptionException)
        {
            return null;
        }
    }

    private async Task AuditAppliedAsync(
        TeamId teamId,
        UserId? actorUserId,
        ServerId serverId,
        ProxyConfigurationDocument document,
        int routeCount,
        ProxyApplyResult apply,
        IReadOnlyList<ProxyRouteRejection> rejections,
        CancellationToken cancellationToken)
    {
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.ProxyConfigurationApplied,
            new AuditTarget("server", serverId.Value.ToString("D")), null,
            new Dictionary<string, object?>
            {
                ["provider"] = document.Provider.ToString(),
                ["version"] = document.Version,
                ["configurationHash"] = document.Sha256Hash,
                ["routeCount"] = routeCount,
                ["delivery"] = redactor.Redact(apply.Message),
                ["rejectedRoutes"] = rejections
                    .Select(rejection => new
                    {
                        host = rejection.Route.Identity, reason = redactor.Redact(rejection.Reason)
                    })
                    .ToArray()
            }, cancellationToken);
    }

    private ProxyConfigurationVersion LoadVersion(ServerId serverId, ProxyConfigurationVersionId versionId)
    {
        ProxyConfigurationVersion version =
            dbContext.ProxyConfigurationVersions.SingleOrDefault(item => item.Id == versionId)
            ?? throw new InvalidOperationException("Proxy configuration version was not found.");
        if (version.ServerId != serverId)
            throw new UnauthorizedAccessException("Proxy configuration version is outside the requested server.");
        return version;
    }

    private Server LoadReachableServer(ServerId serverId)
    {
        Server server = dbContext.Servers.SingleOrDefault(item => item.Id == serverId)
                        ?? throw new InvalidOperationException("Server was not found.");
        if (server.Status == ServerStatus.Unreachable)
            throw new DomainException("Cannot apply proxy configuration while server is unreachable.");
        return server;
    }

    private async Task<DistributedLockHandle> AcquireAsync(
        ServerId serverId,
        TimeSpan wait,
        CancellationToken cancellationToken)
    {
        return await locks.TryAcquireAsync(ProxyLifecycleService.LockKey(serverId), ProxyLifecycleService.LockLease,
                   wait, cancellationToken)
               ?? throw new DomainException("A proxy configuration operation is already running for this server.");
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
            version.ValidationError, version.ApplyError, version.CreatedAt, version.AppliedAt, version.Source,
            version.FileName, version.RestoredFromId?.Value);
    }

    private static ProxyDynamicConfigurationSummary ToSummary(ProxyDynamicConfiguration configuration)
    {
        return new ProxyDynamicConfigurationSummary(configuration.Id.Value, configuration.ServerId.Value,
            configuration.FileName, configuration.ContentHash, configuration.CreatedAt, configuration.UpdatedAt);
    }
}

/// <summary>Hangfire entry point for proxy configuration history retention.</summary>
public sealed class ProxyConfigurationHistoryCleanupJob(ProxyConfigurationService proxyConfigurations)
{
    public Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        return proxyConfigurations.PruneHistoryAsync(ProxyConfigurationService.HistoryRetention, cancellationToken);
    }
}
