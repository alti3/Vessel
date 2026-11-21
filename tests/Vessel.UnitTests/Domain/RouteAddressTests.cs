using Vessel.Domain.Common;
using Vessel.Domain.ValueObjects;

namespace Vessel.UnitTests.Domain;

public sealed class RouteAddressTests
{
    [Theory]
    [InlineData("app.example.com", RouteHostKind.Dns, "app.example.com", "", null)]
    [InlineData("HTTPS://App.Example.COM/", RouteHostKind.Dns, "app.example.com", "", RouteScheme.Https)]
    [InlineData("http://my_service.example.com", RouteHostKind.Dns, "my_service.example.com", "", RouteScheme.Http)]
    [InlineData("wss://socket.example.com/Live", RouteHostKind.Dns, "socket.example.com", "/Live", RouteScheme.Https)]
    [InlineData("ws://socket.example.com", RouteHostKind.Dns, "socket.example.com", "", RouteScheme.Http)]
    [InlineData("*.apps.example.com", RouteHostKind.Wildcard, "*.apps.example.com", "", null)]
    [InlineData("https://shop.xn--p1ai", RouteHostKind.Dns, "shop.xn--p1ai", "", RouteScheme.Https)]
    [InlineData("http://пример.рф", RouteHostKind.Dns, "xn--e1afmkfd.xn--p1ai", "", RouteScheme.Http)]
    [InlineData("10.1.2.3", RouteHostKind.IPv4, "10.1.2.3", "", null)]
    [InlineData("http://[2001:DB8::1]/x", RouteHostKind.IPv6, "2001:db8::1", "/x", RouteScheme.Http)]
    [InlineData("2001:db8::1", RouteHostKind.IPv6, "2001:db8::1", "", null)]
    [InlineData("https://app.example.com/Api/V1/", RouteHostKind.Dns, "app.example.com", "/Api/V1", RouteScheme.Https)]
    public void Parse_NormalizesSupportedForms(string input, RouteHostKind kind, string host, string path,
        RouteScheme? scheme)
    {
        RouteAddress address = RouteAddress.Parse(input);

        Assert.Equal(kind, address.HostKind);
        Assert.Equal(host, address.Host);
        Assert.Equal(path, address.PathPrefix);
        Assert.Equal(scheme, address.Scheme);
    }

    [Fact]
    public void Parse_TreatsHttpAndHttpsAsTheSameIdentityAndKeepsPathCase()
    {
        Assert.Equal(RouteAddress.Parse("http://app.example.com/Docs").Identity,
            RouteAddress.Parse("https://APP.example.com/Docs/").Identity);
        Assert.NotEqual(RouteAddress.Parse("app.example.com/Docs").Identity,
            RouteAddress.Parse("app.example.com/docs").Identity);
    }

    [Fact]
    public void Parse_ReadsThePortAsTheInternalTargetPort()
    {
        RouteAddress address = RouteAddress.Parse("https://app.example.com:3000/api");

        Assert.Equal(3000, address.Port);
        Assert.Equal("https://app.example.com/api", address.WithoutPort().ToUrl(true));
        Assert.Equal(8443, RouteAddress.Parse("[::1]:8443").Port);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("intranet")]
    [InlineData("ftp://app.example.com")]
    [InlineData("app.example.com?x=1")]
    [InlineData("app.example.com#top")]
    [InlineData("user:pass@app.example.com")]
    [InlineData("app.example.com:0")]
    [InlineData("app.example.com:70000")]
    [InlineData("app.*.example.com")]
    [InlineData("*.com")]
    [InlineData("**.example.com")]
    [InlineData("256.1.1.1")]
    [InlineData("1.2.3")]
    [InlineData("[fe80::1%eth0]")]
    [InlineData("app.example.com/a//b")]
    [InlineData("app.example.com/a/../b")]
    [InlineData("app.example.com/pa`th")]
    [InlineData("app.example.com/pa\"th")]
    [InlineData("app example.com")]
    [InlineData("-bad.example.com")]
    [InlineData("")]
    public void Parse_RejectsInvalidForms(string input)
    {
        Assert.Throws<DomainException>(() => RouteAddress.Parse(input));
    }

    [Fact]
    public void Parse_RejectsOversizedUrls()
    {
        Assert.Throws<DomainException>(() =>
            RouteAddress.Parse("https://app.example.com/" + new string('a', RouteAddress.MaxInputLength)));
    }

    [Fact]
    public void MatchesHost_WildcardCoversExactlyOneLabel()
    {
        RouteAddress wildcard = RouteAddress.Parse("*.example.com");

        Assert.True(wildcard.MatchesHost("app.example.com"));
        Assert.False(wildcard.MatchesHost("a.b.example.com"));
        Assert.False(wildcard.MatchesHost("example.com"));
    }

    [Theory]
    [InlineData("my_app.example.com")]
    [InlineData("shop.xn--p1ai")]
    public void DomainName_AcceptsUnderscoresAndPunycodeTlds(string value)
    {
        Assert.Equal(value, new DomainName(value).Value);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("example.123")]
    [InlineData("app-.example.com")]
    public void DomainName_RejectsSingleLabelAndInvalidLabels(string value)
    {
        Assert.Throws<DomainException>(() => new DomainName(value));
    }
}
