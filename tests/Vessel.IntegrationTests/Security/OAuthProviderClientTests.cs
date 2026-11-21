using System.Text;
using System.Text.Json;
using Vessel.Application.Auth.External;
using Vessel.Domain.Auth;
using Vessel.Infrastructure.Security;

namespace Vessel.IntegrationTests.Security;

/// <summary>IAM-07: ID token claim validation (issuer, audience, expiry, nonce) of the OAuth/OIDC client.</summary>
public sealed class OAuthProviderClientTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void ValidIdToken_IsAccepted()
    {
        using JsonDocument claims = Decode(Token(Claims()));
        OAuthProviderClient.ValidateIdToken(claims.RootElement, Request(), Now);
    }

    [Theory]
    [InlineData("iss", "https://evil.example", ExternalLoginDenials.IssuerMismatch)]
    [InlineData("aud", "someone-else", ExternalLoginDenials.ProviderError)]
    [InlineData("nonce", "replayed", ExternalLoginDenials.ProviderError)]
    public void TamperedIdToken_IsRejected(string claim, string value, string reason)
    {
        Dictionary<string, object> payload = Claims();
        payload[claim] = value;
        using JsonDocument claims = Decode(Token(payload));

        ExternalLoginException exception = Assert.Throws<ExternalLoginException>(() =>
            OAuthProviderClient.ValidateIdToken(claims.RootElement, Request(), Now));
        Assert.Equal(reason, exception.Reason);
    }

    [Fact]
    public void ExpiredIdToken_IsRejected()
    {
        Dictionary<string, object> payload = Claims();
        payload["exp"] = Now.AddMinutes(-10).ToUnixTimeSeconds();
        using JsonDocument claims = Decode(Token(payload));

        Assert.Throws<ExternalLoginException>(() =>
            OAuthProviderClient.ValidateIdToken(claims.RootElement, Request(), Now));
    }

    [Fact]
    public void MalformedToken_IsRejected()
    {
        Assert.Throws<ExternalLoginException>(() => OAuthProviderClient.DecodeJwtPayload("not-a-jwt"));
    }

    private static Dictionary<string, object> Claims()
    {
        return new Dictionary<string, object>
        {
            ["iss"] = "https://idp.example.com",
            ["aud"] = "client-id",
            ["sub"] = "subject",
            ["nonce"] = "nonce",
            ["exp"] = Now.AddMinutes(5).ToUnixTimeSeconds()
        };
    }

    private static string Token(Dictionary<string, object> payload)
    {
        static string Encode(string json)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        return Encode("{\"alg\":\"RS256\"}") + "." + Encode(JsonSerializer.Serialize(payload)) + ".sig";
    }

    private static JsonDocument Decode(string token)
    {
        return OAuthProviderClient.DecodeJwtPayload(token);
    }

    private static OAuthExchangeRequest Request()
    {
        var endpoints = new OAuthEndpoints("https://idp.example.com", new Uri("https://idp.example.com/authorize"),
            new Uri("https://idp.example.com/token"), null, true);
        return new OAuthExchangeRequest(AuthProviderKind.Oidc, "client-id", "secret", endpoints, "code",
            new Uri("https://vessel.example.com/auth/oauth/x/callback"), "verifier", "nonce", null);
    }
}
