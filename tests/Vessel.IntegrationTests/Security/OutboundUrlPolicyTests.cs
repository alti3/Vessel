using System.Net;
using Microsoft.Extensions.Options;
using Vessel.Application.OutboundRequests;
using Vessel.Infrastructure.OutboundRequests;

namespace Vessel.IntegrationTests.Security;

public sealed class OutboundUrlPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("169.254.169.254")]
    [InlineData("fd00:ec2::254")]
    [InlineData("fe80::1")]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.3.4")]
    [InlineData("192.168.1.10")]
    [InlineData("100.64.0.1")]
    [InlineData("fc00::1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("64:ff9b::a9fe:a9fe")]
    [InlineData("2002:7f00:1::")]
    [InlineData("2001:db8::1")]
    public async Task RejectsNonPublicAddressesByDefault(string address)
    {
        OutboundUrlPolicy policy = CreatePolicy(new OutboundRequestOptions(), address);

        OutboundTargetCheck check = await policy.CheckUrlAsync(new Uri("https://hooks.example.test/services/secret"),
            TestContext.Current.CancellationToken);

        Assert.False(check.Allowed);
        Assert.DoesNotContain("secret", check.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("93.184.216.34")]
    [InlineData("2606:4700::6810:84e5")]
    public async Task AllowsPublicAddresses(string address)
    {
        OutboundUrlPolicy policy = CreatePolicy(new OutboundRequestOptions(), address);

        OutboundTargetCheck check = await policy.CheckUrlAsync(new Uri("https://hooks.example.test/x"),
            TestContext.Current.CancellationToken);

        Assert.True(check.Allowed);
    }

    [Fact]
    public async Task RejectsWhenAnyResolvedAddressIsUnsafe()
    {
        OutboundUrlPolicy policy = CreatePolicy(new OutboundRequestOptions(), "93.184.216.34", "127.0.0.1");

        OutboundTargetCheck check = await policy.CheckUrlAsync(new Uri("https://rebind.example.test/"),
            TestContext.Current.CancellationToken);

        Assert.False(check.Allowed);
    }

    [Theory]
    [InlineData("http://hooks.example.test/x")]
    [InlineData("ftp://hooks.example.test/x")]
    [InlineData("https://user:pass@hooks.example.test/x")]
    [InlineData("https://hooks.example.test./x")]
    public async Task RejectsDisallowedUrlShapes(string url)
    {
        OutboundUrlPolicy policy = CreatePolicy(new OutboundRequestOptions(), "93.184.216.34");

        OutboundTargetCheck check = await policy.CheckUrlAsync(new Uri(url), TestContext.Current.CancellationToken);

        Assert.False(check.Allowed);
        Assert.DoesNotContain("pass", check.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsMetadataHostNamesWithoutResolving()
    {
        OutboundUrlPolicy policy = CreatePolicy(new OutboundRequestOptions(), "93.184.216.34");

        OutboundTargetCheck check = await policy.CheckHostAsync("metadata.google.internal",
            TestContext.Current.CancellationToken);

        Assert.False(check.Allowed);
    }

    [Fact]
    public async Task AllowListPermitsPrivateTargetsButNeverMetadata()
    {
        var options = new OutboundRequestOptions
        {
            AllowedInternalHosts = ["alerts.corp.example", "*.svc.internal.example", "10.20.0.0/16", "169.254.0.0/16"]
        };
        CancellationToken token = TestContext.Current.CancellationToken;

        Assert.True((await CreatePolicy(options, "10.1.2.3")
            .CheckUrlAsync(new Uri("http://alerts.corp.example/hook"), token)).Allowed);
        Assert.True((await CreatePolicy(options, "192.168.4.4")
            .CheckUrlAsync(new Uri("https://hook.svc.internal.example/"), token)).Allowed);
        Assert.True((await CreatePolicy(options, "10.20.9.9")
            .CheckUrlAsync(new Uri("https://other.example.test/"), token)).Allowed);
        Assert.False((await CreatePolicy(options, "10.21.9.9")
            .CheckUrlAsync(new Uri("https://other.example.test/"), token)).Allowed);
        Assert.False((await CreatePolicy(options, "169.254.169.254")
            .CheckUrlAsync(new Uri("https://other.example.test/"), token)).Allowed);
        // Allow-listing a name does not unlock loopback.
        Assert.False((await CreatePolicy(options, "127.0.0.1")
            .CheckUrlAsync(new Uri("https://alerts.corp.example/hook"), token)).Allowed);
    }

    [Fact]
    public async Task LoopbackRequiresExplicitOptIn()
    {
        var options = new OutboundRequestOptions { AllowLoopback = true, AllowHttp = true };

        OutboundTargetCheck check = await CreatePolicy(options, "127.0.0.1")
            .CheckUrlAsync(new Uri("http://localhost:8080/hook"), TestContext.Current.CancellationToken);

        Assert.True(check.Allowed);
    }

    [Fact]
    public async Task ConnectTimeGuardRefusesRebindingToPrivateAddress()
    {
        // The save-time check passed for a public answer; at send time DNS now answers with a private address.
        var resolver = new FakeResolver("10.0.0.7");
        var policy = new OutboundUrlPolicy(Options.Create(new OutboundRequestOptions()), resolver);
        using var client = new HttpClient(new OutboundUrlGuardHandler(policy)
        {
            InnerHandler = GuardedOutboundHttpHandler.Create(policy, resolver)
        });

        HttpRequestException exception = await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
            client.PostAsync(new Uri("https://hooks.example.test/services/T0/B0/top-secret"), null,
                TestContext.Current.CancellationToken));

        Assert.Contains(exception.Chain(), candidate => candidate is OutboundRequestBlockedException);
        Assert.DoesNotContain("top-secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuardHandlerRejectsPlainHttpBeforeConnecting()
    {
        var resolver = new FakeResolver("93.184.216.34");
        var policy = new OutboundUrlPolicy(Options.Create(new OutboundRequestOptions()), resolver);
        using var client = new HttpClient(new OutboundUrlGuardHandler(policy)
        {
            InnerHandler = GuardedOutboundHttpHandler.Create(policy, resolver)
        });

        await Assert.ThrowsAsync<OutboundRequestBlockedException>(() =>
            client.GetAsync(new Uri("http://hooks.example.test/x"), TestContext.Current.CancellationToken));
        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public void RedactionKeepsOnlySchemeHostAndPort()
    {
        Assert.Equal("https://hooks.slack.com",
            OutboundRequestRedaction.ForDisplay(new Uri("https://user:pw@hooks.slack.com/services/T/B/secret?x=1")));
        Assert.Equal("https://api.telegram.org:8443",
            OutboundRequestRedaction.ForDisplay(new Uri("https://api.telegram.org:8443/bot123:token/sendMessage")));
    }

    private static OutboundUrlPolicy CreatePolicy(OutboundRequestOptions options, params string[] addresses)
    {
        return new OutboundUrlPolicy(Options.Create(options), new FakeResolver(addresses));
    }

    private sealed class FakeResolver(params string[] addresses) : IHostAddressResolver
    {
        public int Calls { get; private set; }

        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(addresses.Select(IPAddress.Parse).ToArray());
        }
    }
}

internal static class ExceptionChain
{
    public static IEnumerable<Exception> Chain(this Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            yield return current;
    }
}
