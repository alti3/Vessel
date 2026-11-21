using Vessel.Domain.Common;

namespace Vessel.Domain.Sources;

/// <summary>
///     Git provider base URLs: normalization of the HTML (web) URL of a source and derivation of the REST API URL
///     (upstream <c>githubApiUrlFromHtmlUrl</c>: api.github.com for github.com, <c>api.&lt;host&gt;</c> for GHE.com
///     data residency, <c>&lt;origin&gt;/api/v3</c> for GitHub Enterprise Server; <c>/api/v4</c> for GitLab and
///     <c>/api/v1</c> for Gitea/Forgejo).
/// </summary>
public static class GitProviderEndpoints
{
    public const int MaxUrlLength = 512;

    /// <summary>Known provider presets (upstream v4.7.0 Gitea.com, Codeberg and Forgejo presets).</summary>
    public static readonly IReadOnlyDictionary<string, string> Presets =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["github"] = "https://github.com",
            ["gitlab"] = "https://gitlab.com",
            ["gitea"] = "https://gitea.com",
            ["codeberg"] = "https://codeberg.org",
            ["bitbucket"] = "https://bitbucket.org"
        };

    /// <summary>
    ///     Validates and normalizes a provider web URL: absolute <c>https</c>, no user-info, query or fragment, no
    ///     trailing slash. A path prefix is kept (GitLab served under <c>/gitlab</c>).
    /// </summary>
    public static string NormalizeHtmlUrl(string? value)
    {
        var trimmed = DomainValidation.Required(value ?? string.Empty, "Provider URL", MaxUrlLength).TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new DomainException("Provider URL must be an absolute https:// URL.");
        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new DomainException("Provider URL must not contain credentials, a query or a fragment.");
        if (uri.Host.Length == 0 || uri.Host.StartsWith('-'))
            throw new DomainException("Provider URL has an invalid host.");
        var path = uri.AbsolutePath.TrimEnd('/');
        return $"{uri.Scheme}://{uri.Authority.ToLowerInvariant()}{path}";
    }

    public static string ApiUrlFor(GitSourceKind kind, string htmlUrl)
    {
        var html = NormalizeHtmlUrl(htmlUrl);
        return kind switch
        {
            GitSourceKind.GitHubApp => GitHubApiUrl(html),
            GitSourceKind.GitLabApp => html + "/api/v4",
            GitSourceKind.GiteaApp => html + "/api/v1",
            _ => html
        };
    }

    public static string GitHubApiUrl(string htmlUrl)
    {
        var uri = new Uri(NormalizeHtmlUrl(htmlUrl));
        var host = uri.Host.ToLowerInvariant();
        if (host is "github.com" or "www.github.com") return "https://api.github.com";
        if (host.EndsWith(".ghe.com", StringComparison.Ordinal)) return $"https://api.{host}";
        return $"{uri.Scheme}://{uri.Authority.ToLowerInvariant()}/api/v3";
    }

    /// <summary>True for github.com and GHE.com hosts (cloud), false for GitHub Enterprise Server.</summary>
    public static bool IsGitHubCloud(string htmlUrl)
    {
        var host = new Uri(NormalizeHtmlUrl(htmlUrl)).Host.ToLowerInvariant();
        return host is "github.com" or "www.github.com" || host.EndsWith(".ghe.com", StringComparison.Ordinal);
    }

    /// <summary>Validates an explicitly supplied API URL (absolute https, no credentials, query or fragment).</summary>
    public static string NormalizeApiUrl(string? value)
    {
        return NormalizeHtmlUrl(value);
    }
}
