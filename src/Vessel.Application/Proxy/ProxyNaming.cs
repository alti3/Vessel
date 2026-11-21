using System.Security.Cryptography;
using System.Text;
using AppId = Vessel.Domain.ApplicationId;

namespace Vessel.Application.Proxy;

/// <summary>
///     Stable, collision-resistant names shared by proxy generation and runtime compose generation.
///     Proxy upstreams address containers by a per-application network alias, never by user-controlled
///     display names, so two applications on the same server (or in different teams) cannot share traffic.
/// </summary>
public static class ProxyNaming
{
    public const string RedirectToHttpsMiddleware = "vessel-redirect-to-https";
    public const string RedirectToHttpsPermanentMiddleware = "vessel-redirect-to-https-permanent";

    /// <summary>Container name of the managed proxy on every server (upstream: <c>coolify-proxy</c>).</summary>
    public const string ProxyContainerName = "vessel-proxy";

    /// <summary>Compose project name of the managed proxy.</summary>
    public const string ProxyComposeProject = "vessel-proxy";

    /// <summary>
    ///     Per-server Docker network shared by the proxy and application containers. Must match the network the
    ///     deployment runner creates for application containers.
    /// </summary>
    public static string ServerNetwork(Vessel.Domain.ServerId serverId)
    {
        return $"vessel-{serverId.Value:N}";
    }

    /// <summary>Network alias that the application's primary service registers on the server network.</summary>
    public static string UpstreamAlias(AppId applicationId)
    {
        return $"vessel-app-{applicationId.Value:N}";
    }

    /// <summary>
    ///     Network alias of a preview deployment's primary service. Distinct from <see cref="UpstreamAlias" /> so preview
    ///     traffic can never reach the production stack (or the reverse).
    /// </summary>
    public static string PreviewUpstreamAlias(AppId applicationId, int pullRequestNumber)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pullRequestNumber);
        return $"{UpstreamAlias(applicationId)}-pr-{pullRequestNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    ///     Network alias of one Compose service of a service-template resource. The Compose service name is hashed so the
    ///     alias stays a valid, bounded DNS label whatever the template calls its services.
    /// </summary>
    public static string ServiceUpstreamAlias(Vessel.Domain.ServiceResourceId serviceId, string composeService)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(composeService);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(composeService.Trim())))
            .ToLowerInvariant()[..8];
        return $"vessel-svc-{serviceId.Value:N}-{hash}";
    }

    /// <summary>
    ///     Router/service key for any routable endpoint. Application routes at the root path keep the key of
    ///     <see cref="RouterKey(AppId,string)" /> so existing router names are stable across versions; paths and
    ///     service components are part of the hashed identity.
    /// </summary>
    public static string RouterKey(RoutableEndpoint endpoint, string host, string pathPrefix)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var owner = endpoint.Component is null
            ? endpoint.OwnerId.ToString("N")
            : $"{endpoint.OwnerId:N}:{endpoint.Component}";
        return Key(owner, host.Trim().ToLowerInvariant() + (pathPrefix ?? string.Empty),
            host.Trim().ToLowerInvariant() + (pathPrefix ?? string.Empty));
    }

    /// <summary>
    ///     Router/service key: readable host slug plus a hash of (application, host) so hosts that differ only by
    ///     punctuation or beyond the truncation point never collide. Keys are stable across configuration versions.
    /// </summary>
    public static string RouterKey(AppId applicationId, string host)
    {
        var normalizedHost = host.Trim().ToLowerInvariant();
        return Key(applicationId.Value.ToString("N"), normalizedHost, normalizedHost);
    }

    private static string Key(string owner, string slugSource, string identity)
    {
        var normalizedHost = slugSource.ToLowerInvariant();
        string slug = new(normalizedHost.Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-')
            .ToArray());
        slug = slug.Trim('-');
        if (slug.Length > 32)
            slug = slug[..32].TrimEnd('-');
        if (slug.Length == 0)
            slug = "host";

        var hash = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes($"{owner}|{identity}")))
            .ToLowerInvariant()[..10];
        return $"vessel-{slug}-{hash}";
    }
}
