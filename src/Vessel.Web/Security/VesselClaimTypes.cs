namespace Vessel.Web.Security;

internal static class VesselClaimTypes
{
    public const string TeamId = "vessel:team_id";
    public const string TokenId = "vessel:token_id";
    public const string Permission = "vessel:permission";
    public const string Scope = "vessel:scope";

    /// <summary>Server-side session id (IAM-09); validated against <c>user_sessions</c> on every request.</summary>
    public const string SessionId = "vessel:session_id";
}
