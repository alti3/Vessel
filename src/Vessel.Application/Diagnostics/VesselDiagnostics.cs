using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Vessel.Application.Diagnostics;

public static class VesselDiagnostics
{
    public const string InstrumentationName = "Vessel";

    /// <summary>
    ///     Set to <c>true</c> on an activity whose child HTTP client spans carry secrets in the URL path (for example
    ///     chat webhook URLs or bot tokens). The telemetry pipeline reduces <c>url.full</c> on those spans to
    ///     scheme, host, and port.
    /// </summary>
    public const string RedactChildHttpUrlTag = "vessel.redact_child_http_url";

    public static readonly ActivitySource ActivitySource = new(InstrumentationName);
    public static readonly Meter Meter = new(InstrumentationName);

    public static readonly Counter<long> DeploymentStartRequests =
        Meter.CreateCounter<long>("vessel.deployment.start_requests");

    public static readonly Counter<long> DeploymentRuns =
        Meter.CreateCounter<long>("vessel.deployment.runs");

    public static readonly UpDownCounter<long> ActiveDeployments =
        Meter.CreateUpDownCounter<long>("vessel.deployment.active");

    public static readonly Histogram<double> DeploymentDurationSeconds =
        Meter.CreateHistogram<double>("vessel.deployment.duration.seconds");
}
