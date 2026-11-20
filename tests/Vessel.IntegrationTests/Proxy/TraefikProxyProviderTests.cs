using System.Runtime.CompilerServices;
using Vessel.Application.Processes;
using Vessel.Application.Proxy;
using Vessel.Domain;
using Vessel.Domain.Proxy;
using Vessel.Infrastructure.Files;
using Vessel.Infrastructure.Proxy;
using YamlDotNet.RepresentationModel;
using AppId = Vessel.Domain.ApplicationId;

namespace Vessel.IntegrationTests.Proxy;

public sealed class TraefikProxyProviderTests
{
    [Fact]
    public void Generate_IsDeterministicForRouteSet()
    {
        TraefikProxyProvider provider = CreateProvider();
        var serverId = ServerId.New();
        ProxyRoute[] routes =
        [
            new(AppId.New(), serverId, "vessel-app", "app.example.com", 8080, true, true, false)
        ];

        ProxyConfigurationDocument first = provider.Generate(serverId, routes);
        ProxyConfigurationDocument second = provider.Generate(serverId, routes);

        Assert.Equal(first.Contents, second.Contents);
        Assert.Equal(first.Sha256Hash, second.Sha256Hash);
        Assert.Contains("certResolver: letsencrypt", first.Contents, StringComparison.Ordinal);
        Assert.Contains("Host(`app.example.com`)", first.Contents, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_WritesHttpRoutesCustomPortsAndCanonicalRedirects()
    {
        TraefikProxyProvider provider = CreateProvider();
        var serverId = ServerId.New();
        var applicationId = AppId.New();

        ProxyConfigurationDocument document = provider.Generate(serverId,
        [
            new ProxyRoute(applicationId, serverId, "vessel-app", "app.example.com", 8080, true, true, false),
            new ProxyRoute(applicationId, serverId, "vessel-app", "www.example.com", 9090, false, false, true)
        ]);

        Assert.Contains("Host(`app.example.com`)", document.Contents, StringComparison.Ordinal);
        Assert.Contains("Host(`www.example.com`)", document.Contents, StringComparison.Ordinal);
        Assert.Contains("- websecure", document.Contents, StringComparison.Ordinal);
        Assert.Contains("- web", document.Contents, StringComparison.Ordinal);
        Assert.Contains("http://vessel-app:9090", document.Contents, StringComparison.Ordinal);
        Assert.Contains("redirectRegex:", document.Contents, StringComparison.Ordinal);
        ProxyValidationResult validation = provider.Validate(document);
        Assert.True(validation.Succeeded, string.Join("; ", validation.Errors));
    }

    [Fact]
    public void Generate_EmitsParseableYamlWithLiteralCaptureGroupReplacement()
    {
        TraefikProxyProvider provider = CreateProvider();
        var serverId = ServerId.New();
        var applicationId = AppId.New();

        ProxyConfigurationDocument document = provider.Generate(serverId,
        [
            new ProxyRoute(applicationId, serverId, "vessel-app", "app.example.com", 8080, true, true, false),
            new ProxyRoute(applicationId, serverId, "vessel-app", "www.example.com", 8080, true, false, true)
        ]);

        var yaml = new YamlStream();
        yaml.Load(new StringReader(document.Contents));
        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        var middlewares = (YamlMappingNode)((YamlMappingNode)root["http"])["middlewares"];
        YamlMappingNode redirect = middlewares.Children
            .Where(pair => ((YamlScalarNode)pair.Key).Value!.EndsWith("-canonical", StringComparison.Ordinal))
            .Select(pair => (YamlMappingNode)((YamlMappingNode)pair.Value)["redirectRegex"])
            .Single();

        Assert.Equal(@"^https?://www\.example\.com(.*)", ((YamlScalarNode)redirect["regex"]).Value);
        Assert.Equal("https://app.example.com${1}", ((YamlScalarNode)redirect["replacement"]).Value);
        Assert.Equal("false", ((YamlScalarNode)redirect["permanent"]).Value);
        Assert.DoesNotContain("$${1}", document.Contents, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_AddsHttpRouterThatRedirectsTlsHostsToHttps()
    {
        TraefikProxyProvider provider = CreateProvider();
        var serverId = ServerId.New();
        var applicationId = AppId.New();

        ProxyConfigurationDocument document = provider.Generate(serverId,
        [
            new ProxyRoute(applicationId, serverId, "vessel-app", "app.example.com", 8080, true, true, false)
        ]);

        var key = ProxyNaming.RouterKey(applicationId, "app.example.com");
        Assert.Contains($"{key}-https:", document.Contents, StringComparison.Ordinal);
        Assert.Contains($"{key}-http:", document.Contents, StringComparison.Ordinal);
        Assert.Contains(ProxyNaming.RedirectToHttpsMiddleware + ":", document.Contents, StringComparison.Ordinal);
        Assert.Contains("redirectScheme:", document.Contents, StringComparison.Ordinal);
    }

    [Fact]
    public void RouterKey_IsStableAndDistinguishesPunctuationAndLastCharacter()
    {
        var applicationId = AppId.New();

        Assert.Equal(ProxyNaming.RouterKey(applicationId, "api.test"),
            ProxyNaming.RouterKey(applicationId, "API.test"));
        Assert.NotEqual(ProxyNaming.RouterKey(applicationId, "api.test"),
            ProxyNaming.RouterKey(applicationId, "api-test"));
        Assert.NotEqual(ProxyNaming.RouterKey(applicationId, "a.example.com"),
            ProxyNaming.RouterKey(applicationId, "a.example.co"));
        Assert.NotEqual(ProxyNaming.RouterKey(applicationId, "app.example.com"),
            ProxyNaming.RouterKey(AppId.New(), "app.example.com"));
    }

    [Fact]
    public void UpstreamAlias_IsUniquePerApplication()
    {
        Assert.NotEqual(ProxyNaming.UpstreamAlias(AppId.New()), ProxyNaming.UpstreamAlias(AppId.New()));
    }

    [Fact]
    public void Generate_RejectsRedirectToCanonicalWithoutCanonicalRoute()
    {
        TraefikProxyProvider provider = CreateProvider();
        var serverId = ServerId.New();

        Assert.Throws<InvalidOperationException>(() => provider.Generate(serverId,
        [
            new ProxyRoute(AppId.New(), serverId, "vessel-app", "www.example.com", 8080, true, false, true)
        ]));
    }

    [Fact]
    public void ValidateRoutes_QuarantinesOnlyTheInvalidRoutes()
    {
        TraefikProxyProvider provider = CreateProvider();
        var serverId = ServerId.New();
        var good = new ProxyRoute(AppId.New(), serverId, "vessel-app-a", "good.example.com", 8080, true, true, false);
        var badHost = new ProxyRoute(AppId.New(), serverId, "vessel-app-b", "-bad.example.com", 8080, true, true,
            false);
        var dangling = new ProxyRoute(AppId.New(), serverId, "vessel-app-c", "www.other.com", 8080, true, false, true);
        var duplicate = new ProxyRoute(AppId.New(), serverId, "vessel-app-d", "good.example.com", 8080, true, true,
            false);

        IReadOnlyList<ProxyRouteRejection> rejections =
            provider.ValidateRoutes([good, badHost, dangling, duplicate]);

        Assert.Equal(3, rejections.Count);
        Assert.DoesNotContain(rejections, rejection => rejection.Route == good);
    }

    [Fact]
    public void Validate_RejectsDuplicateHostsInvalidYamlAndPrivateKeys()
    {
        TraefikProxyProvider provider = CreateProvider();
        var serverId = ServerId.New();
        ProxyRoute[] routes =
        [
            new(AppId.New(), serverId, "app-one", "app.example.com", 8080, true, true, false),
            new(AppId.New(), serverId, "app-two", "app.example.com", 8081, true, false, false)
        ];
        ProxyConfigurationDocument document = provider.Generate(serverId, routes) with
        {
            Contents = "http:\n  routers: {}\n  services: {}\n  key: \"-----BEGIN PRIVATE KEY-----\"\n"
        };

        ProxyValidationResult result = provider.Validate(document);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Contains("more than one resource", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("private key", StringComparison.Ordinal));

        ProxyValidationResult invalidYaml =
            provider.Validate(document with { Contents = "http:\n  routers: \"\\.\"\n", Routes = [] });
        Assert.False(invalidYaml.Succeeded);
        Assert.Contains(invalidYaml.Errors, error => error.Contains("not valid YAML", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_AllowsBasicAuthStyleContentWithoutSecretHeuristics()
    {
        TraefikProxyProvider provider = CreateProvider();
        var document = new ProxyConfigurationDocument(ServerId.New(), ProxyProviderKind.Traefik, "v1",
            "http:\n  routers: {}\n  services: {}\n  middlewares:\n    auth:\n      basicAuth:\n        usersFile: /etc/password-hashes\n",
            "hash", []);

        ProxyValidationResult result = provider.Validate(document);

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
    }

    [Fact]
    public void Validate_RejectsMalformedHostsInvalidPortsAndWrongProvider()
    {
        TraefikProxyProvider provider = CreateProvider();
        var serverId = ServerId.New();
        var document = new ProxyConfigurationDocument(
            serverId,
            ProxyProviderKind.Caddy,
            "v1",
            "http:\n  routers: {}\n  services: {}\n",
            "hash",
            [
                new ProxyRoute(AppId.New(), serverId, "app", "-bad.example.com", 8080, true, false, false),
                new ProxyRoute(AppId.New(), serverId, "app", "bad.example.com", 70000, true, false, false)
            ]);

        ProxyValidationResult result = provider.Validate(document);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors,
            error => error.Contains("Proxy provider must be Traefik", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("is invalid", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("invalid target port", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReloadAsync_TreatsMissingProxyContainerAsSuccess()
    {
        var provider = new TraefikProxyProvider(
            new FakeProcessRunner(false, 1, "No such container: vessel-proxy"),
            new PathSafetyService());

        ProxyApplyResult result = await provider.ReloadAsync(ServerId.New(), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Contains("not running", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApplyAsync_RollsBackPreviousConfigWhenReloadFails()
    {
        var processRunner = new FakeProcessRunner(false);
        var provider = new TraefikProxyProvider(processRunner, new PathSafetyService());
        var serverId = ServerId.New();
        var previous = new ProxyConfigurationDocument(serverId, ProxyProviderKind.Traefik,
            "previous", "http:\n  routers: {}\n  services: {}\n", "hash", []);
        var configPath = Path.Combine(AppContext.BaseDirectory, "proxy", "traefik", "dynamic",
            $"server-{serverId.Value:N}.yml");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        await File.WriteAllTextAsync(configPath, previous.Contents, TestContext.Current.CancellationToken);
        var originalContent = await File.ReadAllTextAsync(configPath, TestContext.Current.CancellationToken);
        ProxyConfigurationDocument current = provider.Generate(serverId,
        [
            new ProxyRoute(AppId.New(), serverId, "vessel-app", "app.example.com", 8080, true, true, false)
        ]);

        ProxyApplyResult result = await provider.ApplyAsync(current, previous, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.True(processRunner.RunCount >= 2);
        Assert.Equal(originalContent, await File.ReadAllTextAsync(configPath, TestContext.Current.CancellationToken));
    }

    private static TraefikProxyProvider CreateProvider()
    {
        return new TraefikProxyProvider(new FakeProcessRunner(true), new PathSafetyService());
    }

    private sealed class FakeProcessRunner(
        bool success,
        int? exitCode = null,
        string? standardError = null) : IProcessRunner
    {
        public int RunCount { get; private set; }

        public Task<ProcessResult> RunTextAsync(ProcessCommand command, CancellationToken cancellationToken = default)
        {
            RunCount++;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return Task.FromResult(new ProcessResult(
                command,
                new ProcessExitInfo(success ? 0 : exitCode ?? 2, false, false, null),
                123,
                now,
                now,
                string.Empty,
                success ? string.Empty : standardError ?? "reload failed"));
        }

        public Task<ProcessBinaryResult> RunBinaryAsync(ProcessCommand command,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ProcessResult> RunTextWithInputAsync(
            ProcessCommand command,
            Stream standardInput,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ProcessExitInfo> RunAsync(ProcessCommand command, CancellationToken cancellationToken = default)
        {
            RunCount++;
            return Task.FromResult(new ProcessExitInfo(success ? 0 : exitCode ?? 2, false, false, null));
        }

        public async IAsyncEnumerable<ProcessOutputLine> StreamLinesAsync(
            ProcessCommand command,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<IInteractiveProcess> StartInteractiveAsync(
            ProcessCommand command,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
