using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Vessel.Application.Proxy;
using Vessel.Domain;
using Vessel.Domain.Common;
using Vessel.Domain.Proxy;
using Vessel.Domain.ValueObjects;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace Vessel.Infrastructure.Proxy;

/// <summary>
///     Traefik file-provider generator. Each route gets an <c>https</c> router (TLS) and/or an <c>http</c> router; route
///     policies become middlewares in a fixed order: redirects (canonical, www/non-www), basic auth, noindex, strip
///     prefix, compression. Shared middleware names carry a version suffix so a changed definition never conflicts
///     with a stale one.
/// </summary>
public sealed class TraefikProxyProvider : IProxyProvider
{
    /// <summary>Upstream <c>gzip-no-media</c>; the suffix changes whenever the excluded list changes.</summary>
    public const string CompressionMiddleware = "vessel-gzip-no-media-v1";

    /// <summary>bcrypt cost for basic-auth hashes (upstream default 10). Traefik verifies bcrypt htpasswd entries.</summary>
    public const int BasicAuthBcryptCost = 10;

    /// <summary>
    ///     Upstream <c>TRAEFIK_GZIP_EXCLUDED_CONTENT_TYPES</c>: event streams must not be buffered and media/archives are
    ///     already compressed (compressing video breaks range requests and seeking).
    /// </summary>
    public static readonly IReadOnlyList<string> CompressionExcludedContentTypes =
    [
        "text/event-stream",
        "video/mp4", "video/webm", "video/ogg", "video/quicktime", "video/x-matroska", "video/x-msvideo",
        "video/mpeg", "video/mp2t", "video/3gpp", "video/x-m4v", "video/x-flv",
        "audio/mpeg", "audio/mp4", "audio/aac", "audio/ogg", "audio/opus", "audio/webm", "audio/wav",
        "audio/x-wav", "audio/flac", "audio/x-m4a",
        "image/jpeg", "image/png", "image/gif", "image/webp", "image/avif",
        "application/pdf", "application/octet-stream", "application/zip", "application/gzip",
        "application/x-gzip", "application/zstd", "application/x-7z-compressed", "application/x-rar-compressed",
        "application/x-bzip2", "application/x-xz", "application/x-tar"
    ];

    private static readonly HashSet<string> UserConfigurationSections =
        new(["http", "tcp", "udp", "tls"], StringComparer.Ordinal);

