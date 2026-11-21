using Org.BouncyCastle.Crypto.Generators;
using Vessel.Application.Proxy;
using Vessel.Domain;
using Vessel.Domain.Proxy;
using Vessel.Infrastructure.Proxy;
using YamlDotNet.RepresentationModel;
using AppId = Vessel.Domain.ApplicationId;

namespace Vessel.IntegrationTests.Proxy;

/// <summary>Golden-style checks of the generated Traefik YAML for route identity forms (NET-03/06) and policies (NET-05).</summary>
public sealed class TraefikRoutePolicyTests
{
    private static readonly ServerId Server = ServerId.New();

    [Fact]
    public void PathRoute_UsesCasePreservedPathPrefixAndStripsItByDefault()
    {
        var app = AppId.New();
        YamlMappingNode http = Generate(out ProxyConfigurationDocument document,
            Route(app, "app.example.com", path: "/Api/V1"));

        var key = ProxyNaming.RouterKey(new RoutableEndpoint(RouteOwnerKind.Application, app.Value, "vessel-app", 8080),
            "app.example.com", "/Api/V1");
        Assert.Equal("Host(`app.example.com`) && PathPrefix(`/Api/V1`)", Scalar(http, "routers", $"{key}-https", "rule"));
        Assert.Equal("/Api/V1", ((YamlScalarNode)((YamlSequenceNode)Node(http, "middlewares", $"{key}-strip-prefix",
            "stripPrefix", "prefixes")).Children[0]).Value);
        Assert.NotEqual(ProxyNaming.RouterKey(app, "app.example.com"), key);
        AssertValid(document);
    }

    [Fact]
    public void RootApplicationRoute_KeepsTheRouterKeyOfEarlierVersions()
    {
        var app = AppId.New();
        Assert.Equal(ProxyNaming.RouterKey(app, "app.example.com"),
            ProxyNaming.RouterKey(new RoutableEndpoint(RouteOwnerKind.Application, app.Value, "x", 1),
                "app.example.com", ""));
    }

    [Fact]
    public void WildcardIpv6AndIpv4Routes_RenderTraefikMatchers()
    {
        YamlMappingNode http = Generate(out ProxyConfigurationDocument document,
            Route(AppId.New(), "*.apps.example.com"),
            Route(AppId.New(), "2001:db8::1", tls: false),
            Route(AppId.New(), "10.0.0.5", tls: false));

        var rules = Routers(http).Select(router => Scalar(router, "rule")).ToArray();
        Assert.Contains(@"HostRegexp(`^[^.]+\.apps\.example\.com$`)", rules);
        Assert.Contains("Host(`2001:db8::1`)", rules);
        Assert.Contains("Host(`10.0.0.5`)", rules);
        Assert.Contains("main: '*.apps.example.com'", document.Contents, StringComparison.Ordinal);
        AssertValid(document);
    }

    [Fact]
    public void ForceHttpsOff_ServesPlainHttpWithTheSameMiddlewares()
    {
        RoutePolicy policy = RoutePolicy.Default with { ForceHttps = false, NoIndex = true };
        var app = AppId.New();
        YamlMappingNode http = Generate(out _, Route(app, "app.example.com", policy: policy));

        var key = ProxyNaming.RouterKey(app, "app.example.com");
        var httpMiddlewares = Sequence(http, "routers", $"{key}-http", "middlewares");
        Assert.Contains($"{key}-noindex", httpMiddlewares);
        Assert.DoesNotContain(ProxyNaming.RedirectToHttpsMiddleware, httpMiddlewares);
        Assert.Equal("noindex, nofollow",
            Scalar(http, "middlewares", $"{key}-noindex", "headers", "customResponseHeaders", "X-Robots-Tag"));
    }

    [Fact]
    public void Redirects_AreTemporaryByDefaultAndPermanentOnlyWhenEnabled()
    {
        var app = AppId.New();
        YamlMappingNode temporary = Generate(out _, Route(app, "app.example.com"));
        Assert.Equal("false",
            Scalar(temporary, "middlewares", ProxyNaming.RedirectToHttpsMiddleware, "redirectScheme", "permanent"));

        YamlMappingNode permanent = Generate(out _,
            Route(app, "app.example.com", policy: RoutePolicy.Default with { PermanentRedirect = true }));
        var key = ProxyNaming.RouterKey(app, "app.example.com");
        Assert.Equal([ProxyNaming.RedirectToHttpsPermanentMiddleware],
            Sequence(permanent, "routers", $"{key}-http", "middlewares"));
        Assert.Equal("true", Scalar(permanent, "middlewares", ProxyNaming.RedirectToHttpsPermanentMiddleware,
            "redirectScheme", "permanent"));
    }

