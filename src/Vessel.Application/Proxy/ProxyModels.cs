using Vessel.Domain;
using Vessel.Domain.Certificates;
using Vessel.Domain.Proxy;
using Vessel.Domain.ValueObjects;
using ApplicationId = Vessel.Domain.ApplicationId;

namespace Vessel.Application.Proxy;

/// <summary>Kind of resource that owns a routable endpoint.</summary>
public enum RouteOwnerKind
{
    Application = 0,
    Preview = 1,
    Service = 2
}

/// <summary>
///     The target of a route: a resource's container reachable on the server network under <see cref="UpstreamAlias" />
///     (see <see cref="ProxyNaming" />, never a user-controlled display name) and the internal port it listens on.
///     <see cref="OwnerId" /> groups routes of one resource (canonical redirects, router keys); previews use their
///     application id. <see cref="Component" /> is the Compose service for service-template resources.
/// </summary>
public sealed record RoutableEndpoint(
    RouteOwnerKind OwnerKind,
    Guid OwnerId,
    string UpstreamAlias,
    int TargetPort,
    string? Component = null)
{
    public static RoutableEndpoint ForApplication(ApplicationId applicationId, int targetPort)
    {
        return new RoutableEndpoint(RouteOwnerKind.Application, applicationId.Value,
            ProxyNaming.UpstreamAlias(applicationId), targetPort);
    }
}

/// <summary>
///     Plaintext basic-auth credentials resolved from the secret vault for rendering only. The password is hashed by the
///     provider and never written to logs, audit records or the database; <see cref="ToString" /> is redacted.
/// </summary>
public sealed record ProxyBasicAuthCredentials(string Username, string Password)
{
    public override string ToString()
    {
        return $"ProxyBasicAuthCredentials {{ Username = {Username}, Password = [redacted] }}";
    }
}

/// <summary>
///     A single public route (host + optional path prefix) on the server proxy.
/// </summary>
public sealed record ProxyRoute(
    RoutableEndpoint Endpoint,
    ServerId ServerId,
    string Host,
    string PathPrefix,
    bool TlsEnabled,
    bool Canonical,
    bool RedirectToCanonical,
    RoutePolicy? Policy = null,
    ProxyBasicAuthCredentials? BasicAuth = null)
{
    public int TargetPort => Endpoint.TargetPort;

    public string UpstreamHost => Endpoint.UpstreamAlias;

    /// <summary>Scheme-independent routing identity (host + path).</summary>
    public string Identity => Host + PathPrefix;

    public RoutePolicy EffectivePolicy => Policy ?? RoutePolicy.Default;

    /// <summary>Convenience constructor for an application route at the root path.</summary>
    public static ProxyRoute ForApplication(
        ApplicationId applicationId,
        ServerId serverId,
        string host,
        int targetPort,
        bool tlsEnabled,
        bool canonical = false,
        bool redirectToCanonical = false,
        RoutePolicy? policy = null,
        string pathPrefix = "")
    {
        return new ProxyRoute(RoutableEndpoint.ForApplication(applicationId, targetPort), serverId, host, pathPrefix,
            tlsEnabled, canonical, redirectToCanonical, policy);
    }
}

/// <summary>A route excluded from the generated configuration because it failed validation.</summary>
public sealed record ProxyRouteRejection(ProxyRoute Route, string Reason);

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
    DateTimeOffset? AppliedAt,
    ProxyConfigurationSource Source = ProxyConfigurationSource.Generated,
    string? FileName = null,
    Guid? RestoredFromId = null);

/// <summary>Full text of a configuration version (sensitive; requires write access to the server).</summary>
public sealed record ProxyConfigurationContent(ProxyConfigurationSummary Version, string Configuration);

public sealed record ProxyDynamicConfigurationSummary(
    Guid Id,
    Guid ServerId,
    string FileName,
    string ContentHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ProxyDynamicConfigurationContent(ProxyDynamicConfigurationSummary Summary, string Content);

public sealed record SaveProxyDynamicConfigurationRequest(string Content);

/// <summary>Write model for a route policy. <see cref="BasicAuthPassword" /> is write-only.</summary>
public sealed record RoutePolicyRequest(
    bool? ForceHttps = null,
    RouteRedirectMode? RedirectMode = null,
    bool? PermanentRedirect = null,
    bool? Compression = null,
    bool? NoIndex = null,
    bool? StripPathPrefix = null,
    bool? BasicAuthEnabled = null,
    string? BasicAuthUsername = null,
    string? BasicAuthPassword = null)
{
    public override string ToString()
    {
        return
            $"RoutePolicyRequest {{ ForceHttps = {ForceHttps}, RedirectMode = {RedirectMode}, PermanentRedirect = {PermanentRedirect}, Compression = {Compression}, NoIndex = {NoIndex}, StripPathPrefix = {StripPathPrefix}, BasicAuthEnabled = {BasicAuthEnabled}, BasicAuthUsername = {BasicAuthUsername}, BasicAuthPassword = [redacted] }}";
    }
}

/// <summary>Read model for a route policy. The basic-auth password is never returned.</summary>
public sealed record RoutePolicySummary(
    bool ForceHttps,
    RouteRedirectMode RedirectMode,
    bool PermanentRedirect,
    bool Compression,
    bool NoIndex,
    bool StripPathPrefix,
    bool BasicAuthEnabled,
    string? BasicAuthUsername)
{
    public static RoutePolicySummary From(RoutePolicy policy)
    {
        return new RoutePolicySummary(policy.ForceHttps, policy.RedirectMode, policy.PermanentRedirect,
            policy.Compression, policy.NoIndex, policy.StripPathPrefix, policy.BasicAuth is not null,
            policy.BasicAuth?.Username);
    }
}

public sealed record DomainRouteSummary(
    string Host,
    int? TargetPort,
    bool TlsEnabled,
    bool Canonical,
    bool RedirectToCanonical,
    CertificateStatus? CertificateStatus,
    CertificateProvider? CertificateProvider,
    DateTimeOffset? CertificateExpiresAt,
    string? CertificateLastError,
    string PathPrefix = "",
    RouteHostKind HostKind = RouteHostKind.Dns,
    string? Url = null,
    RoutePolicySummary? Policy = null);

/// <summary>
///     Configures a route. <see cref="Host" /> accepts a URL (<c>https://app.example.com/api</c>, <c>ws(s)://</c> is
///     normalized), a wildcard (<c>*.example.com</c>), or an IPv4/IPv6 address. A scheme in the URL overrides
///     <see cref="TlsEnabled" />; a port is the internal target port. <see cref="ForceDomainOverride" /> saves despite a
///     conflict with another resource (audited).
/// </summary>
public sealed record ConfigureDomainRouteRequest(
    string Host,
    int? TargetPort,
    bool TlsEnabled,
    bool Canonical,
    bool RedirectToCanonical,
    RoutePolicyRequest? Policy = null,
    bool ForceDomainOverride = false);

/// <summary>A resource that already claims a route identity (host + path).</summary>
public sealed record DomainConflict(RouteOwnerKind OwnerKind, Guid OwnerId, string OwnerName, string Identity);


public sealed record CertificateSummary(
    Guid Id,
    Guid? ApplicationId,
    string Host,
    CertificateProvider Provider,
    CertificateStatus Status,
    DateTimeOffset? ExpiresAt,
    string? LastError,
    Guid ServerId = default,
    IReadOnlyList<string>? SubjectAlternativeNames = null,
    string? Resolver = null,
    bool InUse = false,
    DateTimeOffset? IssuedAt = null,
    DateTimeOffset? LastSyncedAt = null);
