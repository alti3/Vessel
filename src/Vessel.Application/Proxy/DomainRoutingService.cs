using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Persistence;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Certificates;
using Vessel.Domain.Common;
using Vessel.Domain.ValueObjects;
using AppId = Vessel.Domain.ApplicationId;

namespace Vessel.Application.Proxy;

public sealed class DomainRoutingService(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    IAuditWriter auditWriter,
    ISecretRedactor redactor,
    TimeProvider timeProvider)
{
    public IReadOnlyList<DomainRouteSummary> List(UserId actorUserId, TeamId teamId, AppId applicationId)
    {
        RequireApplication(actorUserId, teamId, applicationId, VesselPermissions.ApplicationsRead);
        Domain.Applications.Application application = GetApplication(applicationId);
        Certificate[] certificates = dbContext.Certificates
            .Where(certificate => certificate.ApplicationId == applicationId).ToArray();
        return application.Domains
            .OrderBy(domain => domain.DomainName.Value, StringComparer.OrdinalIgnoreCase)
            .Select(domain =>
            {
                Certificate? certificate = certificates.SingleOrDefault(item =>
                    string.Equals(item.Host, domain.DomainName.Value, StringComparison.OrdinalIgnoreCase));
                return new DomainRouteSummary(domain.DomainName.Value, domain.TargetPort, domain.TlsEnabled,
                    domain.Canonical, domain.RedirectToCanonical, certificate?.Status, certificate?.Provider,
                    certificate?.RenewalDueAt, certificate?.LastError);
            })
            .ToArray();
    }

    public async Task<DomainRouteSummary> ConfigureAsync(
        UserId actorUserId,
        TeamId teamId,
        AppId applicationId,
        ConfigureDomainRouteRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireApplication(actorUserId, teamId, applicationId, VesselPermissions.ApplicationsWrite);
        Domain.Applications.Application application = GetApplication(applicationId);
        var domainName = new DomainName(NormalizeHost(request.Host));
        DateTimeOffset now = timeProvider.GetUtcNow();
        application.UpsertDomain(domainName, request.TargetPort, request.TlsEnabled, request.Canonical,
            request.RedirectToCanonical, now);

        if (request.TlsEnabled && !dbContext.Certificates.Any(certificate =>
                certificate.ApplicationId == applicationId && certificate.Host == domainName.Value))
            await dbContext.CertificateRepository.AddAsync(
                Certificate.Create(teamId, applicationId, domainName.Value, CertificateProvider.TraefikAcme, now),
                cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.DomainRouteConfigured,
            new AuditTarget("application", applicationId.Value.ToString("D")), null,
            new Dictionary<string, object?>
            {
                ["host"] = domainName.Value,
                ["tlsEnabled"] = request.TlsEnabled,
                ["targetPort"] = request.TargetPort
            }, cancellationToken);

        return List(actorUserId, teamId, applicationId).Single(route => route.Host == domainName.Value);
    }

    public async Task RemoveAsync(
        UserId actorUserId,
        TeamId teamId,
        AppId applicationId,
        string host,
        CancellationToken cancellationToken = default)
    {
        RequireApplication(actorUserId, teamId, applicationId, VesselPermissions.ApplicationsWrite);
        Domain.Applications.Application application = GetApplication(applicationId);
        var normalized = NormalizeHost(host);
        application.RemoveDomain(new DomainName(normalized), timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.DomainRouteRemoved,
            new AuditTarget("application", applicationId.Value.ToString("D")), null,
            new Dictionary<string, object?> { ["host"] = redactor.Redact(normalized) }, cancellationToken);
    }

    private Domain.Applications.Application GetApplication(AppId applicationId)
    {
        Domain.Applications.Application application =
            dbContext.Applications.SingleOrDefault(application => application.Id == applicationId)
            ?? throw new InvalidOperationException("Application was not found.");
        _ = dbContext.ApplicationDomains.Where(domain => domain.ApplicationId == applicationId).ToArray();
        return application;
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
        if (string.IsNullOrWhiteSpace(host)) throw new DomainException("Domain host is required.");
        var candidate = host.Trim().TrimEnd('/');
        if (Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri))
            candidate = uri.Host;
        candidate = candidate.Trim().ToLowerInvariant();
        if (candidate.Contains('/', StringComparison.Ordinal) || candidate.Contains(':', StringComparison.Ordinal))
            throw new DomainException("Domain host must not include a path or port.");
        return candidate;
    }
}
