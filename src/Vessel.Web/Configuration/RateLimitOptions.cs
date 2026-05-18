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
}

public sealed class RateLimitPolicyOptions
{
    public int PermitLimit { get; init; }

    public int WindowSeconds { get; init; }
}
