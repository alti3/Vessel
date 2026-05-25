using System.Runtime.CompilerServices;
using Vessel.Application.Processes;
using Vessel.Application.Proxy;
using Vessel.Domain;
using Vessel.Domain.Proxy;
using Vessel.Infrastructure.Files;
using Vessel.Infrastructure.Proxy;
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
        Assert.Contains("url: \"http://vessel-app:9090\"", document.Contents, StringComparison.Ordinal);
        Assert.Contains("redirectRegex:", document.Contents, StringComparison.Ordinal);
        Assert.Contains("replacement: \"https://app.example.com$${1}\"", document.Contents, StringComparison.Ordinal);
        Assert.True(provider.Validate(document).Succeeded);
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
    public void Validate_RejectsDuplicateHostsAndSecretLikeContent()
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
            Contents = "http:\n  routers: {}\n  services: {}\n  password: super-secret\n"
        };

        ProxyValidationResult result = provider.Validate(document);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Contains("configured more than once", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("secret-like", StringComparison.Ordinal));
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

        ProxyApplyResult result = await provider.ReloadAsync(ServerId.New());

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
        await File.WriteAllTextAsync(configPath, previous.Contents);
        var originalContent = await File.ReadAllTextAsync(configPath);
        ProxyConfigurationDocument current = provider.Generate(serverId,
        [
            new ProxyRoute(AppId.New(), serverId, "vessel-app", "app.example.com", 8080, true, true, false)
        ]);

        ProxyApplyResult result = await provider.ApplyAsync(current, previous);

        Assert.False(result.Succeeded);
        Assert.True(processRunner.RunCount >= 2);
        Assert.Equal(originalContent, await File.ReadAllTextAsync(configPath));
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
    }
}
