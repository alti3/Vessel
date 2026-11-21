using System.Security.Cryptography;
using System.Text;
using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Persistence;
using Vessel.Application.Proxy.Domains;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Applications;
using Vessel.Domain.Auditing;
using Vessel.Domain.Certificates;
using Vessel.Domain.Common;
using Vessel.Domain.Proxy;
using Vessel.Domain.Secrets;
using Vessel.Domain.ValueObjects;
using AppId = Vessel.Domain.ApplicationId;

namespace Vessel.Application.Proxy;

/// <summary>
///     Application routes: parsing (<see cref="RouteAddress" />), save-time conflict detection with an explicit, audited
///     override, per-route policies and the expected certificate rows for TLS hosts.
/// </summary>
public sealed class DomainRoutingService(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    IAuditWriter auditWriter,
    ISecretRedactor redactor,
    TimeProvider timeProvider,
    RoutePolicyWriter policyWriter)
{
    public IReadOnlyList<DomainRouteSummary> List(UserId actorUserId, TeamId teamId, AppId applicationId)
    {
        RequireApplication(actorUserId, teamId, applicationId, VesselPermissions.ApplicationsRead);
        Domain.Applications.Application application = GetApplication(applicationId);
        Certificate[] certificates = dbContext.Certificates
            .Where(certificate => certificate.ServerId == application.ServerId)
            .ToArray();
        return application.Domains
            .OrderBy(domain => domain.Host, StringComparer.OrdinalIgnoreCase)
            .ThenBy(domain => domain.PathPrefix, StringComparer.Ordinal)
            .Select(domain => ToSummary(domain, certificates))
            .ToArray();
    }

    public async Task<DomainRouteSummary> ConfigureAsync(
        UserId actorUserId,
        TeamId teamId,
        AppId applicationId,
        ConfigureDomainRouteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireApplication(actorUserId, teamId, applicationId, VesselPermissions.ApplicationsWrite);
        Domain.Applications.Application application = GetApplication(applicationId);
        RouteAddress address = RouteAddress.Parse(request.Host);
        var tlsEnabled = address.Scheme switch
        {
            RouteScheme.Https => true,
            RouteScheme.Http => false,
            _ => request.TlsEnabled
        };
        if (request.TargetPort is not null && address.Port is not null && request.TargetPort != address.Port)
            throw new DomainException("The port in the domain URL does not match the target port.");
        var targetPort = request.TargetPort ?? address.Port;
        address = address.WithoutPort();

        IReadOnlyList<DomainConflict> conflicts = DomainConflicts.Find(dbContext, teamId, address,
            RouteOwnerKind.Application, applicationId.Value);
        if (conflicts.Count > 0 && !request.ForceDomainOverride)
            throw new DomainException(DomainConflicts.Describe(conflicts));

        DateTimeOffset now = timeProvider.GetUtcNow();
        ApplicationDomain? existing = application.Domains.SingleOrDefault(domain =>
            domain.Host == address.Host && domain.PathPrefix == address.PathPrefix);
        (ProjectId projectId, EnvironmentId environmentId) = ApplicationProject(application);
        // Secrets and certificate rows belong to the team that owns the application, never the caller's active team
        // (an instance administrator may act from another team); deployments reveal secrets with the owning team.
        TeamId ownerTeamId = dbContext.Projects.Where(project => project.Id == projectId)
            .Select(project => project.TeamId).Single();
        RoutePolicy policy = await policyWriter.MergeAsync(existing?.Policy ?? RoutePolicy.Default, request.Policy,
            ownerTeamId, SecretScope.Application, BasicAuthSecretKey(address),
            new SecretTarget(projectId, environmentId, ApplicationId: applicationId), cancellationToken);
        ApplicationDomain domain = application.UpsertDomain(address, targetPort, tlsEnabled, request.Canonical,
            request.RedirectToCanonical, policy, now);

        if (domain.TlsEnabled && !dbContext.Certificates.Any(certificate =>
                certificate.ServerId == application.ServerId && certificate.Host == domain.Host))
            await dbContext.CertificateRepository.AddAsync(
                Certificate.CreatePending(ownerTeamId, application.ServerId, applicationId, domain.Host, now),
                cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.DomainRouteConfigured,
            new AuditTarget("application", applicationId.Value.ToString("D")), null,
            new Dictionary<string, object?>
            {
                ["host"] = domain.Host,
                ["path"] = domain.PathPrefix,
                ["tlsEnabled"] = domain.TlsEnabled,
                ["targetPort"] = domain.TargetPort,
                ["forceHttps"] = policy.ForceHttps,
                ["redirectMode"] = policy.RedirectMode.ToString(),
                ["permanentRedirect"] = policy.PermanentRedirect,
                ["compression"] = policy.Compression,
                ["noIndex"] = policy.NoIndex,
                ["basicAuth"] = policy.BasicAuth is not null,
                ["basicAuthPasswordChanged"] = request.Policy?.BasicAuthPassword is not null
            }, cancellationToken);
        if (conflicts.Count > 0)
            await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.DomainConflictOverridden,
                new AuditTarget("application", applicationId.Value.ToString("D")), null,
                new Dictionary<string, object?>
                {
                    ["identity"] = address.Identity,
                    ["conflicts"] = conflicts
                        .Select(conflict => new { kind = conflict.OwnerKind.ToString(), id = conflict.OwnerId })
                        .ToArray()
                }, cancellationToken);

        Certificate[] certificates = dbContext.Certificates
            .Where(certificate => certificate.ServerId == application.ServerId)
            .ToArray();
        return ToSummary(domain, certificates);
    }

    public async Task RemoveAsync(
        UserId actorUserId,
        TeamId teamId,
        AppId applicationId,
        string address,
        CancellationToken cancellationToken = default)
    {
        RequireApplication(actorUserId, teamId, applicationId, VesselPermissions.ApplicationsWrite);
        Domain.Applications.Application application = GetApplication(applicationId);
        RouteAddress route = RouteAddress.Parse(address).WithoutPort();
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (!application.RemoveDomain(route, now)) return;

        // Expected (never issued) certificates for a host no route uses any more are dropped right away; issued ones
        // are reconciled by the certificate sync job from acme.json.
        AppId[] otherApplications = dbContext.Applications
            .Where(other => other.ServerId == application.ServerId && other.Id != applicationId)
            .Select(other => other.Id)
            .ToArray();
        var stillRouted = application.Domains.Any(domain => domain.Host == route.Host) ||
                          dbContext.ApplicationDomains.Any(domain =>
                              domain.Host == route.Host && otherApplications.Contains(domain.ApplicationId));
        if (!stillRouted)
            foreach (Certificate pending in dbContext.Certificates.Where(certificate =>
                             certificate.ServerId == application.ServerId && certificate.Host == route.Host &&
                             certificate.AcmeKey == null)
                         .ToArray())
                dbContext.CertificateRepository.Remove(pending);

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.DomainRouteRemoved,
            new AuditTarget("application", applicationId.Value.ToString("D")), null,
            new Dictionary<string, object?> { ["host"] = redactor.Redact(route.Identity) }, cancellationToken);
    }

    /// <summary>Secret key of a route's basic-auth password; hashed so long hosts/paths fit the key column.</summary>
    internal static string BasicAuthSecretKey(RouteAddress address)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(address.Identity)))
            .ToLowerInvariant()[..16];
        return $"route-basic-auth:{hash}";
    }

    private static DomainRouteSummary ToSummary(ApplicationDomain domain, IReadOnlyList<Certificate> certificates)
    {
        RouteAddress address = domain.Address;
        Certificate? certificate = domain.TlsEnabled
            ? certificates
                  .Where(item => string.Equals(item.Host, domain.Host, StringComparison.Ordinal))
                  .OrderByDescending(item => item.Status == CertificateStatus.Issued)
                  .FirstOrDefault()
              ?? certificates.FirstOrDefault(item => item.AcmeKey is not null &&
                                                     item.SubjectAlternativeNameList.Prepend(item.Host)
                                                         .Any(name => CertificateCovers(name, domain.Host)))
            : null;
        return new DomainRouteSummary(domain.Host, domain.TargetPort, domain.TlsEnabled, domain.Canonical,
            domain.RedirectToCanonical, certificate?.Status, certificate?.Provider, certificate?.ExpiresAt,
            certificate?.LastError, domain.PathPrefix, domain.HostKind, address.ToUrl(domain.TlsEnabled),
            RoutePolicySummary.From(domain.Policy));
    }

    private static bool CertificateCovers(string certificateName, string host)
    {
        if (string.Equals(certificateName, host, StringComparison.OrdinalIgnoreCase)) return true;
        if (!certificateName.StartsWith("*.", StringComparison.Ordinal)) return false;
        var suffix = certificateName[1..];
        if (!host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
        var label = host[..^suffix.Length];
        return label.Length > 0 && !label.Contains('.', StringComparison.Ordinal);
    }

    private (ProjectId ProjectId, EnvironmentId EnvironmentId) ApplicationProject(
        Domain.Applications.Application application)
    {
        ProjectId projectId = dbContext.Environments
            .Where(environment => environment.Id == application.EnvironmentId)
            .Select(environment => environment.ProjectId)
            .Single();
        return (projectId, application.EnvironmentId);
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
        authorization.Demand(actorUserId, teamId, ResourceRef.Application(applicationId), permission);
    }
}
