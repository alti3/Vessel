using Vessel.Domain;
using Vessel.Domain.Certificates;
using Vessel.Domain.Proxy;
using AppId = Vessel.Domain.ApplicationId;

namespace Vessel.Application.Proxy;

public sealed record ProxyRoute(
    AppId ApplicationId,
    ServerId ServerId,
    string ServiceName,
    string Host,
    int TargetPort,
    bool TlsEnabled,
    bool Canonical,
    bool RedirectToCanonical);

public sealed record ProxyConfigurationDocument(
    ServerId ServerId,
    ProxyProviderKind Provider,
    string Version,
    string Contents,
    string Sha256Hash,
    IReadOnlyList<ProxyRoute> Routes);

public sealed record ProxyValidationResult(bool Succeeded, IReadOnlyList<string> Errors)
{
    public static ProxyValidationResult Success { get; } = new(true, []);
}

public sealed record ProxyApplyResult(bool Succeeded, string Message);

public sealed record ProxyConfigurationSummary(
    Guid Id,
    Guid ServerId,
    ProxyProviderKind Provider,
    string Version,
    string ConfigurationHash,
    ProxyConfigurationStatus Status,
    Guid? PreviousVersionId,
    string? ValidationError,
    string? ApplyError,
    DateTimeOffset CreatedAt,
    DateTimeOffset? AppliedAt);

public sealed record DomainRouteSummary(
    string Host,
    int? TargetPort,
    bool TlsEnabled,
    bool Canonical,
    bool RedirectToCanonical,
    CertificateStatus? CertificateStatus,
    CertificateProvider? CertificateProvider,
    DateTimeOffset? CertificateRenewalDueAt,
    string? CertificateLastError);

public sealed record ConfigureDomainRouteRequest(
    string Host,
    int? TargetPort,
    bool TlsEnabled,
    bool Canonical,
    bool RedirectToCanonical);

public sealed record CertificateSummary(
    Guid Id,
    Guid ApplicationId,
    string Host,
    CertificateProvider Provider,
    CertificateStatus Status,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RenewalDueAt,
    string? LastError);
