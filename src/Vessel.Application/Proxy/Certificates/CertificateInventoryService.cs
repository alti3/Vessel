using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Notifications;
using Vessel.Application.Persistence;
using Vessel.Application.Redis;
using Vessel.Application.Security;
using Vessel.Application.Servers;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Certificates;
using Vessel.Domain.Common;
using Vessel.Domain.Servers;
using ApplicationId = Vessel.Domain.ApplicationId;

namespace Vessel.Application.Proxy.Certificates;

public sealed record CertificateSyncResult(Guid ServerId, int Certificates, int Pending, int Removed, int Alerts);

public sealed record CertificateDeleteResult(int Deleted, string BackupName, bool RestartRequired);

/// <summary>
///     Certificate truth from Traefik's <c>acme.json</c> (upstream <c>TraefikAcmeService</c>): reconciles
///     <see cref="Certificate" /> rows (issued/expiry/SANs/in-use), raises an "expiring soon" alert when Traefik has not
///     renewed an in-use certificate, and deletes certificates or restores <c>acme.json</c> backups. Deleting does not
///     revoke a certificate; Traefik keeps renewing whatever <c>acme.json</c> holds until the proxy restarts without it.
///     Every operation that touches <c>acme.json</c> runs under the <c>proxy:{serverId}</c> lock.
/// </summary>
public sealed class CertificateInventoryService(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    IDistributedLockManager locks,
    ITraefikAcmeStore acmeStore,
    ProxyRouteProjector routeProjector,
    ProxyLifecycleService proxyLifecycle,
    IAuditWriter auditWriter,
    ISecretRedactor redactor,
    TimeProvider timeProvider,
    INotificationEventPublisher notifications)
{
    /// <summary>Traefik renews 30 days before expiry; a certificate still this close to expiry was not renewed.</summary>
    public static readonly TimeSpan ExpiryAlertWindow = TimeSpan.FromDays(14);

    private static readonly TimeSpan SyncLockWait = TimeSpan.FromSeconds(10);

    public IReadOnlyList<CertificateSummary> ListForServer(UserId actorUserId, TeamId teamId, ServerId serverId)
    {
        RequireServer(actorUserId, teamId, serverId, VesselPermissions.ServersRead);
        return dbContext.Certificates
            .Where(certificate => certificate.ServerId == serverId)
            .OrderBy(certificate => certificate.Host)
            .AsEnumerable()
            .Select(CertificateManagementService.ToSummary)
            .ToArray();
    }

    /// <summary>Operator-triggered sync of one server.</summary>
    public async Task<CertificateSyncResult> SyncServerAsync(
        UserId actorUserId,
        TeamId teamId,
        ServerId serverId,
        CancellationToken cancellationToken = default)
    {
        RequireServer(actorUserId, teamId, serverId, VesselPermissions.ServersWrite);
        Server server = LoadServer(serverId);
        HostLocalExecutionGuard.RequireInstanceAdmin(authorization, actorUserId, server);
        await using DistributedLockHandle handle = await AcquireAsync(serverId, SyncLockWait, cancellationToken);
        return await SyncCoreAsync(server, cancellationToken);
    }

    /// <summary>
    ///     Background sync of every reachable server that has a managed proxy. A busy or failing server is skipped and
    ///     retried on the next run. Returns the number of servers synced.
    /// </summary>
    public async Task<int> SyncAllAsync(CancellationToken cancellationToken = default)
    {
        Server[] servers = dbContext.Servers.Where(server => server.Status != ServerStatus.Unreachable).ToArray();
        var synced = 0;
        foreach (Server server in servers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!proxyLifecycle.IsRoutingEnabled(server.Id)) continue;
            await using DistributedLockHandle? handle = await locks.TryAcquireAsync(
                ProxyLifecycleService.LockKey(server.Id), ProxyLifecycleService.LockLease, TimeSpan.Zero,
                cancellationToken);
            if (handle is null) continue;
            try
            {
                await SyncCoreAsync(server, cancellationToken);
                synced++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Reading acme.json can fail while the server is briefly unavailable; the next run retries.
            }
        }

        return synced;
    }

    public async Task<CertificateDeleteResult> DeleteAsync(
        UserId actorUserId,
        TeamId teamId,
        ServerId serverId,
        CertificateId certificateId,
        CancellationToken cancellationToken = default)
    {
        Server server = RequireServerWrite(actorUserId, teamId, serverId);
        Certificate certificate = dbContext.Certificates.SingleOrDefault(item => item.Id == certificateId)
                                  ?? throw new InvalidOperationException("Certificate was not found.");
        if (certificate.ServerId != serverId)
            throw new UnauthorizedAccessException("Certificate is outside the requested server.");
        if (certificate.AcmeKey is null)
            throw new DomainException("The certificate has not been issued; there is nothing to delete.");

        await using DistributedLockHandle handle = await AcquireAsync(serverId, TimeSpan.Zero, cancellationToken);
        AcmeWriteResult write = await WriteAcmeAsync(() =>
            acmeStore.DeleteCertificatesAsync(ServerExecutionTarget.From(server), [certificate.AcmeKey],
                cancellationToken));
        var host = certificate.Host;
        await SyncCoreAsync(server, cancellationToken);
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.CertificateDeleted,
            new AuditTarget("server", serverId.Value.ToString("D")), null,
            new Dictionary<string, object?>
            {
                ["host"] = host,
                ["certificateId"] = certificateId.Value.ToString("D"),
                ["backup"] = write.BackupName
            }, cancellationToken);
        return new CertificateDeleteResult(write.RemovedCertificates, write.BackupName, write.RestartRequired);
    }

    /// <summary>Deletes every certificate that no route on the server uses (wildcards cover one label).</summary>
    public async Task<CertificateDeleteResult> DeleteUnusedAsync(
        UserId actorUserId,
        TeamId teamId,
        ServerId serverId,
        CancellationToken cancellationToken = default)
    {
        Server server = RequireServerWrite(actorUserId, teamId, serverId);
        await using DistributedLockHandle handle = await AcquireAsync(serverId, TimeSpan.Zero, cancellationToken);
        var target = ServerExecutionTarget.From(server);
        IReadOnlySet<string> hosts = routeProjector.HostsForServer(serverId);
        IReadOnlyList<AcmeCertificateEntry> entries = await acmeStore.ListCertificatesAsync(target, cancellationToken);
        string[] unused = entries.Where(entry => !IsInUse(entry, hosts)).Select(entry => entry.Id).ToArray();
        if (unused.Length == 0)
            return new CertificateDeleteResult(0, string.Empty, false);

        AcmeWriteResult write = await WriteAcmeAsync(() =>
            acmeStore.DeleteCertificatesAsync(target, unused, cancellationToken));
        await SyncCoreAsync(server, cancellationToken);
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.CertificateDeleted,
            new AuditTarget("server", serverId.Value.ToString("D")), null,
            new Dictionary<string, object?>
            {
                ["unused"] = true,
                ["deleted"] = write.RemovedCertificates,
                ["backup"] = write.BackupName
            }, cancellationToken);
        return new CertificateDeleteResult(write.RemovedCertificates, write.BackupName, write.RestartRequired);
    }

    public async Task<IReadOnlyList<AcmeBackup>> ListBackupsAsync(
        UserId actorUserId,
        TeamId teamId,
        ServerId serverId,
        CancellationToken cancellationToken = default)
    {
        RequireServer(actorUserId, teamId, serverId, VesselPermissions.ServersRead);
        Server server = LoadServer(serverId);
        return await acmeStore.ListBackupsAsync(ServerExecutionTarget.From(server), cancellationToken);
    }

    public async Task<CertificateDeleteResult> RestoreBackupAsync(
        UserId actorUserId,
        TeamId teamId,
        ServerId serverId,
        string backupName,
        CancellationToken cancellationToken = default)
    {
        Server server = RequireServerWrite(actorUserId, teamId, serverId);
        await using DistributedLockHandle handle = await AcquireAsync(serverId, TimeSpan.Zero, cancellationToken);
        AcmeWriteResult write = await WriteAcmeAsync(() =>
            acmeStore.RestoreBackupAsync(ServerExecutionTarget.From(server), backupName, cancellationToken));
        await SyncCoreAsync(server, cancellationToken);
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.AcmeBackupRestored,
            new AuditTarget("server", serverId.Value.ToString("D")), null,
            new Dictionary<string, object?> { ["restored"] = backupName, ["backup"] = write.BackupName },
            cancellationToken);
        return new CertificateDeleteResult(0, write.BackupName, write.RestartRequired);
    }

    public async Task DeleteBackupAsync(
        UserId actorUserId,
        TeamId teamId,
        ServerId serverId,
        string backupName,
        CancellationToken cancellationToken = default)
    {
        Server server = RequireServerWrite(actorUserId, teamId, serverId);
        await using DistributedLockHandle handle = await AcquireAsync(serverId, TimeSpan.Zero, cancellationToken);
        await WriteAcmeAsync(async () =>
        {
            await acmeStore.DeleteBackupAsync(ServerExecutionTarget.From(server), backupName, cancellationToken);
            return new AcmeWriteResult(0, backupName, false);
        });
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.AcmeBackupDeleted,
            new AuditTarget("server", serverId.Value.ToString("D")), null,
            new Dictionary<string, object?> { ["backup"] = backupName }, cancellationToken);
    }

    /// <summary>A certificate is in use when a routed host matches its main domain or a SAN (wildcards: one label).</summary>
    public static bool IsInUse(AcmeCertificateEntry entry, IReadOnlySet<string> hosts)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(hosts);
        return entry.AllNames.Any(name => hosts.Any(host => Covers(name, host)));
    }

    /// <summary>True when certificate name <paramref name="name" /> covers <paramref name="host" />.</summary>
    public static bool Covers(string name, string host)
    {
        var certificateName = name.Trim().ToLowerInvariant();
        var routed = host.Trim().ToLowerInvariant();
        if (string.Equals(certificateName, routed, StringComparison.Ordinal)) return true;
        if (!certificateName.StartsWith("*.", StringComparison.Ordinal)) return false;
        var suffix = certificateName[1..];
        if (!routed.EndsWith(suffix, StringComparison.Ordinal)) return false;
        var label = routed[..^suffix.Length];
        return label.Length > 0 && !label.Contains('.', StringComparison.Ordinal) &&
               !label.Contains('*', StringComparison.Ordinal);
    }

    private async Task<CertificateSyncResult> SyncCoreAsync(Server server, CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        IReadOnlyList<AcmeCertificateEntry> entries =
            await acmeStore.ListCertificatesAsync(ServerExecutionTarget.From(server), cancellationToken);
        IReadOnlyList<ProxyRoute> routes = routeProjector.RoutesForServer(server.Id);
        var hosts = routes.Select(route => route.Host).ToHashSet(StringComparer.Ordinal);
        var tlsHosts = routes.Where(route => route.TlsEnabled).Select(route => route.Host)
            .ToHashSet(StringComparer.Ordinal);
        List<Certificate> rows = dbContext.Certificates.Where(item => item.ServerId == server.Id).ToList();
        var seen = new HashSet<CertificateId>();
        int pending = 0, removed = 0, alerts = 0;

        var entryIds = entries.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        foreach (AcmeCertificateEntry entry in entries)
        {
            var main = entry.MainDomain.Trim().ToLowerInvariant();
            // A renewed certificate gets a new entry id; it keeps the row of the same host (and its alert state).
            Certificate? row = rows.FirstOrDefault(item => item.AcmeKey == entry.Id && !seen.Contains(item.Id))
                               ?? rows.FirstOrDefault(item => item.Host == main && !seen.Contains(item.Id) &&
                                                              (item.AcmeKey == null ||
                                                               !entryIds.Contains(item.AcmeKey)));
            if (row is null)
            {
                row = Certificate.CreatePending(server.TeamId, server.Id, null, main, now);
                await dbContext.CertificateRepository.AddAsync(row, cancellationToken);
                rows.Add(row);
            }

            row.SyncFromAcme(entry.Id, entry.Resolver, main, entry.SubjectAlternativeNames, entry.NotBefore,
                entry.ExpiresAt, IsInUse(entry, hosts), OwningApplication(routes, entry), now);
            seen.Add(row.Id);
        }

        foreach (Certificate row in rows.Where(item => !seen.Contains(item.Id)).ToArray())
        {
            var covered = entries.Any(entry => entry.AllNames.Any(name => Covers(name, row.Host)));
            if (tlsHosts.Contains(row.Host) && !covered)
            {
                if (row.AcmeKey is not null) row.ResetToPending(now);
                else row.MarkPendingSynced(true, OwningApplication(routes, row.Host), now);
                seen.Add(row.Id);
                pending++;
                continue;
            }

            dbContext.CertificateRepository.Remove(row);
            rows.Remove(row);
            removed++;
        }

        foreach (var host in tlsHosts.Where(host =>
                     !entries.Any(entry => entry.AllNames.Any(name => Covers(name, host))) &&
                     !rows.Any(row => row.Host == host)))
        {
            Certificate created = Certificate.CreatePending(server.TeamId, server.Id,
                OwningApplication(routes, host), host, now);
            created.MarkPendingSynced(true, null, now);
            await dbContext.CertificateRepository.AddAsync(created, cancellationToken);
            rows.Add(created);
            pending++;
        }

        List<Certificate> expiring = rows.Where(row => row.NeedsExpiryAlert(now, ExpiryAlertWindow)).ToList();
        foreach (Certificate row in expiring) row.MarkExpiryAlertSent(now);
        await dbContext.SaveChangesAsync(cancellationToken);

        foreach (Certificate row in expiring)
        {
            await notifications.PublishAsync(row.TeamId,
                OperationalNotifications.CertificateExpiringSoon(row.Id.Value, server.Id.Value, server.Name.Value,
                    row.Host, row.ExpiresAt!.Value), CancellationToken.None);
            alerts++;
        }

        return new CertificateSyncResult(server.Id.Value, entries.Count, pending, removed, alerts);
    }

    private static ApplicationId? OwningApplication(IReadOnlyList<ProxyRoute> routes, AcmeCertificateEntry entry)
    {
        ProxyRoute? route = routes.FirstOrDefault(item =>
            item.Endpoint.OwnerKind is RouteOwnerKind.Application or RouteOwnerKind.Preview &&
            entry.AllNames.Any(name => Covers(name, item.Host)));
        return route is null ? null : new ApplicationId(route.Endpoint.OwnerId);
    }

    private static ApplicationId? OwningApplication(IReadOnlyList<ProxyRoute> routes, string host)
    {
        ProxyRoute? route = routes.FirstOrDefault(item =>
            item.Endpoint.OwnerKind is RouteOwnerKind.Application or RouteOwnerKind.Preview &&
            string.Equals(item.Host, host, StringComparison.Ordinal));
        return route is null ? null : new ApplicationId(route.Endpoint.OwnerId);
    }

    private async Task<AcmeWriteResult> WriteAcmeAsync(Func<Task<AcmeWriteResult>> write)
    {
        try
        {
            return await write();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            throw new DomainException(redactor.Redact(ex.Message));
        }
    }

    private Server RequireServerWrite(UserId actorUserId, TeamId teamId, ServerId serverId)
    {
        RequireServer(actorUserId, teamId, serverId, VesselPermissions.ServersWrite);
        Server server = LoadServer(serverId);
        // Editing acme.json of a Local/DockerSocket server writes on the control-plane host.
        HostLocalExecutionGuard.RequireInstanceAdmin(authorization, actorUserId, server);
        if (server.Status == ServerStatus.Unreachable)
            throw new DomainException("Cannot change certificates while the server is unreachable.");
        return server;
    }

    private async Task<DistributedLockHandle> AcquireAsync(
        ServerId serverId,
        TimeSpan wait,
        CancellationToken cancellationToken)
    {
        return await locks.TryAcquireAsync(ProxyLifecycleService.LockKey(serverId), ProxyLifecycleService.LockLease,
                   wait, cancellationToken)
               ?? throw new DomainException("A proxy operation is already running for this server.");
    }

    private Server LoadServer(ServerId serverId)
    {
        return dbContext.Servers.SingleOrDefault(item => item.Id == serverId)
               ?? throw new InvalidOperationException("Server was not found.");
    }

    private void RequireServer(UserId actorUserId, TeamId teamId, ServerId serverId, string permission)
    {
        authorization.Demand(actorUserId, teamId, ResourceRef.Server(serverId), permission);
    }
}

/// <summary>Hangfire entry point: reconciles certificate rows with acme.json on every server.</summary>
public sealed class CertificateSyncJob(CertificateInventoryService inventory)
{
    public Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        return inventory.SyncAllAsync(cancellationToken);
    }
}
