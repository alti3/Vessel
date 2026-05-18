namespace Vessel.Application.Auth;

public sealed class AuthOptions
{
    public const string SectionName = "Vessel:Auth";

    public int LockoutThreshold { get; set; } = 5;

    public int LockoutMinutes { get; set; } = 15;

    public int PasswordResetTokenMinutes { get; set; } = 60;

    public int InvitationExpirationDays { get; set; } = 7;

    public string OidcAuthority { get; set; } = string.Empty;

    public string OidcClientId { get; set; } = string.Empty;

    public string OidcClientSecret { get; set; } = string.Empty;

    public string GitHubClientId { get; set; } = string.Empty;

    public string GitHubClientSecret { get; set; } = string.Empty;

    public string GitLabAuthority { get; set; } = "https://gitlab.com";

    public string GitLabClientId { get; set; } = string.Empty;

    public string GitLabClientSecret { get; set; } = string.Empty;
}
