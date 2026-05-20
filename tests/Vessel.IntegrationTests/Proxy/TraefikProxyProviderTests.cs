using Vessel.Application.Processes;
using Vessel.Application.Proxy;
using Vessel.Domain;
using Vessel.Infrastructure.Files;
using Vessel.Infrastructure.Proxy;
using AppId = Vessel.Domain.ApplicationId;

namespace Vessel.IntegrationTests.Proxy;

public sealed class TraefikProxyProviderTests
{
    [Fact]
    public void Generate_IsDeterministicForRouteSet()
    {
        var provider = CreateProvider();
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
    public void Validate_RejectsDuplicateHostsAndSecretLikeContent()
    {
        var provider = CreateProvider();
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
    public async Task ApplyAsync_RollsBackPreviousConfigWhenReloadFails()
    {
        var processRunner = new FakeProcessRunner(success: false);
        var provider = new TraefikProxyProvider(processRunner, new PathSafetyService());
        var serverId = ServerId.New();
        var previous = new ProxyConfigurationDocument(serverId, Vessel.Domain.Proxy.ProxyProviderKind.Traefik,
            "previous", "http:\n  routers: {}\n  services: {}\n", "hash", []);
        string configPath = Path.Combine(AppContext.BaseDirectory, "proxy", "traefik", "dynamic",
            $"server-{serverId.Value:N}.yml");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        await File.WriteAllTextAsync(configPath, previous.Contents);
        string originalContent = await File.ReadAllTextAsync(configPath);
        var current = provider.Generate(serverId,
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
        return new TraefikProxyProvider(new FakeProcessRunner(success: true), new PathSafetyService());
    }

    private sealed class FakeProcessRunner(bool success) : IProcessRunner
    {
        public int RunCount { get; private set; }

        public Task<ProcessResult> RunTextAsync(ProcessCommand command, CancellationToken cancellationToken = default)
        {
            RunCount++;
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new ProcessResult(
                command,
                new ProcessExitInfo(success ? 0 : 2, false, false, null),
                123,
                now,
                now,
                string.Empty,
                success ? string.Empty : "reload failed"));
        }

        public Task<ProcessBinaryResult> RunBinaryAsync(ProcessCommand command, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ProcessExitInfo> RunAsync(ProcessCommand command, CancellationToken cancellationToken = default)
        {
            RunCount++;
            return Task.FromResult(new ProcessExitInfo(success ? 0 : 2, false, false, null));
        }

        public async IAsyncEnumerable<ProcessOutputLine> StreamLinesAsync(
            ProcessCommand command,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
