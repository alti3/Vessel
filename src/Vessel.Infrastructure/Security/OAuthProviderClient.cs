using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Vessel.Application.Auth.External;
using Vessel.Domain.Auth;

namespace Vessel.Infrastructure.Security;

/// <summary>
///     OAuth 2.0 / OpenID Connect protocol client for external sign-in (IAM-07). Uses the SSRF-guarded HTTP client
///     (outbound URL policy at request and connect time). OIDC endpoints come from discovery and the discovered issuer
///     must equal the configured one. The ID token is received directly from the token endpoint over TLS and its
///     claims (<c>iss</c>, <c>aud</c>, <c>exp</c>, <c>nonce</c>) are validated; per OpenID Connect Core 3.1.3.7 (6) the
///     TLS channel stands in for the signature check in this flow. Tokens, secrets and response bodies are never
///     logged or returned.
/// </summary>
public sealed class OAuthProviderClient(IHttpClientFactory httpClientFactory, TimeProvider timeProvider)
    : IOAuthProviderClient
{
    public const string HttpClientName = "Vessel.OAuth";
    private static readonly TimeSpan DiscoveryCacheLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(60);

    private static readonly ConcurrentDictionary<string, (OAuthEndpoints Endpoints, DateTimeOffset Expires)> Discovery =
        new(StringComparer.Ordinal);

    public async Task<OAuthEndpoints> ResolveEndpointsAsync(AuthProviderKind kind, string authority,
        CancellationToken cancellationToken = default)
    {
        var issuer = authority.TrimEnd('/');
        if (kind == AuthProviderKind.GitHub)
            return new OAuthEndpoints(issuer, new Uri(issuer + "/login/oauth/authorize"),
                new Uri(issuer + "/login/oauth/access_token"), null, false);

        DateTimeOffset now = timeProvider.GetUtcNow();
        if (Discovery.TryGetValue(issuer, out var cached) && cached.Expires > now) return cached.Endpoints;

        using HttpClient client = httpClientFactory.CreateClient(HttpClientName);
        using HttpResponseMessage response = await client.GetAsync(
            new Uri(issuer + "/.well-known/openid-configuration"), cancellationToken);
        if (!response.IsSuccessStatusCode) throw ProviderError("discovery failed");
        using JsonDocument document = await ReadJsonAsync(response, cancellationToken);
        JsonElement root = document.RootElement;
        var discoveredIssuer = GetString(root, "issuer")?.TrimEnd('/');
        if (!string.Equals(discoveredIssuer, issuer, StringComparison.Ordinal))
            throw new ExternalLoginException(ExternalLoginDenials.IssuerMismatch,
                "The discovery document names a different issuer.");

        var endpoints = new OAuthEndpoints(
            GetString(root, "issuer")!,
            RequireHttpsUri(GetString(root, "authorization_endpoint")),
            RequireHttpsUri(GetString(root, "token_endpoint")),
            GetString(root, "userinfo_endpoint") is { } userInfo ? RequireHttpsUri(userInfo) : null,
            true);
        Discovery[issuer] = (endpoints, now.Add(DiscoveryCacheLifetime));
        return endpoints;
    }

    public async Task<ExternalUserInfo> ExchangeCodeAsync(OAuthExchangeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using HttpClient client = httpClientFactory.CreateClient(HttpClientName);
        using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, request.Endpoints.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = request.Code,
                ["redirect_uri"] = request.RedirectUri.AbsoluteUri,
                ["client_id"] = request.ClientId,
                ["client_secret"] = request.ClientSecret,
                ["code_verifier"] = request.CodeVerifier
            })
        };
        tokenRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using HttpResponseMessage tokenResponse = await client.SendAsync(tokenRequest, cancellationToken);
        if (!tokenResponse.IsSuccessStatusCode) throw ProviderError("token exchange failed");
        using JsonDocument tokens = await ReadJsonAsync(tokenResponse, cancellationToken);
        var accessToken = GetString(tokens.RootElement, "access_token") ?? throw ProviderError("no access token");

        return request.Endpoints.IsOpenIdConnect
            ? await ReadOpenIdIdentityAsync(client, request, tokens.RootElement, accessToken, cancellationToken)
            : await ReadGitHubIdentityAsync(client, request, accessToken, cancellationToken);
    }

    private async Task<ExternalUserInfo> ReadOpenIdIdentityAsync(HttpClient client, OAuthExchangeRequest request,
        JsonElement tokens, string accessToken, CancellationToken cancellationToken)
    {
        var idToken = GetString(tokens, "id_token") ?? throw ProviderError("no id token");
        using JsonDocument claimsDocument = DecodeJwtPayload(idToken);
        JsonElement claims = claimsDocument.RootElement;
        ValidateIdToken(claims, request, timeProvider.GetUtcNow());

        var subject = GetString(claims, "sub") ?? throw ProviderError("no subject");
        var email = GetString(claims, "email");
        var verified = GetBool(claims, "email_verified");
        var name = GetString(claims, "name") ?? GetString(claims, "preferred_username");
        var hostedDomain = GetString(claims, "hd");

        if ((email is null || !verified) && request.Endpoints.UserInfoEndpoint is { } userInfoEndpoint)
        {
            using var userInfoRequest = new HttpRequestMessage(HttpMethod.Get, userInfoEndpoint);
            userInfoRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using HttpResponseMessage response = await client.SendAsync(userInfoRequest, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                using JsonDocument userInfo = await ReadJsonAsync(response, cancellationToken);
                // The UserInfo response must describe the same subject as the ID token (OIDC Core 5.3.2).
                if (string.Equals(GetString(userInfo.RootElement, "sub"), subject, StringComparison.Ordinal))
                {
                    email ??= GetString(userInfo.RootElement, "email");
                    verified = verified || GetBool(userInfo.RootElement, "email_verified");
                    name ??= GetString(userInfo.RootElement, "name");
                }
            }
        }

        return new ExternalUserInfo(request.Endpoints.Issuer, subject, email, email is not null && verified, name,
            hostedDomain);
    }

    public static void ValidateIdToken(JsonElement claims, OAuthExchangeRequest request, DateTimeOffset now)
    {
        if (!string.Equals(GetString(claims, "iss")?.TrimEnd('/'), request.Endpoints.Issuer.TrimEnd('/'),
                StringComparison.Ordinal))
            throw new ExternalLoginException(ExternalLoginDenials.IssuerMismatch, "ID token issuer mismatch.");

        var audienceOk = claims.TryGetProperty("aud", out JsonElement audience) && audience.ValueKind switch
        {
            JsonValueKind.String => audience.GetString() == request.ClientId,
            JsonValueKind.Array => audience.EnumerateArray().Any(item => item.GetString() == request.ClientId),
            _ => false
        };
        if (!audienceOk) throw ProviderError("ID token audience mismatch");

        if (!claims.TryGetProperty("exp", out JsonElement exp) || !exp.TryGetInt64(out var expSeconds)
                                                                || DateTimeOffset.FromUnixTimeSeconds(expSeconds) < now - ClockSkew)
            throw ProviderError("ID token expired");

        if (!string.Equals(GetString(claims, "nonce"), request.Nonce, StringComparison.Ordinal))
            throw ProviderError("ID token nonce mismatch");
    }

    private static async Task<ExternalUserInfo> ReadGitHubIdentityAsync(HttpClient client,
        OAuthExchangeRequest request, string accessToken, CancellationToken cancellationToken)
    {
        var baseUrl = (request.ApiBaseUrl ?? "https://github.com").TrimEnd('/');
        var apiBase = string.Equals(baseUrl, "https://github.com", StringComparison.OrdinalIgnoreCase)
            ? "https://api.github.com"
            : baseUrl + "/api/v3";

        using JsonDocument user = await GetGitHubAsync(client, new Uri(apiBase + "/user"), accessToken,
            cancellationToken);
        if (!user.RootElement.TryGetProperty("id", out JsonElement id) || !id.TryGetInt64(out var numericId))
            throw ProviderError("no GitHub user id");

        using JsonDocument emails = await GetGitHubAsync(client, new Uri(apiBase + "/user/emails"), accessToken,
            cancellationToken);
        string? email = null;
        var verified = false;
        if (emails.RootElement.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in emails.RootElement.EnumerateArray())
            {
                if (!GetBool(item, "primary")) continue;
                email = GetString(item, "email");
                verified = GetBool(item, "verified");
            }

        return new ExternalUserInfo(baseUrl, numericId.ToString(CultureInfo.InvariantCulture), email,
            email is not null && verified, GetString(user.RootElement, "name") ?? GetString(user.RootElement, "login"),
            null);
    }

    private static async Task<JsonDocument> GetGitHubAsync(HttpClient client, Uri url, string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Vessel", "1.0"));
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw ProviderError("GitHub API request failed");
        return await ReadJsonAsync(response, cancellationToken);
    }

    public static JsonDocument DecodeJwtPayload(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3) throw ProviderError("malformed ID token");
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
        try
        {
            return JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw ProviderError("malformed ID token");
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > 1_048_576) throw ProviderError("response too large");
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        try
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            throw ProviderError("invalid JSON");
        }
    }

    private static Uri RequireHttpsUri(string? value)
    {
        if (value is null || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
                          || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw ProviderError("invalid endpoint");
        return uri;
    }

    private static string? GetString(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value)
                                                         && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool GetBool(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out JsonElement value))
            return false;
        return value.ValueKind == JsonValueKind.True
               || (value.ValueKind == JsonValueKind.String
                   && string.Equals(value.GetString(), "true", StringComparison.OrdinalIgnoreCase));
    }

    private static ExternalLoginException ProviderError(string detail)
    {
        return new ExternalLoginException(ExternalLoginDenials.ProviderError, "External sign-in failed: " + detail + ".");
    }
}
