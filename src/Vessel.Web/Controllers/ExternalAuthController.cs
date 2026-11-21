using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Vessel.Application.Auth;
using Vessel.Application.Auth.External;
using Vessel.Domain;
using Vessel.Web.Middleware;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers;

/// <summary>
///     External sign-in (IAM-07): OAuth 2.0 authorization code + PKCE for OIDC, GitHub, GitLab and Google. The state,
///     nonce and PKCE verifier live in a short-lived, data-protected cookie; the callback URL comes from the instance URL
///     (never the Host header). Accounts with two-factor authentication get a pending-challenge cookie and must enter
///     their code on <c>/login/two-factor</c> before a session exists.
/// </summary>
[ApiController]
[AllowAnonymous]
public sealed class ExternalAuthController(
    ExternalLoginService externalLogin,
    VesselAuthenticationService authentication,
    VesselSessionManager sessionManager,
    IDataProtectionProvider dataProtection) : ControllerBase
{
    internal const string ChallengeCookie = "__Host-Vessel-OAuth";
    internal const string TwoFactorCookie = "__Host-Vessel-2FA";
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TwoFactorLifetime = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private ITimeLimitedDataProtector ChallengeProtector =>
        dataProtection.CreateProtector("Vessel.Auth.OAuthChallenge.v1").ToTimeLimitedDataProtector();

    private ITimeLimitedDataProtector TwoFactorProtector =>
        dataProtection.CreateProtector("Vessel.Auth.OAuthTwoFactor.v1").ToTimeLimitedDataProtector();

    /// <summary>Enabled sign-in providers for the login page.</summary>
    [HttpGet("api/auth/providers")]
    public ActionResult<IReadOnlyList<AuthProviderButtonResponse>> Providers()
    {
        return Ok(externalLogin.ListSignInProviders()
            .Select(provider => new AuthProviderButtonResponse(provider.Id.Value, provider.Name,
                provider.Kind.ToString().ToLowerInvariant()))
            .ToArray());
    }

    [HttpGet("auth/oauth/{providerId:guid}/start")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Start(Guid providerId, [FromQuery] string? returnUrl,
        CancellationToken cancellationToken)
    {
        ExternalLoginChallenge challenge;
        try
        {
            challenge = await externalLogin.BeginAsync(new AuthProviderId(providerId), NormalizeReturnUrl(returnUrl),
                cancellationToken);
        }
        catch (ExternalLoginException exception)
        {
            return LoginError(exception.Reason);
        }

        var payload = JsonSerializer.Serialize(new StoredChallenge(challenge.ProviderId.Value, challenge.State,
            challenge.Nonce, challenge.CodeVerifier, challenge.ReturnUrl), JsonOptions);
        // Lax (not Strict): the provider's redirect back to the callback is a cross-site top-level navigation.
        Response.Cookies.Append(ChallengeCookie, ChallengeProtector.Protect(payload, ChallengeLifetime),
            CookieOptions(SameSiteMode.Lax, ChallengeLifetime));
        return Redirect(challenge.AuthorizationUrl.AbsoluteUri);
    }

    [HttpGet("auth/oauth/{providerId:guid}/callback")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Callback(Guid providerId, [FromQuery] string? code, [FromQuery] string? state,
        [FromQuery] string? error, CancellationToken cancellationToken)
    {
        StoredChallenge? stored = ReadCookie<StoredChallenge>(ChallengeCookie, ChallengeProtector);
        Response.Cookies.Delete(ChallengeCookie, CookieOptions(SameSiteMode.Lax, TimeSpan.Zero));
        if (stored is null || stored.ProviderId != providerId || string.IsNullOrEmpty(state)
            || !FixedTimeEquals(stored.State, state))
            return LoginError(ExternalLoginDenials.StateMismatch);
        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
            return LoginError(ExternalLoginDenials.ProviderError);

        var challenge = new ExternalLoginChallenge(new AuthProviderId(stored.ProviderId), new Uri("about:blank"),
            stored.State, stored.Nonce, stored.CodeVerifier, stored.ReturnUrl);
        ExternalLoginResult result = await externalLogin.CompleteAsync(challenge, code,
            CorrelationIdMiddleware.GetCorrelationId(HttpContext), cancellationToken);

        switch (result.Status)
        {
            case ExternalLoginStatus.SignedIn:
                await sessionManager.SignInAsync(HttpContext, result.User!, result.AuthenticationMethod);
                return LocalRedirect(stored.ReturnUrl);
            case ExternalLoginStatus.TwoFactorRequired:
                var pending = JsonSerializer.Serialize(new PendingTwoFactor(result.PendingUserId!.Value.Value,
                    result.AuthenticationMethod, stored.ReturnUrl), JsonOptions);
                Response.Cookies.Append(TwoFactorCookie, TwoFactorProtector.Protect(pending, TwoFactorLifetime),
                    CookieOptions(SameSiteMode.Strict, TwoFactorLifetime));
                return LocalRedirect("/login/two-factor");
            default:
                return LoginError(result.DenialReason ?? ExternalLoginDenials.ProviderError);
        }
    }

    /// <summary>Second factor after an external sign-in.</summary>
    [HttpPost("auth/oauth/two-factor")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> TwoFactor([FromForm] ExternalTwoFactorRequest request,
        CancellationToken cancellationToken)
    {
        PendingTwoFactor? pending = ReadCookie<PendingTwoFactor>(TwoFactorCookie, TwoFactorProtector);
        if (pending is null) return LoginError("oauth_two_factor_expired");

        AuthenticatedUser? user = await authentication.CompleteExternalTwoFactorAsync(new UserId(pending.UserId),
            pending.Method, request.Code, CorrelationIdMiddleware.GetCorrelationId(HttpContext), cancellationToken);
        if (user is null) return LocalRedirect("/login/two-factor?error=invalid");

        Response.Cookies.Delete(TwoFactorCookie, CookieOptions(SameSiteMode.Strict, TimeSpan.Zero));
        await sessionManager.SignInAsync(HttpContext, user, pending.Method + "+2fa");
        return LocalRedirect(pending.ReturnUrl);
    }

    private T? ReadCookie<T>(string name, ITimeLimitedDataProtector protector) where T : class
    {
        if (!Request.Cookies.TryGetValue(name, out var value) || string.IsNullOrEmpty(value)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(protector.Unprotect(value), JsonOptions);
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or FormatException)
        {
            return null;
        }
    }

    private static CookieOptions CookieOptions(SameSiteMode sameSite, TimeSpan lifetime)
    {
        return new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = sameSite,
            Path = "/",
            IsEssential = true,
            MaxAge = lifetime > TimeSpan.Zero ? lifetime : null
        };
    }

    private LocalRedirectResult LoginError(string reason)
    {
        return LocalRedirect("/login?error=" + Uri.EscapeDataString(reason));
    }

    private static string NormalizeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl) || !returnUrl.StartsWith('/') || returnUrl.StartsWith("//",
                StringComparison.Ordinal) || returnUrl.Contains('\\', StringComparison.Ordinal)
            || !Uri.IsWellFormedUriString(returnUrl, UriKind.Relative))
            return "/";
        return returnUrl;
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
    }

    private sealed record StoredChallenge(Guid ProviderId, string State, string Nonce, string CodeVerifier,
        string ReturnUrl);

    private sealed record PendingTwoFactor(Guid UserId, string Method, string ReturnUrl);
}

public sealed record AuthProviderButtonResponse(Guid Id, string Name, string Kind);

public sealed record ExternalTwoFactorRequest(string? Code);
