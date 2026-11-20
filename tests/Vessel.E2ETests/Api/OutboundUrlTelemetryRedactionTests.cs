using System.Diagnostics;
using Vessel.Application.Diagnostics;
using Vessel.Application.OutboundRequests;
using Vessel.Web.Diagnostics;

namespace Vessel.E2ETests.Api;

public sealed class OutboundUrlTelemetryRedactionTests
{
    private const string SecretUrl = "https://hooks.slack.com/services/T000/B000/top-secret";

    [Fact]
    public void EnrichRedactsUrlForMarkedRequests()
    {
        using var activity = new Activity("POST");
        activity.SetTag("url.full", SecretUrl);
        using var request = new HttpRequestMessage(HttpMethod.Post, SecretUrl);
        request.Options.Set(OutboundRequestRedaction.RedactUrlOption, true);

        OutboundUrlTelemetryRedaction.EnrichWithHttpRequestMessage(activity, request);

        Assert.Equal("https://hooks.slack.com", activity.GetTagItem("url.full"));
    }

    [Fact]
    public void EnrichLeavesUnmarkedRequestsAlone()
    {
        using var activity = new Activity("GET");
        activity.SetTag("url.full", "https://example.test/health");
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/health");

        OutboundUrlTelemetryRedaction.EnrichWithHttpRequestMessage(activity, request);

        Assert.Equal("https://example.test/health", activity.GetTagItem("url.full"));
    }

    [Fact]
    public void ProcessorRedactsChildSpansOfNotificationSends()
    {
        using var parent = new Activity("SendNotification");
        parent.SetTag(VesselDiagnostics.RedactChildHttpUrlTag, true);
        parent.Start();
        using var child = new Activity("POST");
        child.SetTag("url.full", SecretUrl);
        child.Start();
        child.Stop();

        new OutboundUrlTelemetryRedaction().OnEnd(child);

        Assert.Equal("https://hooks.slack.com", child.GetTagItem("url.full"));
        parent.Stop();
    }
}
