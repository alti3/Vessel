using Microsoft.Extensions.Options;

namespace Vessel.Application.Common;

/// <summary>
///     The configured public URL of this Vessel instance (<c>Vessel:Host:PublicUrl</c>). Links in emails and
///     notifications are built from it, never from the request <c>Host</c> header, so a forged Host cannot turn a
///     password-reset or invitation link into a phishing link (upstream v4.4.0/v4.7.0, #11623).
/// </summary>
public sealed class InstanceUrlOptions
{
    public const string SectionName = "Vessel:Host";

    /// <summary>Absolute <c>https</c> (or <c>http</c>) base URL, for example <c>https://vessel.example.com</c>.</summary>
    public string? PublicUrl { get; set; }

    /// <summary>Returns the validated base URL, or <c>null</c> when it is not configured or not acceptable.</summary>
    public static Uri? ParseBaseUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            return null;
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return null;

        var path = uri.AbsolutePath.TrimEnd('/');
        return new Uri($"{uri.GetLeftPart(UriPartial.Authority)}{path}/");
    }
}

/// <summary>Builds absolute links from <see cref="InstanceUrlOptions.PublicUrl" />.</summary>
public sealed class InstanceLinkBuilder(IOptions<InstanceUrlOptions> options)
{
    private readonly Uri? _baseUrl = InstanceUrlOptions.ParseBaseUrl(options.Value.PublicUrl);

    public bool IsConfigured => _baseUrl is not null;

    /// <summary>
    ///     Builds <c>{PublicUrl}/{relativePath}?{query}</c>. Returns <c>null</c> when no public URL is configured or the
    ///     path is not a plain relative path (absolute and protocol-relative values are refused).
    /// </summary>
    public Uri? Build(string relativePath, IReadOnlyDictionary<string, string>? query = null)
    {
        if (_baseUrl is null || string.IsNullOrWhiteSpace(relativePath))
            return null;

        var path = relativePath.Trim();
        if (path.StartsWith("//", StringComparison.Ordinal) || path.Contains("://", StringComparison.Ordinal) ||
            path.Contains('\\', StringComparison.Ordinal))
            return null;

        var queryString = query is null || query.Count == 0
            ? string.Empty
            : "?" + string.Join('&', query.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return new Uri(_baseUrl, path.TrimStart('/') + queryString);
    }

    /// <summary>Makes a relative resource URL (for example <c>/deployments/…</c>) absolute; other values pass through.</summary>
    public string? Absolutize(string? resourceUrl)
    {
        if (string.IsNullOrWhiteSpace(resourceUrl) || !resourceUrl.StartsWith('/') || resourceUrl.StartsWith("//",
                StringComparison.Ordinal))
            return resourceUrl;

        return _baseUrl is null ? resourceUrl : new Uri(_baseUrl, resourceUrl.TrimStart('/')).AbsoluteUri;
    }
}
