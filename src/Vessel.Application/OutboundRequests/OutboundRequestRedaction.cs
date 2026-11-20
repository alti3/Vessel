namespace Vessel.Application.OutboundRequests;

/// <summary>
///     Shared helpers that keep secret-bearing URLs (chat webhook paths, bot tokens, signed query strings) out of
///     logs, failure reasons, and telemetry.
/// </summary>
public static class OutboundRequestRedaction
{
    /// <summary>
    ///     Set on an <see cref="HttpRequestMessage" /> whose URL must not be exported. The telemetry enrichment reduces
    ///     <c>url.full</c> to <see cref="ForDisplay" />.
    /// </summary>
    public static readonly HttpRequestOptionsKey<bool> RedactUrlOption = new("Vessel.OutboundRequests.RedactUrl");

    /// <summary>Returns only <c>scheme://host[:port]</c>; the path, query, fragment, and user info are dropped.</summary>
    public static string ForDisplay(Uri? url)
    {
        if (url is null || !url.IsAbsoluteUri)
            return "[redacted]";

        // SchemeAndServer = scheme + host + non-default port; user info is intentionally excluded.
        return url.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
    }
}