    [Fact]
    public void WwwModes_RedirectInTheConfiguredDirection()
    {
        var app = AppId.New();
        YamlMappingNode http = Generate(out ProxyConfigurationDocument document,
            Route(app, "example.com", policy: RoutePolicy.Default with { RedirectMode = RouteRedirectMode.Www }),
            Route(app, "www.example.com", policy: RoutePolicy.Default with { RedirectMode = RouteRedirectMode.Www }),
            Route(AppId.New(), "www.other.com",
                policy: RoutePolicy.Default with { RedirectMode = RouteRedirectMode.NonWww }));

        var toWww = ProxyNaming.RouterKey(app, "example.com") + "-to-www";
        Assert.Equal("${1}://www.${2}", Scalar(http, "middlewares", toWww, "redirectRegex", "replacement"));
        Assert.Equal("false", Scalar(http, "middlewares", toWww, "redirectRegex", "permanent"));
        Assert.DoesNotContain(ProxyNaming.RouterKey(app, "www.example.com") + "-to-www", document.Contents,
            StringComparison.Ordinal);
        Assert.Contains(Middlewares(http), name => name.EndsWith("-to-non-www", StringComparison.Ordinal));
        AssertValid(document);
    }

    [Fact]
    public void Compression_UsesTheNoMediaMiddlewareAndCanBeDisabled()
    {
        YamlMappingNode http = Generate(out _, Route(AppId.New(), "app.example.com"));
        var excluded = Sequence(http, "middlewares", TraefikProxyProvider.CompressionMiddleware, "compress",
            "excludedContentTypes");
        Assert.Contains("text/event-stream", excluded);
        Assert.Contains("video/mp4", excluded);
        Assert.Contains("application/pdf", excluded);

        YamlMappingNode off = Generate(out ProxyConfigurationDocument document,
            Route(AppId.New(), "app.example.com", policy: RoutePolicy.Default with { Compression = false }));
        Assert.DoesNotContain(TraefikProxyProvider.CompressionMiddleware, document.Contents, StringComparison.Ordinal);
        Assert.DoesNotContain(TraefikProxyProvider.CompressionMiddleware, Middlewares(off));
    }

    [Fact]
    public void BasicAuth_RendersBcryptCost10HashThatVerifiesAndNeverThePassword()
    {
        var app = AppId.New();
        RoutePolicy policy = RoutePolicy.Default with { BasicAuth = new RouteBasicAuth("ops", SecretReferenceId.New()) };
        var credentials = new ProxyBasicAuthCredentials("ops", "correct-horse-battery");
        YamlMappingNode http = Generate(out ProxyConfigurationDocument document,
            Route(app, "admin.example.com", policy: policy, credentials: credentials));

        var key = ProxyNaming.RouterKey(app, "admin.example.com");
        var line = Assert.Single(Sequence(http, "middlewares", $"{key}-basic-auth", "basicAuth", "users"));
        Assert.StartsWith("ops:$2y$10$", line, StringComparison.Ordinal);
        Assert.True(OpenBsdBCrypt.CheckPassword(line["ops:".Length..], "correct-horse-battery".ToCharArray()));
        Assert.False(OpenBsdBCrypt.CheckPassword(line["ops:".Length..], "wrong-password".ToCharArray()));
        Assert.DoesNotContain("correct-horse-battery", document.Contents, StringComparison.Ordinal);
        Assert.Equal("true", Scalar(http, "middlewares", $"{key}-basic-auth", "basicAuth", "removeHeader"));
        // Stable hash for unchanged credentials so unchanged routes do not create new history versions.
        Assert.Equal(document.Sha256Hash, CreateProvider().Generate(Server, document.Routes).Sha256Hash);
        AssertValid(document);
    }

    [Fact]
    public void BasicAuth_WithoutResolvedCredentialsIsNeverPublishedUnprotected()
    {
        RoutePolicy policy = RoutePolicy.Default with { BasicAuth = new RouteBasicAuth("ops", SecretReferenceId.New()) };
        ProxyRoute route = Route(AppId.New(), "admin.example.com", policy: policy);

        Assert.Single(CreateProvider().ValidateRoutes([route]));
        Assert.Throws<InvalidOperationException>(() => CreateProvider().Generate(Server, [route]));
    }

