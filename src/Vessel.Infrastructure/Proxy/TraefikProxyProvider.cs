using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Vessel.Application.Proxy;
using Vessel.Domain;
using Vessel.Domain.Proxy;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace Vessel.Infrastructure.Proxy;

public sealed class TraefikProxyProvider : IProxyProvider
{
    // DNS host names (underscores allowed per upstream v4.2.0 #10663). IP/wildcard forms are handled by NET-03.
    private static readonly Regex HostPattern = new(
        "^(?=.{1,253}$)([a-z0-9_]([a-z0-9_-]{0,61}[a-z0-9_])?)(\\.[a-z0-9_]([a-z0-9_-]{0,61}[a-z0-9_])?)+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex UpstreamPattern = new("^[a-z0-9][a-z0-9_.-]{0,62}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithQuotingNecessaryStrings()
        .DisableAliases()
        .Build();

    public IReadOnlyList<ProxyRouteRejection> ValidateRoutes(IReadOnlyList<ProxyRoute> routes)
    {
        var rejections = new List<ProxyRouteRejection>();
        var seenHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ProxyRoute route in routes.OrderBy(route => route.Host, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(route => route.ApplicationId.Value))
        {
            var reason = RouteError(route, routes);
            if (reason is null && !seenHosts.Add(route.Host))
                reason = $"Domain host '{route.Host}' is configured by more than one resource on this server.";
            if (reason is not null)
                rejections.Add(new ProxyRouteRejection(route, reason));
        }

        return rejections;
    }

    public ProxyConfigurationDocument Generate(ServerId serverId, IReadOnlyList<ProxyRoute> routes)
    {
        ProxyRoute[] ordered = routes
            .OrderBy(route => route.Host, StringComparer.OrdinalIgnoreCase)
            .ThenBy(route => route.ApplicationId.Value)
            .ToArray();
        foreach (ProxyRoute route in ordered)
            if (route.RedirectToCanonical && !route.Canonical && CanonicalHostFor(route, ordered) is null)
                throw new InvalidOperationException(
                    "Redirect-to-canonical routes require a canonical domain for the same application.");

        var routers = new SortedDictionary<string, object>(StringComparer.Ordinal);
        var services = new SortedDictionary<string, object>(StringComparer.Ordinal);
        var middlewares = new SortedDictionary<string, object>(StringComparer.Ordinal);

        foreach (ProxyRoute route in ordered)
        {
            var key = ProxyNaming.RouterKey(route.ApplicationId, route.Host);
            var rule = $"Host(`{route.Host}`)";
            var routeMiddlewares = new List<string>();
            var canonicalHost = route.RedirectToCanonical && !route.Canonical
                ? CanonicalHostFor(route, ordered)
                : null;
            if (canonicalHost is not null)
            {
                var canonicalMiddleware = $"{key}-canonical";
                routeMiddlewares.Add(canonicalMiddleware);
                middlewares[canonicalMiddleware] = new Dictionary<string, object>
                {
                    ["redirectRegex"] = new Dictionary<string, object>
                    {
                        // Traefik file provider: the replacement uses Go regexp expansion, so `${1}` is literal here.
                        // `$$` escaping is only required when values pass through Docker Compose interpolation.
                        ["regex"] = $"^https?://{Regex.Escape(route.Host)}(.*)",
                        ["replacement"] = $"{(route.TlsEnabled ? "https" : "http")}://{canonicalHost}${{1}}",
                        // Temporary by default (upstream v4.5.0 #12233); browsers cache 301s indefinitely.
                        ["permanent"] = false
                    }
                };
            }

            if (route.TlsEnabled)
            {
                routers[$"{key}-https"] = Router(rule, key, "websecure", routeMiddlewares, true);
                // Every TLS host also gets a plain HTTP router that upgrades the scheme.
                routers[$"{key}-http"] = Router(rule, key, "web", [ProxyNaming.RedirectToHttpsMiddleware], false);
            }
            else
            {
                routers[$"{key}-http"] = Router(rule, key, "web", routeMiddlewares, false);
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

        if (ordered.Any(route => route.TlsEnabled))
            middlewares[ProxyNaming.RedirectToHttpsMiddleware] = new Dictionary<string, object>
            {
                ["redirectScheme"] = new Dictionary<string, object> { ["scheme"] = "https", ["permanent"] = false }
            };

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

    private static string? RouteError(ProxyRoute route, IReadOnlyList<ProxyRoute> routes)
    {
        if (!HostPattern.IsMatch(route.Host))
            return $"Domain host '{route.Host}' is invalid.";
        if (route.TargetPort is < 1 or > 65535)
            return $"Domain host '{route.Host}' has an invalid target port.";
        if (!UpstreamPattern.IsMatch(route.UpstreamHost))
            return $"Domain host '{route.Host}' has an invalid upstream.";
        if (route.RedirectToCanonical && !route.Canonical && CanonicalHostFor(route, routes) is null)
            return $"Domain host '{route.Host}' redirects to a canonical domain that is not configured.";
        return null;
    }

    private static string? CanonicalHostFor(ProxyRoute route, IReadOnlyList<ProxyRoute> routes)
    {
        return routes
            .Where(candidate => candidate.ApplicationId == route.ApplicationId && candidate.Canonical)
            .Select(candidate => candidate.Host)
            .OrderBy(host => host, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static Dictionary<string, object> Router(
        string rule,
        string service,
        string entryPoint,
        IReadOnlyList<string> middlewares,
        bool tls)
    {
        var router = new Dictionary<string, object>
        {
            ["rule"] = rule,
            ["service"] = service,
            ["entryPoints"] = new[] { entryPoint }
        };
        if (middlewares.Count > 0)
            router["middlewares"] = middlewares.ToArray();
        if (tls)
            router["tls"] = new Dictionary<string, object> { ["certResolver"] = "letsencrypt" };
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