    private static readonly Regex UpstreamPattern = new("^[a-z0-9][a-z0-9_.-]{0,62}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithQuotingNecessaryStrings()
        .DisableAliases()
        .Build();

    public IReadOnlyList<ProxyRouteRejection> ValidateRoutes(IReadOnlyList<ProxyRoute> routes)
    {
        // A route identity (host + path) claimed by more than one resource is rejected for every claimant: picking a
        // winner would let a newly configured resource hijack traffic. Save-time conflict checks prevent this case
        // unless an operator explicitly overrode the conflict.
        var duplicated = routes
            .GroupBy(route => route.Identity, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rejections = new List<ProxyRouteRejection>();
        foreach (ProxyRoute route in Order(routes))
        {
            var reason = duplicated.Contains(route.Identity)
                ? $"Domain host '{route.Identity}' is configured by more than one resource on this server."
                : RouteError(route, routes);
            if (reason is not null)
                rejections.Add(new ProxyRouteRejection(route, reason));
        }

        return rejections;
    }

    public ProxyConfigurationDocument Generate(ServerId serverId, IReadOnlyList<ProxyRoute> routes)
    {
        ProxyRoute[] ordered = Order(routes).ToArray();
        foreach (ProxyRoute route in ordered)
        {
            if (route.RedirectToCanonical && !route.Canonical && CanonicalHostFor(route, ordered) is null)
                throw new InvalidOperationException(
                    "Redirect-to-canonical routes require a canonical domain for the same application.");
            if (route.EffectivePolicy.BasicAuth is not null && route.BasicAuth is null)
                throw new InvalidOperationException(
                    "Routes with basic auth must carry resolved credentials; they are never published unprotected.");
        }

        var routers = new SortedDictionary<string, object>(StringComparer.Ordinal);
        var services = new SortedDictionary<string, object>(StringComparer.Ordinal);
        var middlewares = new SortedDictionary<string, object>(StringComparer.Ordinal);

        foreach (ProxyRoute route in ordered)
        {
            RoutePolicy policy = route.EffectivePolicy;
            var key = ProxyNaming.RouterKey(route.Endpoint, route.Host, route.PathPrefix);
            var rule = Rule(route);
            var routeMiddlewares = new List<string>();

            var canonicalHost = route.RedirectToCanonical && !route.Canonical
                ? CanonicalHostFor(route, ordered)
                : null;
            if (canonicalHost is not null)
            {
                var name = $"{key}-canonical";
                routeMiddlewares.Add(name);
                middlewares[name] = new Dictionary<string, object>
                {
                    ["redirectRegex"] = new Dictionary<string, object>
                    {
                        // Traefik file provider: the replacement uses Go regexp expansion, so `${1}` is literal here.
                        // `$$` escaping is only required when values pass through Docker Compose interpolation.
                        ["regex"] = $"^https?://{Regex.Escape(route.Host)}(.*)",
                        ["replacement"] = $"{(route.TlsEnabled ? "https" : "http")}://{canonicalHost}${{1}}",
                        // Temporary by default (upstream v4.5.0 #12233); browsers cache 301s indefinitely.
                        ["permanent"] = policy.PermanentRedirect
                    }
                };
            }

            var isWww = route.Host.StartsWith("www.", StringComparison.Ordinal);
            if (route.Endpoint.OwnerKind != RouteOwnerKind.Preview && canonicalHost is null &&
                IsDnsHost(route))
            {
                if (policy.RedirectMode == RouteRedirectMode.NonWww && isWww)
                {
                    var name = $"{key}-to-non-www";
                    routeMiddlewares.Add(name);
                    middlewares[name] = RedirectRegex(@"^(https?)://www\.(.+)", "${1}://${2}",
                        policy.PermanentRedirect);
                }
                else if (policy.RedirectMode == RouteRedirectMode.Www && !isWww)
                {
                    var name = $"{key}-to-www";
                    routeMiddlewares.Add(name);
                    middlewares[name] = RedirectRegex(@"^(https?)://(?:www\.)?(.+)", "${1}://www.${2}",
                        policy.PermanentRedirect);
                }
            }

            if (route.BasicAuth is not null && policy.BasicAuth is not null)
            {
                var name = $"{key}-basic-auth";
                routeMiddlewares.Add(name);
                middlewares[name] = new Dictionary<string, object>
                {
                    ["basicAuth"] = new Dictionary<string, object>
                    {
                        ["users"] = new[] { HtpasswdLine(key, route.BasicAuth) },
                        // The upstream never sees the client's credentials.
                        ["removeHeader"] = true
                    }
                };
            }

            if (policy.NoIndex)
            {
                var name = $"{key}-noindex";
                routeMiddlewares.Add(name);
                middlewares[name] = new Dictionary<string, object>
                {
                    ["headers"] = new Dictionary<string, object>
                    {
                        ["customResponseHeaders"] = new Dictionary<string, object>
                        {
                            ["X-Robots-Tag"] = "noindex, nofollow"
                        }
                    }
                };
            }

            if (route.PathPrefix.Length > 0 && policy.StripPathPrefix)
            {
                var name = $"{key}-strip-prefix";
                routeMiddlewares.Add(name);
                middlewares[name] = new Dictionary<string, object>
                {
                    ["stripPrefix"] = new Dictionary<string, object> { ["prefixes"] = new[] { route.PathPrefix } }
                };
            }

            if (policy.Compression)
            {
                routeMiddlewares.Add(CompressionMiddleware);
                middlewares[CompressionMiddleware] = new Dictionary<string, object>
                {
                    ["compress"] = new Dictionary<string, object>
                    {
                        ["excludedContentTypes"] = CompressionExcludedContentTypes.ToArray()
                    }
                };
            }

            if (route.TlsEnabled)
            {
                routers[$"{key}-https"] = Router(rule, key, "websecure", routeMiddlewares, TlsFor(route));
                if (policy.ForceHttps)
                {
                    // The plain HTTP router only upgrades the scheme (temporary unless permanent redirects are on).
                    var redirect = policy.PermanentRedirect
                        ? ProxyNaming.RedirectToHttpsPermanentMiddleware
                        : ProxyNaming.RedirectToHttpsMiddleware;
                    middlewares[redirect] = new Dictionary<string, object>
                    {
                        ["redirectScheme"] = new Dictionary<string, object>
                        {
                            ["scheme"] = "https", ["permanent"] = policy.PermanentRedirect
                        }
                    };
                    routers[$"{key}-http"] = Router(rule, key, "web", [redirect], null);
                }
                else
                {
                    routers[$"{key}-http"] = Router(rule, key, "web", routeMiddlewares, null);
                }
            }
            else
            {
                routers[$"{key}-http"] = Router(rule, key, "web", routeMiddlewares, null);
            }

            services[key] = new Dictionary<string, object>
            {
                ["loadBalancer"] = new Dictionary<string, object>
                {
                    ["servers"] = new object[]
                    {
                        new Dictionary<string, object> { ["url"] = $"http://{route.UpstreamHost}:{route.TargetPort}" }
                    }
                }
            };
        }

        var http = new Dictionary<string, object>
        {
            ["routers"] = routers,
            ["services"] = services
        };
        if (middlewares.Count > 0)
            http["middlewares"] = middlewares;

        var body = Serializer.Serialize(new Dictionary<string, object> { ["http"] = http });
        var contents = ("# Generated by Vessel. Do not edit by hand.\n" + body).ReplaceLineEndings("\n");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contents))).ToLowerInvariant();
        var version = hash[..16];
        return new ProxyConfigurationDocument(serverId, ProxyProviderKind.Traefik, version, contents, hash, ordered);
    }

    public ProxyValidationResult Validate(ProxyConfigurationDocument document)
    {
        var errors = new List<string>();
        if (document.Provider != ProxyProviderKind.Traefik)
            errors.Add("Proxy provider must be Traefik.");
        if (string.IsNullOrWhiteSpace(document.Contents))
            errors.Add("Proxy configuration is empty.");
        else
            errors.AddRange(ValidateYamlStructure(document.Contents));

        if (document.Contents.Contains("PRIVATE KEY-----", StringComparison.OrdinalIgnoreCase))
            errors.Add("Proxy configuration contains private key material.");

        foreach (ProxyRouteRejection rejection in ValidateRoutes(document.Routes))
            errors.Add(rejection.Reason);

        return errors.Count == 0 ? ProxyValidationResult.Success : new ProxyValidationResult(false, errors);
    }

    public ProxyValidationResult ValidateUserDynamicConfiguration(string contents)
    {
        if (string.IsNullOrWhiteSpace(contents))
            return new ProxyValidationResult(false, ["Dynamic configuration is empty."]);
        if (contents.Contains("PRIVATE KEY-----", StringComparison.OrdinalIgnoreCase))
            return new ProxyValidationResult(false,
                ["Dynamic configuration contains private key material; reference certificate files instead."]);

        YamlStream stream = new();
        try
        {
            using var reader = new StringReader(contents);
            stream.Load(reader);
        }
        catch (YamlException ex)
        {
            return new ProxyValidationResult(false, [$"Dynamic configuration is not valid YAML: {ex.Message}"]);
        }

        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
            return new ProxyValidationResult(false, ["Dynamic configuration must be a single YAML mapping."]);

        var errors = new List<string>();
        foreach (var (keyNode, valueNode) in root.Children)
        {
            var section = (keyNode as YamlScalarNode)?.Value;
            if (section is null || !UserConfigurationSections.Contains(section))
                errors.Add($"Dynamic configuration section '{section}' is not supported (use http, tcp, udp or tls).");
            else if (valueNode is not YamlMappingNode)
                errors.Add($"Dynamic configuration section '{section}' must be a mapping.");
        }

        return errors.Count == 0 ? ProxyValidationResult.Success : new ProxyValidationResult(false, errors);
    }

    /// <summary>
    ///     htpasswd line with a bcrypt hash. The salt is derived from the router key and credentials so the rendered file
    ///     (and its hash) is stable across versions while differing per route; the password itself never leaves memory.
    /// </summary>
    internal static string HtpasswdLine(string routerKey, ProxyBasicAuthCredentials credentials)
    {
        var salt = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"vessel-basic-auth-v1\n{routerKey}\n{credentials.Username}\n{credentials.Password}"))[..16];
        var hash = Org.BouncyCastle.Crypto.Generators.OpenBsdBCrypt.Generate("2y",
            credentials.Password.ToCharArray(), salt, BasicAuthBcryptCost);
        return $"{credentials.Username}:{hash}";
    }

    private static IEnumerable<ProxyRoute> Order(IEnumerable<ProxyRoute> routes)
    {
        return routes
            .OrderBy(route => route.Host, StringComparer.OrdinalIgnoreCase)
            .ThenBy(route => route.PathPrefix, StringComparer.Ordinal)
            .ThenBy(route => route.Endpoint.OwnerId)
            .ThenBy(route => route.Endpoint.Component, StringComparer.Ordinal);
    }

    private static string Rule(ProxyRoute route)
    {
        RouteAddress address = RouteAddress.FromParts(route.Host, route.PathPrefix);
        var host = address.HostKind == RouteHostKind.Wildcard
            // Traefik v3 Host() has no wildcards; a wildcard covers exactly one label like a wildcard certificate.
            ? $"HostRegexp(`^[^.]+{Regex.Escape(address.Host[1..])}$`)"
            // IPv6 hosts are matched without brackets (upstream: trim($host, '[]')).
            : $"Host(`{address.Host}`)";
        return address.PathPrefix.Length == 0 ? host : $"{host} && PathPrefix(`{address.PathPrefix}`)";
    }

    private static Dictionary<string, object> TlsFor(ProxyRoute route)
    {
        var tls = new Dictionary<string, object> { ["certResolver"] = TraefikRuntimeRenderer.CertificateResolver };
        if (route.Host.StartsWith("*.", StringComparison.Ordinal))
            // HostRegexp rules give Traefik no domain to request; name the wildcard (needs a DNS-challenge resolver).
            tls["domains"] = new object[] { new Dictionary<string, object> { ["main"] = route.Host } };
        return tls;
    }

    private static bool IsDnsHost(ProxyRoute route)
    {
        try
        {
            return RouteAddress.FromParts(route.Host, route.PathPrefix).HostKind == RouteHostKind.Dns;
        }
        catch (DomainException)
        {
            return false;
        }
    }

    private static Dictionary<string, object> RedirectRegex(string regex, string replacement, bool permanent)
    {
        return new Dictionary<string, object>
        {
            ["redirectRegex"] = new Dictionary<string, object>
            {
                ["regex"] = regex,
                ["replacement"] = replacement,
                ["permanent"] = permanent
            }
        };
    }

    private static string? RouteError(ProxyRoute route, IReadOnlyList<ProxyRoute> routes)
    {
        try
        {
            RouteAddress address = RouteAddress.FromParts(route.Host, route.PathPrefix);
            if (!string.Equals(address.Host, route.Host, StringComparison.Ordinal) ||
                !string.Equals(address.PathPrefix, route.PathPrefix, StringComparison.Ordinal))
                return $"Domain host '{route.Identity}' is invalid.";
        }
        catch (DomainException)
        {
            return $"Domain host '{route.Identity}' is invalid.";
        }

        if (route.TargetPort is < 1 or > 65535)
            return $"Domain host '{route.Identity}' has an invalid target port.";
        if (!UpstreamPattern.IsMatch(route.UpstreamHost))
            return $"Domain host '{route.Identity}' has an invalid upstream.";
        if (route.RedirectToCanonical && !route.Canonical && CanonicalHostFor(route, routes) is null)
            return $"Domain host '{route.Identity}' redirects to a canonical domain that is not configured.";
        if (route.EffectivePolicy.BasicAuth is not null && route.BasicAuth is null)
            return $"Domain host '{route.Identity}' has basic auth enabled but no credentials were resolved.";
        return null;
    }

    private static string? CanonicalHostFor(ProxyRoute route, IReadOnlyList<ProxyRoute> routes)
    {
        return routes
            .Where(candidate => candidate.Endpoint.OwnerKind == route.Endpoint.OwnerKind &&
                                candidate.Endpoint.OwnerId == route.Endpoint.OwnerId && candidate.Canonical)
            .Select(candidate => candidate.Host)
            .OrderBy(host => host, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static Dictionary<string, object> Router(
        string rule,
        string service,
        string entryPoint,
        IReadOnlyList<string> middlewares,
        Dictionary<string, object>? tls)
    {
        var router = new Dictionary<string, object>
        {
            ["rule"] = rule,
            ["service"] = service,
            ["entryPoints"] = new[] { entryPoint }
        };
        if (middlewares.Count > 0)
            router["middlewares"] = middlewares.ToArray();
        if (tls is not null)
            router["tls"] = tls;
        return router;
    }

    private static IEnumerable<string> ValidateYamlStructure(string contents)
    {
        YamlStream stream = new();
        try
        {
            using var reader = new StringReader(contents);
            stream.Load(reader);
        }
        catch (YamlException ex)
        {
            return [$"Proxy configuration is not valid YAML: {ex.Message}"];
        }

        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
            return ["Proxy configuration must be a single YAML mapping."];
        if (!root.Children.TryGetValue(new YamlScalarNode("http"), out YamlNode? httpNode) ||
            httpNode is not YamlMappingNode http)
            return ["Proxy configuration must include a Traefik 'http' section."];

        var errors = new List<string>();
        foreach (var section in new[] { "routers", "services" })
            if (!http.Children.TryGetValue(new YamlScalarNode(section), out YamlNode? node) ||
                node is not YamlMappingNode)
                errors.Add($"Proxy configuration must include Traefik http {section}.");
        if (errors.Count > 0)
            return errors;

        var services = (YamlMappingNode)http.Children[new YamlScalarNode("services")];
        var middlewareNames = http.Children.TryGetValue(new YamlScalarNode("middlewares"), out YamlNode? mw) &&
                              mw is YamlMappingNode middlewareMap
            ? middlewareMap.Children.Keys.OfType<YamlScalarNode>().Select(key => key.Value).ToHashSet()
            : [];
        var routers = (YamlMappingNode)http.Children[new YamlScalarNode("routers")];
        foreach (var (keyNode, valueNode) in routers.Children)
        {
            var name = (keyNode as YamlScalarNode)?.Value ?? "?";
            if (valueNode is not YamlMappingNode router)
            {
                errors.Add($"Router '{name}' must be a mapping.");
                continue;
            }

            if (!router.Children.TryGetValue(new YamlScalarNode("service"), out YamlNode? service) ||
                service is not YamlScalarNode { Value: { } serviceName } ||
                !services.Children.ContainsKey(new YamlScalarNode(serviceName)))
                errors.Add($"Router '{name}' references an unknown service.");
            if (router.Children.TryGetValue(new YamlScalarNode("middlewares"), out YamlNode? refs) &&
                refs is YamlSequenceNode sequence)
                foreach (YamlScalarNode reference in sequence.Children.OfType<YamlScalarNode>())
                    if (reference.Value is null || !middlewareNames.Contains(reference.Value))
                        errors.Add($"Router '{name}' references unknown middleware '{reference.Value}'.");
        }

        return errors;
    }
}
