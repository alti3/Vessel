using System.Diagnostics;
using OpenTelemetry;
using Vessel.Application.Diagnostics;
using Vessel.Application.OutboundRequests;

namespace Vessel.Web.Diagnostics;

/// <summary>
///     Keeps secret-bearing URLs (Discord/Slack webhook paths, Telegram bot tokens) out of exported HTTP client spans.
///     Two independent signals mark a span for redaction: the request option set by the outbound guard handler
///     (<see cref="OutboundRequestRedaction.RedactUrlOption" />) and an ancestor activity tagged with
///     <see cref="VesselDiagnostics.RedactChildHttpUrlTag" />. Either one reduces <c>url.full</c> to
///     <c>scheme://host[:port]</c>.
/// </summary>
public sealed class OutboundUrlTelemetryRedaction : BaseProcessor<Activity>
{
    internal const string UrlFullTag = "url.full";
    private const string MarkerTag = "vessel.url_redacted";
    private const int MaxAncestorDepth = 8;

    /// <summary>Used as <c>HttpClientTraceInstrumentationOptions.EnrichWithHttpRequestMessage</c>.</summary>
    public static void EnrichWithHttpRequestMessage(Activity activity, HttpRequestMessage request)
    {
        if (request.Options.TryGetValue(OutboundRequestRedaction.RedactUrlOption, out var redact) && redact)
            Redact(activity, request.RequestUri);
    }

    public override void OnEnd(Activity activity)
    {
        if (activity.GetTagItem(MarkerTag) is true || activity.GetTagItem(UrlFullTag) is not string url)
            return;

        if (HasRedactingAncestor(activity))
            Redact(activity, Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed : null);
    }

    internal static void Redact(Activity activity, Uri? url)
    {
        activity.SetTag(UrlFullTag, OutboundRequestRedaction.ForDisplay(url));
        activity.SetTag("url.path", null);
        activity.SetTag("url.query", null);
        activity.SetTag(MarkerTag, true);
    }

    private static bool HasRedactingAncestor(Activity activity)
    {
        var current = activity.Parent;
        for (var depth = 0; current is not null && depth < MaxAncestorDepth; depth++, current = current.Parent)
            if (current.GetTagItem(VesselDiagnostics.RedactChildHttpUrlTag) is true)
                return true;

        return false;
    }
}
