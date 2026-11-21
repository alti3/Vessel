using Vessel.Domain;
using Vessel.Domain.Auth;

namespace Vessel.Application.Auth.External;

/// <summary>Resolved protocol endpoints of a provider.</summary>
public sealed record OAuthEndpoints(
    string Issuer,
    Uri AuthorizationEndpoint,
    Uri TokenEndpoint,
    Uri? UserInfoEndpoint,
    bool IsOpenIdConnect);

/// <summary>The provider-attested identity after a successful code exchange.</summary>
/// <param name="Issuer">OIDC <c>iss</c> (validated against discovery) or the preset issuer for GitHub.</param>
/// <param name="Subject">Stable provider user id (<c>sub</c>, GitHub numeric id).</param>
/// <param name="Email">Email reported by the provider, if any.</param>
/// <param name="EmailVerified">True only when the provider asserts the email is verified.</param>
/// <param name="Name">Display name reported by the provider.</param>
/// <param name="HostedDomain">Google Workspace <c>hd</c> claim.</param>
public sealed record ExternalUserInfo(
    string Issuer,
    string Subject,
    string? Email,
    bool EmailVerified,
    string? Name,
    string? HostedDomain);

/// <summary>What the client needs to exchange a code (the client secret is revealed only for the exchange).</summary>
public sealed record OAuthExchangeRequest(
    AuthProviderKind Kind,
    string ClientId,
    string ClientSecret,
    OAuthEndpoints Endpoints,
    string Code,
    Uri RedirectUri,
    string CodeVerifier,
    string Nonce,
    string? ApiBaseUrl);

/// <summary>
///     Protocol client for external providers (Infrastructure). Every request goes through the outbound URL policy
///     (SSRF guard); responses and secrets are never logged.
/// </summary>
public interface IOAuthProviderClient
{
    Task<OAuthEndpoints> ResolveEndpointsAsync(AuthProviderKind kind, string authority,
        CancellationToken cancellationToken = default);

    /// <exception cref="ExternalLoginException">The exchange failed or the identity could not be validated.</exception>
    Task<ExternalUserInfo> ExchangeCodeAsync(OAuthExchangeRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Provider presets (Coolify parity: GitHub, GitLab, Google, generic OIDC).</summary>
public static class AuthProviderPresets
{
    public const string GitHubDefaultAuthority = "https://github.com";
    public const string GitLabDefaultAuthority = "https://gitlab.com";
    public const string GoogleAuthority = "https://accounts.google.com";

    public static string DefaultScopes(AuthProviderKind kind)
    {
        return kind == AuthProviderKind.GitHub ? "read:user user:email" : "openid profile email";
    }

    /// <summary>The issuer/base URL the provider uses (configured value or the preset default).</summary>
    public static string Authority(AuthProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return provider.Kind switch
        {
            AuthProviderKind.Google => GoogleAuthority,
            AuthProviderKind.GitHub => provider.Authority ?? GitHubDefaultAuthority,
            AuthProviderKind.GitLab => provider.Authority ?? GitLabDefaultAuthority,
            _ => provider.Authority ?? throw new InvalidOperationException("The OIDC provider has no issuer.")
        };
    }

    public static string Scopes(AuthProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var scopes = provider.Scopes ?? DefaultScopes(provider.Kind);
        if (provider.Kind != AuthProviderKind.GitHub && !scopes.Split(' ').Contains("openid", StringComparer.Ordinal))
            scopes = "openid " + scopes;
        return scopes;
    }
}

/// <summary>Reason codes shown on the login page (Coolify <c>auth.failed.*</c> parity).</summary>
public static class ExternalLoginDenials
{
    public const string ProviderUnavailable = "oauth_provider_unavailable";
    public const string InstanceUrlMissing = "oauth_instance_url_missing";
    public const string StateMismatch = "oauth_state_mismatch";
    public const string ProviderError = "oauth_provider_error";
    public const string EmailMissing = "oauth_email_missing";
    public const string EmailUnverified = "oauth_email_unverified";
    public const string AlreadyLinked = "oauth_already_linked";
    public const string IssuerMismatch = "oauth_issuer_mismatch";
    public const string DomainNotAllowed = "oauth_domain_not_allowed";
    public const string RegistrationDisabled = "registration_disabled";
    public const string LockedOut = "locked_out";
}

public sealed class ExternalLoginException(string reason, string message) : InvalidOperationException(message)
{
    public string Reason { get; } = reason;
}

/// <summary>State the Web layer keeps (data-protected, short-lived) between the redirect and the callback.</summary>
public sealed record ExternalLoginChallenge(
    AuthProviderId ProviderId,
    Uri AuthorizationUrl,
    string State,
    string Nonce,
    string CodeVerifier,
    string ReturnUrl);

public enum ExternalLoginStatus
{
    SignedIn,
    TwoFactorRequired,
    Denied
}

public sealed record ExternalLoginResult(
    ExternalLoginStatus Status,
    AuthenticatedUser? User,
    UserId? PendingUserId,
    string? DenialReason,
    string AuthenticationMethod)
{
    public static ExternalLoginResult Denied(string reason, string method)
    {
        return new ExternalLoginResult(ExternalLoginStatus.Denied, null, null, reason, method);
    }
}

/// <summary>A sign-in button on the login page.</summary>
public sealed record AuthProviderButton(AuthProviderId Id, string Name, AuthProviderKind Kind);
