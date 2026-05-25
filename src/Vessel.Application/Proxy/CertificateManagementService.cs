using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Jobs;
using Vessel.Application.Persistence;
using Vessel.Application.Redis;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Certificates;
using Vessel.Domain.Common;
using Vessel.Domain.ValueObjects;
using AppId = Vessel.Domain.ApplicationId;

namespace Vessel.Application.Proxy;

public sealed class CertificateManagementService(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    IDistributedLockManager locks,
    IBackgroundJobDispatcher backgroundJobs,
    ProxyConfigurationService proxyConfiguration,
    IAuditWriter auditWriter,
    ISecretRedactor redactor,
    TimeProvider timeProvider)
{
    public IReadOnlyList<CertificateSummary> List(UserId actorUserId, TeamId teamId, AppId applicationId)
    {
        RequireApplication(actorUserId, teamId, applicationId, VesselPermissions.ApplicationsRead);
        return dbContext.Certificates
            .Where(certificate => certificate.ApplicationId == applicationId)
            .OrderBy(certificate => certificate.Host)
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
        var normalized = NormalizeHost(host);
        await using DistributedLockHandle? handle = await locks.TryAcquireAsync(
            $"certificate-issuance:{applicationId.Value:D}:{normalized}",
            TimeSpan.FromMinutes(5),
            TimeSpan.Zero,
            cancellationToken);
        if (handle is null)
            throw new DomainException("A certificate issuance operation is already running for this host.");

        Certificate? existing = dbContext.Certificates.SingleOrDefault(item =>
            item.ApplicationId == applicationId && item.Host == normalized);
        Certificate certificate = existing
                                  ?? Certificate.Create(teamId, applicationId, normalized,
                                      CertificateProvider.TraefikAcme, timeProvider.GetUtcNow());
        if (existing is null) await dbContext.CertificateRepository.AddAsync(certificate, cancellationToken);

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

        Domain.Applications.Application application = dbContext.Applications
                                                          .SingleOrDefault(item => item.Id == certificate.ApplicationId)
                                                      ?? throw new InvalidOperationException(
                                                          "Application was not found.");

        await proxyConfiguration.ApplyForDeploymentAsync(
            null,
            certificate.TeamId,
            application.ServerId,
            cancellationToken);

        return ToSummary(certificate);
    }

    public async Task<int> RenewDueAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        Certificate[] due = dbContext.Certificates
            .Where(certificate => certificate.Status == CertificateStatus.RenewalDue ||
                                  (certificate.RenewalDueAt != null && certificate.RenewalDueAt <= now))
            .ToArray();
        var renewed = 0;
        foreach (Certificate certificate in due)
        {
            await using DistributedLockHandle? handle = await locks.TryAcquireAsync(
                $"certificate:{certificate.Id.Value:D}",
                TimeSpan.FromMinutes(10),
                TimeSpan.Zero,
                cancellationToken);
            if (handle is null) continue;

            try
            {
                certificate.QueueRenewal(now);
                renewed++;
            }
            catch (Exception ex)
            {
                certificate.MarkFailed(redactor.Redact(ex.Message), now);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return renewed;
    }

    public async Task<CertificateSummary> FinalizeIssuanceAsync(
        CertificateId certificateId,
        DateTimeOffset expiresAt,
        SecretReferenceId? certificateSecretReferenceId,
        SecretReferenceId? privateKeySecretReferenceId,
        CancellationToken cancellationToken = default)
    {
        Certificate certificate = dbContext.Certificates.SingleOrDefault(item => item.Id == certificateId)
                                  ?? throw new InvalidOperationException("Certificate was not found.");
        DateTimeOffset now = timeProvider.GetUtcNow();
        certificate.MarkIssued(now, expiresAt, certificateSecretReferenceId, privateKeySecretReferenceId);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToSummary(certificate);
    }

    private void RequireApplication(UserId actorUserId, TeamId teamId, AppId applicationId, string permission)
    {
        if (!authorization.HasPermission(actorUserId, teamId, permission))
            throw new UnauthorizedAccessException($"Missing required permission '{permission}'.");
        if (!authorization.CanAccessApplication(actorUserId, applicationId))
            throw new UnauthorizedAccessException("Application is outside the active team.");
    }

    private static string NormalizeHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new DomainException("Certificate host is required.");
        if (Uri.TryCreate(host, UriKind.Absolute, out Uri? uri))
            host = uri.Host;
        var normalized = host.Trim().TrimEnd('/').ToLowerInvariant();
        if (normalized.Contains('/', StringComparison.Ordinal) || normalized.Contains(':', StringComparison.Ordinal))
            throw new DomainException("Certificate host must not include a path or port.");
        return new DomainName(normalized).Value;
    }

    private static CertificateSummary ToSummary(Certificate certificate)
    {
        return new CertificateSummary(certificate.Id.Value, certificate.ApplicationId.Value, certificate.Host,
            certificate.Provider, certificate.Status, certificate.ExpiresAt, certificate.RenewalDueAt,
            certificate.LastError);
    }
}

public sealed class CertificateRenewalJob(CertificateManagementService certificates)
{
    public Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        return certificates.RenewDueAsync(cancellationToken);
    }
}

public sealed class CertificateIssuanceJob(CertificateManagementService certificates)
{
    public Task<CertificateSummary> RunAsync(Guid certificateId, CancellationToken cancellationToken = default)
    {
        return certificates.RequestIssuanceAsync(new CertificateId(certificateId), cancellationToken);
    }
}
