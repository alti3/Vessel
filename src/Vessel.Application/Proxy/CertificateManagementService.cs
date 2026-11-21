using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Jobs;
using Vessel.Application.Notifications;
using Vessel.Application.Persistence;
using Vessel.Application.Redis;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Certificates;
using Vessel.Domain.Common;
using Vessel.Domain.ValueObjects;
using AppId = Vessel.Domain.ApplicationId;

namespace Vessel.Application.Proxy;

/// <summary>
///     Application-facing certificate operations. Traefik issues and renews certificates through its ACME resolver once
///     a TLS route is published; Vessel requests issuance by applying the routes and learns the outcome from
///     <c>acme.json</c> through <see cref="Certificates.CertificateInventoryService" />. There is no Vessel-side renewal state
///     machine.
/// </summary>
public sealed class CertificateManagementService(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    IDistributedLockManager locks,
    IBackgroundJobDispatcher backgroundJobs,
    ProxyConfigurationService proxyConfiguration,
    IAuditWriter auditWriter,
    TimeProvider timeProvider,
    INotificationEventPublisher notifications)
{
    public IReadOnlyList<CertificateSummary> List(UserId actorUserId, TeamId teamId, AppId applicationId)
    {
        RequireApplication(actorUserId, teamId, applicationId, VesselPermissions.ApplicationsRead);
        Domain.Applications.Application application = GetApplication(applicationId);
        var hosts = dbContext.ApplicationDomains
            .Where(domain => domain.ApplicationId == applicationId)
            .Select(domain => domain.Host)
            .ToArray();
        return dbContext.Certificates
            .Where(certificate => certificate.ServerId == application.ServerId &&
                                  (certificate.ApplicationId == applicationId || hosts.Contains(certificate.Host)))
            .OrderBy(certificate => certificate.Host)
            .AsEnumerable()
            .Select(ToSummary)
            .ToArray();
    }

    public async Task<CertificateSummary> QueueIssuanceAsync(
        UserId actorUserId,
        TeamId teamId,
        AppId applicationId,
        string host,
        CancellationToken cancellationToken = default)
    {
        RequireApplication(actorUserId, teamId, applicationId, VesselPermissions.ApplicationsWrite);
        Domain.Applications.Application application = GetApplication(applicationId);
        var normalized = NormalizeHost(host);
        await using DistributedLockHandle? handle = await locks.TryAcquireAsync(
            $"certificate-issuance:{application.ServerId.Value:D}:{normalized}",
            TimeSpan.FromMinutes(5),
            TimeSpan.Zero,
            cancellationToken);
        if (handle is null)
            throw new DomainException("A certificate issuance operation is already running for this host.");

        Certificate? existing = dbContext.Certificates.FirstOrDefault(item =>
            item.ServerId == application.ServerId && item.Host == normalized);
        TeamId ownerTeamId = (
                from environment in dbContext.Environments
                join project in dbContext.Projects on environment.ProjectId equals project.Id
                where environment.Id == application.EnvironmentId
                select project.TeamId)
            .Single();
        Certificate certificate = existing ?? Certificate.CreatePending(ownerTeamId, application.ServerId,
            applicationId, normalized, timeProvider.GetUtcNow());
        if (existing is null) await dbContext.CertificateRepository.AddAsync(certificate, cancellationToken);
        certificate.MarkIssuanceRequested(timeProvider.GetUtcNow());

        await dbContext.SaveChangesAsync(cancellationToken);
        var jobId = backgroundJobs.Enqueue<CertificateIssuanceJob>(job =>
            job.RunAsync(certificate.Id.Value, CancellationToken.None));
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.CertificateIssuanceQueued,
            new AuditTarget("certificate", certificate.Id.Value.ToString("D")), null,
            new Dictionary<string, object?>
            {
                ["provider"] = certificate.Provider.ToString(),
                ["host"] = normalized,
                ["jobId"] = jobId
            },
            cancellationToken);
        return ToSummary(certificate);
    }

    /// <summary>
    ///     Publishes the routes of the certificate's server so Traefik requests the certificate. The certificate row is
    ///     updated from <c>acme.json</c> by the next certificate sync.
    /// </summary>
    public async Task<CertificateSummary> RequestIssuanceAsync(
        CertificateId certificateId,
        CancellationToken cancellationToken = default)
    {
        Certificate certificate = dbContext.Certificates.SingleOrDefault(item => item.Id == certificateId)
                                  ?? throw new InvalidOperationException("Certificate was not found.");

        await using DistributedLockHandle? handle = await locks.TryAcquireAsync(
            $"certificate:{certificate.Id.Value:D}",
            TimeSpan.FromMinutes(10),
            TimeSpan.Zero,
            cancellationToken);
        if (handle is null)
            throw new DomainException("A certificate operation is already running for this certificate.");

        try
        {
            await proxyConfiguration.ApplyForDeploymentAsync(null, certificate.TeamId, certificate.ServerId,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await notifications.PublishAsync(certificate.TeamId,
                OperationalNotifications.CertificateFailed(certificate.Id.Value,
                    certificate.ApplicationId?.Value ?? Guid.Empty, certificate.Host, renewal: false),
                CancellationToken.None);
            throw;
        }

        return ToSummary(certificate);
    }

    internal static CertificateSummary ToSummary(Certificate certificate)
    {
        return new CertificateSummary(certificate.Id.Value, certificate.ApplicationId?.Value, certificate.Host,
            certificate.Provider, certificate.Status, certificate.ExpiresAt, certificate.LastError,
            certificate.ServerId.Value, certificate.SubjectAlternativeNameList, certificate.Resolver,
            certificate.InUse, certificate.IssuedAt, certificate.LastSyncedAt);
    }

    private Domain.Applications.Application GetApplication(AppId applicationId)
    {
        return dbContext.Applications.SingleOrDefault(item => item.Id == applicationId)
               ?? throw new InvalidOperationException("Application was not found.");
    }

    private void RequireApplication(UserId actorUserId, TeamId teamId, AppId applicationId, string permission)
    {
        authorization.Demand(actorUserId, teamId, ResourceRef.Application(applicationId), permission);
    }

    private static string NormalizeHost(string host)
    {
        RouteAddress address = RouteAddress.Parse(host);
        if (address.PathPrefix.Length > 0 || address.Port is not null)
            throw new DomainException("Certificate host must not include a path or port.");
        if (address.HostKind is RouteHostKind.IPv4 or RouteHostKind.IPv6)
            throw new DomainException("Certificates cannot be requested for IP address hosts.");
        return address.Host;
    }
}

public sealed class CertificateIssuanceJob(CertificateManagementService certificates)
{
    public Task<CertificateSummary> RunAsync(Guid certificateId, CancellationToken cancellationToken = default)
    {
        return certificates.RequestIssuanceAsync(new CertificateId(certificateId), cancellationToken);
    }
}
