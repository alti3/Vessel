using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vessel.Application.Sources;
using Vessel.Domain.Sources;
using Vessel.Infrastructure.OutboundRequests;

namespace Vessel.Infrastructure.Sources;

/// <summary>
///     GitHub, GitLab and Gitea REST calls for Git sources (DEP-08). The client is built on the SSRF-guarded outbound
///     handler (no redirects, no proxies, private networks only when allow-listed) with request logging removed, so
///     tokens and JWTs never reach logs. Reads and token requests are retried three times on 5xx and connection
///     failures (upstream v4.7.0); a rejected GitHub JWT reports clock skew from the response <c>Date</c> header.
/// </summary>
public sealed class GitProviderApiClient(HttpClient http, TimeProvider timeProvider) : IGitProviderApi
{
    public const string HttpClientName = "vessel-git-provider-api";
    private const int MaxAttempts = 3;
    private const int MaxResponseBytes = 4 * 1024 * 1024;

    public async Task<GitProviderToken> CreateGitHubInstallationTokenAsync(string apiUrl, long appId,
        long installationId, string privateKeyPem, CancellationToken cancellationToken = default)
    {
        var jwt = GitHubAppJwt.Create(appId, privateKeyPem, timeProvider.GetUtcNow());
        var url = $"{apiUrl.TrimEnd('/')}/app/installations/{installationId.ToString(CultureInfo.InvariantCulture)}/access_tokens";
        using HttpResponseMessage response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url);
            GitHubHeaders(request, "Bearer", jwt);
            return request;
        }, cancellationToken);
        var body = await ReadAsync(response, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized && ClockSkew(response) is { } skew &&
                skew > TimeSpan.FromSeconds(50))
                throw new GitProviderApiException(
                    $"System time differs from GitHub by {skew.TotalSeconds:F0} seconds; synchronize the clock.",
                    (int)response.StatusCode);
            var message = Message(body);
            if (response.StatusCode == HttpStatusCode.NotFound)
                message =
                    $"GitHub App installation {installationId} was not found. Install the GitHub App again or update its installation id.";
            throw new GitProviderApiException($"GitHub installation token request failed: {message}",
                (int)response.StatusCode);
        }

        using JsonDocument document = JsonDocument.Parse(body);
        var token = document.RootElement.TryGetProperty("token", out JsonElement value) ? value.GetString() : null;
        if (string.IsNullOrEmpty(token))
            throw new GitProviderApiException("GitHub returned no installation token.");
        DateTimeOffset? expiresAt = document.RootElement.TryGetProperty("expires_at", out JsonElement expires) &&
                                    expires.ValueKind == JsonValueKind.String &&
                                    DateTimeOffset.TryParse(expires.GetString(), CultureInfo.InvariantCulture,
                                        DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed)
            ? parsed
            : null;
        return new GitProviderToken(token, expiresAt);
    }

    public async Task<GitOAuthTokens> RefreshOAuthTokenAsync(GitSourceKind kind, string htmlUrl, string clientId,
        string clientSecret, string refreshToken, CancellationToken cancellationToken = default)
    {
        var html = GitProviderEndpoints.NormalizeHtmlUrl(htmlUrl);
        using HttpResponseMessage response = await SendAsync(() => kind switch
        {
            GitSourceKind.GitLabApp => new HttpRequestMessage(HttpMethod.Post, html + "/oauth/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = refreshToken,
                    ["client_id"] = clientId,
                    ["client_secret"] = clientSecret
                })
            },
            GitSourceKind.GiteaApp => new HttpRequestMessage(HttpMethod.Post, html + "/login/oauth/access_token")
            {
                Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = refreshToken,
                    ["client_id"] = clientId,
                    ["client_secret"] = clientSecret
                }), Encoding.UTF8, "application/json")
            },
            _ => throw new GitProviderApiException("Only GitLab and Gitea sources refresh OAuth tokens.")
        }, cancellationToken);
        var body = await ReadAsync(response, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new GitProviderApiException($"OAuth token refresh failed: {Message(body)}", (int)response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        var access = root.TryGetProperty("access_token", out JsonElement accessValue) ? accessValue.GetString() : null;
        if (string.IsNullOrEmpty(access)) throw new GitProviderApiException("The provider returned no access token.");
        var refresh = root.TryGetProperty("refresh_token", out JsonElement refreshValue)
            ? refreshValue.GetString()
            : null;
        DateTimeOffset? expiresAt = root.TryGetProperty("expires_in", out JsonElement expiresIn) &&
                                    expiresIn.TryGetInt64(out var seconds) && seconds > 0
            ? timeProvider.GetUtcNow().AddSeconds(seconds)
            : null;
        return new GitOAuthTokens(access, refresh, expiresAt);
    }

    public async Task<IReadOnlyList<GitProviderRepository>> ListRepositoriesAsync(GitSourceKind kind, string apiUrl,
        string accessToken, int page, CancellationToken cancellationToken = default)
    {
        var api = apiUrl.TrimEnd('/');
        var pageText = page.ToString(CultureInfo.InvariantCulture);
        var url = kind switch
        {
            GitSourceKind.GitHubApp => $"{api}/installation/repositories?per_page=100&page={pageText}",
            GitSourceKind.GitLabApp =>
                $"{api}/projects?membership=true&simple=true&order_by=last_activity_at&per_page=100&page={pageText}",
            GitSourceKind.GiteaApp => $"{api}/user/repos?limit=50&page={pageText}",
            _ => throw new GitProviderApiException("Repository listing is not supported for this source.")
        };
        using JsonDocument document = await GetJsonAsync(kind, url, accessToken, cancellationToken);
        JsonElement items = kind == GitSourceKind.GitHubApp
            ? document.RootElement.TryGetProperty("repositories", out JsonElement repositories)
                ? repositories
                : default
            : document.RootElement;
        if (items.ValueKind != JsonValueKind.Array) return [];

        var result = new List<GitProviderRepository>();
        foreach (JsonElement item in items.EnumerateArray())
        {
            var fullName = Text(item, kind == GitSourceKind.GitLabApp ? "path_with_namespace" : "full_name");
            var cloneUrl = Text(item, kind == GitSourceKind.GitLabApp ? "http_url_to_repo" : "clone_url");
            if (fullName is null || cloneUrl is null) continue;
            var isPrivate = kind == GitSourceKind.GitLabApp
                ? !string.Equals(Text(item, "visibility"), "public", StringComparison.Ordinal)
                : item.TryGetProperty("private", out JsonElement flag) && flag.ValueKind == JsonValueKind.True;
            result.Add(new GitProviderRepository(fullName, cloneUrl,
                Text(item, kind == GitSourceKind.GitLabApp ? "web_url" : "html_url"), Text(item, "default_branch"),
                isPrivate));
        }

        return result;
    }

    public async Task<IReadOnlyList<GitProviderBranch>> ListBranchesAsync(GitSourceKind kind, string apiUrl,
        string accessToken, string repositoryPath, CancellationToken cancellationToken = default)
    {
        var api = apiUrl.TrimEnd('/');
        var segments = repositoryPath.Trim('/').Split('/');
        var url = kind switch
        {
            GitSourceKind.GitHubApp =>
                $"{api}/repos/{string.Join('/', segments.Select(Uri.EscapeDataString))}/branches?per_page=100",
            GitSourceKind.GitLabApp =>
                $"{api}/projects/{Uri.EscapeDataString(repositoryPath.Trim('/'))}/repository/branches?per_page=100",
            GitSourceKind.GiteaApp =>
                $"{api}/repos/{string.Join('/', segments.Select(Uri.EscapeDataString))}/branches?limit=50",
            _ => throw new GitProviderApiException("Branch listing is not supported for this source.")
        };
        using JsonDocument document = await GetJsonAsync(kind, url, accessToken, cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
        var result = new List<GitProviderBranch>();
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            var name = Text(item, "name");
            if (name is null) continue;
            string? sha = null;
            if (item.TryGetProperty("commit", out JsonElement commit) && commit.ValueKind == JsonValueKind.Object)
                sha = Text(commit, "sha") ?? Text(commit, "id");
            result.Add(new GitProviderBranch(name, sha));
        }

        return result;
    }

    private async Task<JsonDocument> GetJsonAsync(GitSourceKind kind, string url, string token,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (kind == GitSourceKind.GitHubApp)
                GitHubHeaders(request, "Bearer", token);
            else
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.UserAgent.ParseAdd("Vessel");
            }

            return request;
        }, cancellationToken);
        var body = await ReadAsync(response, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new GitProviderApiException($"Provider API request failed: {Message(body)}",
                (int)response.StatusCode);
        return JsonDocument.Parse(body);
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> createRequest,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            using HttpRequestMessage request = createRequest();
            try
            {
                HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                if ((int)response.StatusCode < 500 || attempt >= MaxAttempts) return response;
                response.Dispose();
            }
            catch (HttpRequestException exception) when (attempt < MaxAttempts &&
                                                         exception is not OutboundRequestBlockedException &&
                                                         exception.HttpRequestError is not HttpRequestError.Unknown)
            {
                // Transient connection failure: retry.
            }
            catch (HttpRequestException exception)
            {
                throw new GitProviderApiException($"Provider API is unreachable: {exception.Message}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), timeProvider, cancellationToken);
        }
    }

    private static void GitHubHeaders(HttpRequestMessage request, string scheme, string token)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue(scheme, token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.ParseAdd("Vessel");
    }

    private TimeSpan? ClockSkew(HttpResponseMessage response)
    {
        return response.Headers.Date is { } date ? (date - timeProvider.GetUtcNow()).Duration() : null;
    }

    private static async Task<string> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
            throw new GitProviderApiException("Provider API response is too large.");
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (body.Length > MaxResponseBytes) throw new GitProviderApiException("Provider API response is too large.");
        return body.Length == 0 ? "{}" : body;
    }

    /// <summary>Provider error message, shortened; never includes request headers or URLs.</summary>
    private static string Message(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            var text = document.RootElement.ValueKind == JsonValueKind.Object
                ? Text(document.RootElement, "message") ?? Text(document.RootElement, "error_description") ??
                  Text(document.RootElement, "error")
                : null;
            if (!string.IsNullOrWhiteSpace(text)) return text.Length > 300 ? text[..300] : text;
        }
        catch (JsonException)
        {
        }

        return "unexpected response";
    }

    private static string? Text(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}

/// <summary>GitHub App JSON Web Token (RS256): issued one minute in the past, valid for nine minutes.</summary>
public static class GitHubAppJwt
{
    public static string Create(long appId, string privateKeyPem, DateTimeOffset now)
    {
        var issuedAt = now.AddMinutes(-1).ToUnixTimeSeconds();
        var expiresAt = now.AddMinutes(9).ToUnixTimeSeconds();
        var header = Base64Url("""{"alg":"RS256","typ":"JWT"}"""u8.ToArray());
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["iat"] = issuedAt,
            ["exp"] = expiresAt,
            ["iss"] = appId.ToString(CultureInfo.InvariantCulture)
        }));
        var signingInput = $"{header}.{payload}";
        using var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(privateKeyPem);
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException)
        {
            throw new GitProviderApiException("The GitHub App private key could not be read.");
        }

        var signature = rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return $"{signingInput}.{Base64Url(signature)}";
    }

    private static string Base64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
