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

    /// <summary>Network alias that the application's primary service registers on the server network.</summary>
    public static string UpstreamAlias(AppId applicationId)
    {
        return $"vessel-app-{applicationId.Value:N}";
    }

    /// <summary>
    ///     Router/service key: readable host slug plus a hash of (application, host) so hosts that differ only by
    ///     punctuation or beyond the truncation point never collide. Keys are stable across configuration versions.
    /// </summary>
    public static string RouterKey(AppId applicationId, string host)
    {
        var normalizedHost = host.Trim().ToLowerInvariant();
        string slug = new(normalizedHost.Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-')
            .ToArray());
        slug = slug.Trim('-');
        if (slug.Length > 32)
            slug = slug[..32].TrimEnd('-');
        if (slug.Length == 0)
            slug = "host";

        var hash = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes($"{applicationId.Value:N}|{normalizedHost}")))
            .ToLowerInvariant()[..10];
        return $"vessel-{slug}-{hash}";
    }
}
