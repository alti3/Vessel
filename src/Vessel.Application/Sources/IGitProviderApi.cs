using Vessel.Domain.Sources;

namespace Vessel.Application.Sources;

/// <summary>A short-lived provider access token.</summary>
public sealed record GitProviderToken(string Token, DateTimeOffset? ExpiresAt)
{
    public override string ToString()
    {
        return $"GitProviderToken {{ Token = [redacted], ExpiresAt = {ExpiresAt:O} }}";
    }
}

/// <summary>Result of an OAuth refresh: the new access token and (for providers that rotate them) refresh token.</summary>
public sealed record GitOAuthTokens(string AccessToken, string? RefreshToken, DateTimeOffset? ExpiresAt)
{
    public override string ToString()
    {
        return $"GitOAuthTokens {{ AccessToken = [redacted], RefreshToken = [redacted], ExpiresAt = {ExpiresAt:O} }}";
    }
}

/// <summary>A repository the source can access (provider listing).</summary>
public sealed record GitProviderRepository(
    string FullName,
    string CloneUrl,
    string? HtmlUrl,
    string? DefaultBranch,
    bool IsPrivate);

public sealed record GitProviderBranch(string Name, string? CommitSha);

/// <summary>
///     Provider REST calls for Git sources (DEP-08). Implementations call user-supplied provider URLs through the
///     SSRF-guarded outbound HTTP handler and never log tokens.
/// </summary>
public interface IGitProviderApi
{
    /// <summary>Signs a GitHub App JWT (RS256, 9 minute lifetime) and exchanges it for an installation token.</summary>
    Task<GitProviderToken> CreateGitHubInstallationTokenAsync(string apiUrl, long appId, long installationId,
        string privateKeyPem, CancellationToken cancellationToken = default);

    /// <summary>Refreshes a GitLab or Gitea OAuth access token.</summary>
    Task<GitOAuthTokens> RefreshOAuthTokenAsync(GitSourceKind kind, string htmlUrl, string clientId,
        string clientSecret, string refreshToken, CancellationToken cancellationToken = default);

    /// <summary>One page (1-based, up to 100 entries) of repositories the token can access.</summary>
    Task<IReadOnlyList<GitProviderRepository>> ListRepositoriesAsync(GitSourceKind kind, string apiUrl,
        string accessToken, int page, CancellationToken cancellationToken = default);

    /// <summary>Branches of <paramref name="repositoryPath" /> (<c>owner/name</c>, or a GitLab namespace path).</summary>
    Task<IReadOnlyList<GitProviderBranch>> ListBranchesAsync(GitSourceKind kind, string apiUrl, string accessToken,
        string repositoryPath, CancellationToken cancellationToken = default);
}

/// <summary>A provider API call failed (message is safe to show: no tokens, no request URLs with secrets).</summary>
public sealed class GitProviderApiException(string message, int? statusCode = null) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
}