    [Fact]
    public void ValidateRoutes_SameHostOnDifferentPathsIsNotAConflict()
    {
        IReadOnlyList<ProxyRouteRejection> rejections = CreateProvider().ValidateRoutes(
        [
            Route(AppId.New(), "app.example.com"),
            Route(AppId.New(), "app.example.com", path: "/api"),
            Route(AppId.New(), "app.example.com", path: "/api")
        ]);

        Assert.Equal(2, rejections.Count);
        Assert.All(rejections, rejection => Assert.Equal("/api", rejection.Route.PathPrefix));
    }

    [Fact]
    public void ServiceEndpoints_GetDistinctRoutersPerComposeService()
    {
        var service = ServiceResourceId.New();
        ProxyRoute web = ServiceRoute(service, "web", "svc.example.com", "/");
        ProxyRoute api = ServiceRoute(service, "api", "svc.example.com", "/api");

        YamlMappingNode http = Generate(out ProxyConfigurationDocument document, web, api);

        Assert.Contains($"http://{ProxyNaming.ServiceUpstreamAlias(service, "api")}:9000", document.Contents,
            StringComparison.Ordinal);
        Assert.Equal(2, ((YamlMappingNode)http["services"]).Children.Count);
        AssertValid(document);
    }

    [Theory]
    [InlineData("http:\n  routers:\n    custom:\n      rule: Host(`x.example.com`)\n      service: s\n  services:\n    s:\n      loadBalancer:\n        servers:\n          - url: http://backend:80\n", true)]
    [InlineData("tls:\n  options:\n    modern:\n      minVersion: VersionTLS13\n", true)]
    [InlineData("http: [\n", false)]
    [InlineData("- a\n- b\n", false)]
    [InlineData("entryPoints:\n  web: {}\n", false)]
    [InlineData("http:\n  x: \"-----BEGIN PRIVATE KEY-----\"\n", false)]
    [InlineData("", false)]
    public void ValidateUserDynamicConfiguration_ParsesYamlAndAllowsOnlyDynamicSections(string contents, bool valid)
    {
        Assert.Equal(valid, CreateProvider().ValidateUserDynamicConfiguration(contents).Succeeded);
    }

    private static ProxyRoute ServiceRoute(ServiceResourceId service, string component, string host, string path)
    {
        var endpoint = new RoutableEndpoint(RouteOwnerKind.Service, service.Value,
            ProxyNaming.ServiceUpstreamAlias(service, component), component == "api" ? 9000 : 80, component);
        return new ProxyRoute(endpoint, Server, host, path == "/" ? "" : path, true, false, false);
    }

    private static ProxyRoute Route(
        AppId app,
        string host,
        bool tls = true,
        RoutePolicy? policy = null,
        string path = "",
        ProxyBasicAuthCredentials? credentials = null)
    {
        return new ProxyRoute(new RoutableEndpoint(RouteOwnerKind.Application, app.Value, "vessel-app", 8080), Server,
            host, path, tls, false, false, policy, credentials);
    }

    private static TraefikProxyProvider CreateProvider()
    {
        return new TraefikProxyProvider();
    }

    private static YamlMappingNode Generate(out ProxyConfigurationDocument document, params ProxyRoute[] routes)
    {
        document = CreateProvider().Generate(Server, routes);
        var yaml = new YamlStream();
        yaml.Load(new StringReader(document.Contents));
        return (YamlMappingNode)((YamlMappingNode)yaml.Documents[0].RootNode)["http"];
    }

    private static void AssertValid(ProxyConfigurationDocument document)
    {
        ProxyValidationResult result = CreateProvider().Validate(document);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
    }

    private static YamlNode Node(YamlMappingNode root, params string[] path)
    {
        YamlNode node = root;
        foreach (var segment in path) node = ((YamlMappingNode)node)[segment];
        return node;
    }

    private static string? Scalar(YamlMappingNode root, params string[] path)
    {
        return ((YamlScalarNode)Node(root, path)).Value;
    }

    private static string[] Sequence(YamlMappingNode root, params string[] path)
    {
        return ((YamlSequenceNode)Node(root, path)).Children.Cast<YamlScalarNode>().Select(item => item.Value!)
            .ToArray();
    }

    private static IEnumerable<YamlMappingNode> Routers(YamlMappingNode http)
    {
        return ((YamlMappingNode)http["routers"]).Children.Values.Cast<YamlMappingNode>();
    }

    private static string[] Middlewares(YamlMappingNode http)
    {
        return http.Children.ContainsKey(new YamlScalarNode("middlewares"))
            ? ((YamlMappingNode)http["middlewares"]).Children.Keys.Cast<YamlScalarNode>().Select(key => key.Value!)
                .ToArray()
            : [];
    }
}
