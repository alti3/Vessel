namespace Vessel.Application.Auth;

public sealed class AuthOptions
{
    public const string SectionName = "Vessel:Auth";

    public int LockoutThreshold { get; set; } = 5;

    public int LockoutMinutes { get; set; } = 15;

    public int PasswordResetTokenMinutes { get; set; } = 60;

    public int InvitationExpirationDays { get; set; } = 7;

    /// <summary>Sliding idle lifetime of an interactive session (IAM-09); activity extends it.</summary>
    public int SessionIdleDays { get; set; } = 14;

    /// <summary>Revoked and expired session rows are deleted after this many days.</summary>
    public int SessionRetentionDays { get; set; } = 30;

    // The settings below predate IAM-07 and are not used: external sign-in providers are configured by instance
    // administrators in the database (client secrets protected by the key ring). Kept so existing configuration binds.
    public string OidcAuthority { get; set; } = string.Empty;

    public string OidcClientId { get; set; } = string.Empty;

    public string OidcClientSecret { get; set; } = string.Empty;

    public string GitHubClientId { get; set; } = string.Empty;

    public string GitHubClientSecret { get; set; } = string.Empty;

    public string GitLabAuthority { get; set; } = "https://gitlab.com";

    public string GitLabClientId { get; set; } = string.Empty;

    public string GitLabClientSecret { get; set; } = string.Empty;
}
