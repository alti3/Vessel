using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Persistence;
using Vessel.Application.Redis;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Certificates;
using Vessel.Domain.Common;
using AppId = Vessel.Domain.ApplicationId;

namespace Vessel.Application.Proxy;

public sealed class CertificateManagementService(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    IDistributedLockManager locks,
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
        string normalized = NormalizeHost(host);
        Certificate? existing = dbContext.Certificates.SingleOrDefault(item =>
            item.ApplicationId == applicationId && item.Host == normalized);
        Certificate certificate = existing
            ?? Certificate.Create(teamId, applicationId, normalized, CertificateProvider.TraefikAcme, timeProvider.GetUtcNow());
        if (existing is null)
            await dbContext.CertificateRepository.AddAsync(certificate, cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.CertificateIssuanceQueued,
            new AuditTarget("certificate", certificate.Id.Value.ToString("D")), null,
            new Dictionary<string, object?> { ["provider"] = certificate.Provider.ToString(), ["host"] = normalized },
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
        int renewed = 0;
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
        if (Uri.TryCreate(host, UriKind.Absolute, out Uri? uri))
            host = uri.Host;
        string normalized = host.Trim().TrimEnd('/').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized)) throw new DomainException("Certificate host is required.");
        return normalized;
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
