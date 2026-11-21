namespace Vessel.Web.Configuration;

public sealed class RateLimitOptions
{
    public const string SectionName = "Vessel:RateLimits";

    public RateLimitPolicyOptions Auth { get; init; } = new()
    {
        PermitLimit = 20,
        WindowSeconds = 60
    };

    public RateLimitPolicyOptions Webhooks { get; init; } = new()
    {
        PermitLimit = 120,
        WindowSeconds = 60
    };

    public RateLimitPolicyOptions Api { get; init; } = new()
    {
        PermitLimit = 600,
        WindowSeconds = 60
    };

    public RateLimitPolicyOptions Terminal { get; init; } = new()
    {
        PermitLimit = 60,
        WindowSeconds = 60
    };

    /// <summary>
    ///     SEC-13: failed <c>/api</c> requests (401/403, 404 without a token) allowed per token or client address per
    ///     window before the client gets 429. <c>PermitLimit = 0</c> disables the failed-request throttle.
    /// </summary>
    public RateLimitPolicyOptions ApiFailedRequests { get; init; } = new()
    {
        PermitLimit = 30,
        WindowSeconds = 60
    };
}

public sealed class RateLimitPolicyOptions
{
    public int PermitLimit { get; init; }

    public int WindowSeconds { get; init; }
}
