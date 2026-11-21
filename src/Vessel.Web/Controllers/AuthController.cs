using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.RateLimiting;
using Vessel.Application.Auditing;
using Vessel.Application.Auth;
using Vessel.Application.Auth.Sessions;
using Vessel.Application.Instance;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Web.Configuration;
using Vessel.Web.Middleware;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly IAuditWriter _auditWriter;
    private readonly AuthEmailService _authEmails;
    private readonly VesselAuthenticationService _authenticationService;
    private readonly VesselHostOptions _hostOptions;
    private readonly VesselSessionManager _sessionManager;
    private readonly UserSessionService _sessions;

    public AuthController(
        VesselAuthenticationService authenticationService,
        IAuditWriter auditWriter,
        IConfiguration configuration,
        AuthEmailService authEmails,
        VesselSessionManager sessionManager,
        UserSessionService sessions)
    {
        _sessionManager = sessionManager;
        _sessions = sessions;
        _authEmails = authEmails;
        _authenticationService = authenticationService;
        _auditWriter = auditWriter;
        _hostOptions = configuration.GetSection(VesselHostOptions.SectionName).Get<VesselHostOptions>()
                       ?? new VesselHostOptions();
    }

    [HttpPost("register")]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<AuthResponse>> Register(
        RegisterRequest request,
        CancellationToken cancellationToken)
    {
        AuthenticatedUser user;
        try
        {
            user = await _authenticationService.RegisterAsync(
                request.Name,
                request.Email,
                request.Password,
                CorrelationIdMiddleware.GetCorrelationId(HttpContext),
                cancellationToken);
        }
        catch (RegistrationClosedException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status403Forbidden);
        }

        await SignInAsync(user, "password");
        return Ok(AuthResponse.From(user));
    }

    [HttpPost("login")]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        AuthenticatedUser? user = await _authenticationService.LoginAsync(
            request.Email,
            request.Password,
            request.TwoFactorCode,
            CorrelationIdMiddleware.GetCorrelationId(HttpContext),
            cancellationToken);

        if (user is null) return Unauthorized();
        if (user.TwoFactorRequired) return StatusCode(StatusCodes.Status202Accepted, AuthResponse.From(user));

        await SignInAsync(user, string.IsNullOrWhiteSpace(request.TwoFactorCode) ? "password" : "password+2fa");
        return Ok(AuthResponse.From(user));
    }

    [HttpPost("/auth/login")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> LoginForm(
        [FromForm] LoginFormRequest request,
        CancellationToken cancellationToken)
    {
        AuthenticatedUser? user = await _authenticationService.LoginAsync(
            request.Email,
            request.Password,
            request.TwoFactorCode,
            CorrelationIdMiddleware.GetCorrelationId(HttpContext),
            cancellationToken);

        if (user is null || user.TwoFactorRequired)
            return Redirect(
                $"/login?error=invalid&returnUrl={Uri.EscapeDataString(request.ReturnUrl ?? string.Empty)}");

        await SignInAsync(user, string.IsNullOrWhiteSpace(request.TwoFactorCode) ? "password" : "password+2fa");
        return LocalRedirect(NormalizeReturnUrl(request.ReturnUrl));
    }

    [HttpPost("/auth/register")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> RegisterForm(
        [FromForm] RegisterFormRequest request,
        CancellationToken cancellationToken)
    {
        AuthenticatedUser user;
        try
        {
            user = await _authenticationService.RegisterAsync(
                request.Name,
                request.Email,
                request.Password,
                CorrelationIdMiddleware.GetCorrelationId(HttpContext),
                cancellationToken);
        }
        catch (RegistrationClosedException)
        {
            return Redirect("/register?error=closed");
        }

        await SignInAsync(user, "password");
        return LocalRedirect("/");
    }

    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        // IAM-09: signing out revokes the server-side session, so a copied cookie stops working too.
        if (User.GetSessionId() is { } sessionId)
        {
            await _sessions.RevokeAsync(User.GetUserId(), sessionId, UserSessionService.ReasonSignedOut,
                CorrelationIdMiddleware.GetCorrelationId(HttpContext), cancellationToken);
            _sessionManager.Forget(sessionId);
        }

        await _auditWriter.RecordAsync(
            User.GetTeamId(),
            User.GetUserId(),
            AuditActions.UserLoggedOut,
            new AuditTarget("user", User.GetUserId().ToString()),
            CorrelationIdMiddleware.GetCorrelationId(HttpContext),
            new Dictionary<string, object?>(),
            cancellationToken);

        await HttpContext.SignOutAsync(VesselAuthenticationSchemes.Cookie);
        return NoContent();
    }

    [HttpPost("password-reset/request")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> RequestPasswordReset(
        PasswordResetRequest request,
        CancellationToken cancellationToken)
    {
        await _authenticationService.RequestPasswordResetAsync(
            request.Email,
            CorrelationIdMiddleware.GetCorrelationId(HttpContext),
            cancellationToken);
        return Accepted();
    }

    [HttpPost("password-reset/complete")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> ResetPassword(PasswordResetCompleteRequest request,
        CancellationToken cancellationToken)
    {
        var succeeded = await _authenticationService.ResetPasswordAsync(
            request.Email,
            request.Token,
            request.NewPassword,
            CorrelationIdMiddleware.GetCorrelationId(HttpContext),
            cancellationToken);
        return succeeded ? NoContent() : BadRequest();
    }

    /// <summary>Form variant of the reset request used by the password-reset page; never reveals account existence.</summary>
    [HttpPost("/auth/password-reset/request")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> RequestPasswordResetForm(
        [FromForm] PasswordResetRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            await _authenticationService.RequestPasswordResetAsync(
                request.Email,
                CorrelationIdMiddleware.GetCorrelationId(HttpContext),
                cancellationToken);
        }
        catch (Vessel.Domain.Common.DomainException)
        {
            // Invalid address: same response as for unknown accounts.
        }

        return LocalRedirect("/password-reset?status=requested");
    }

    /// <summary>Form variant of the reset completion used by the emailed link (IAM-10).</summary>
    [HttpPost("/auth/password-reset/complete")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> ResetPasswordForm(
        [FromForm] PasswordResetCompleteRequest request,
        CancellationToken cancellationToken)
    {
        bool succeeded;
        try
        {
            succeeded = await _authenticationService.ResetPasswordAsync(
                request.Email,
                request.Token,
                request.NewPassword,
                CorrelationIdMiddleware.GetCorrelationId(HttpContext),
                cancellationToken);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                                              or Vessel.Domain.Common.DomainException)
        {
            succeeded = false;
        }

        return LocalRedirect(succeeded ? "/login?status=password-reset" : "/password-reset?status=invalid");
    }

    /// <summary>Sends a new verification email to the signed-in user (IAM-10).</summary>
    [Authorize(Policy = VesselAuthorizationPolicies.InteractiveSession)]
    [HttpPost("email-verification/send")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> SendEmailVerification(CancellationToken cancellationToken)
    {
        var queued = await _authEmails.RequestEmailVerificationAsync(User.GetUserId(), cancellationToken);
        return queued ? Accepted() : Problem("Email verification is unavailable or the address is already verified.",
            statusCode: StatusCodes.Status409Conflict);
    }

    /// <summary>Confirms an emailed verification token (POST only, so link scanners cannot redeem it).</summary>
    [HttpPost("/auth/verify-email")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> VerifyEmailForm([FromForm] VerifyEmailRequest request,
        CancellationToken cancellationToken)
    {
        var verified = await _authEmails.ConfirmEmailVerificationAsync(request.Token,
            CorrelationIdMiddleware.GetCorrelationId(HttpContext), cancellationToken);
        return LocalRedirect(verified ? "/verify-email?status=verified" : "/verify-email?status=invalid");
    }

    [Authorize(Policy = VesselAuthorizationPolicies.InteractiveSession)]
    [HttpPost("2fa/setup")]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<TwoFactorSetup>> StartTwoFactorSetup(CancellationToken cancellationToken)
    {
        try
        {
            return await _authenticationService.StartTwoFactorSetupAsync(
                User.GetUserId(),
                _hostOptions.ServiceName,
                cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    [Authorize(Policy = VesselAuthorizationPolicies.InteractiveSession)]
    [HttpPost("2fa/confirm")]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<TwoFactorRecoveryCodes>> ConfirmTwoFactor(
        TwoFactorConfirmRequest request,
        CancellationToken cancellationToken)
    {
        TwoFactorRecoveryCodes? result = await _authenticationService.ConfirmTwoFactorAsync(
            User.GetUserId(),
            request.Code,
            User.GetSessionId(),
            CorrelationIdMiddleware.GetCorrelationId(HttpContext),
            cancellationToken);

        return result is null ? BadRequest() : Ok(result);
    }

    [Authorize(Policy = VesselAuthorizationPolicies.InteractiveSession)]
    [HttpDelete("2fa")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> DisableTwoFactor(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] TwoFactorDisableRequest? request,
        CancellationToken cancellationToken)
    {
        // Disabling the second factor requires re-confirmation with a current TOTP code or the account password.
        var disabled = await _authenticationService.DisableTwoFactorAsync(
            User.GetUserId(),
            request?.CurrentPassword,
            request?.Code,
            User.GetSessionId(),
            CorrelationIdMiddleware.GetCorrelationId(HttpContext),
            cancellationToken);
        return disabled
            ? NoContent()
            : Problem("Re-confirm with a current two-factor code or your password.",
                statusCode: StatusCodes.Status403Forbidden);
    }

    /// <summary>The caller's active sessions (IAM-09); the current one is flagged.</summary>
    [Authorize(Policy = VesselAuthorizationPolicies.InteractiveSession)]
    [HttpGet("sessions")]
    public ActionResult<IReadOnlyList<SessionResponse>> Sessions()
    {
        return Ok(_sessions.List(User.GetUserId(), User.GetSessionId()).Select(SessionResponse.From).ToArray());
    }

    /// <summary>Revokes one of the caller's sessions.</summary>
    [Authorize(Policy = VesselAuthorizationPolicies.InteractiveSession)]
    [HttpDelete("sessions/{sessionId:guid}")]
    public async Task<IActionResult> RevokeSession(Guid sessionId, CancellationToken cancellationToken)
    {
        var id = new UserSessionId(sessionId);
        var revoked = await _sessions.RevokeAsync(User.GetUserId(), id, UserSessionService.ReasonRevokedByUser,
            CorrelationIdMiddleware.GetCorrelationId(HttpContext), cancellationToken);
        _sessionManager.Forget(id);
        return revoked ? NoContent() : NotFound();
    }

    /// <summary>"Sign out everywhere else": revokes every other session of the caller.</summary>
    [Authorize(Policy = VesselAuthorizationPolicies.InteractiveSession)]
    [HttpPost("sessions/revoke-others")]
    public async Task<ActionResult<RevokedSessionsResponse>> RevokeOtherSessions(CancellationToken cancellationToken)
    {
        if (User.GetSessionId() is not { } current) return Forbid();
        var revoked = await _sessions.RevokeOthersAsync(User.GetUserId(), current,
            CorrelationIdMiddleware.GetCorrelationId(HttpContext), cancellationToken);
        return Ok(new RevokedSessionsResponse(revoked));
    }

    private Task SignInAsync(AuthenticatedUser user, string method)
    {
        return _sessionManager.SignInAsync(HttpContext, user, method);
    }

    private static string NormalizeReturnUrl(string? returnUrl)
    {
        return string.IsNullOrWhiteSpace(returnUrl) || !Uri.IsWellFormedUriString(returnUrl, UriKind.Relative)
            ? "/"
            : $"/{returnUrl.TrimStart('/')}";
    }
}

public sealed record RegisterRequest(string Name, string Email, string Password);

public sealed record LoginRequest(string Email, string Password, string? TwoFactorCode);

public sealed record LoginFormRequest(string Email, string Password, string? TwoFactorCode, string? ReturnUrl);

public sealed record RegisterFormRequest(string Name, string Email, string Password);

public sealed record PasswordResetRequest(string Email);

public sealed record PasswordResetCompleteRequest(string Email, string Token, string NewPassword);

public sealed record VerifyEmailRequest(string Token);

public sealed record TwoFactorConfirmRequest(string Code);

public sealed record TwoFactorDisableRequest(string? CurrentPassword, string? Code);

public sealed record AuthResponse(Guid UserId, Guid TeamId, string Name, string Email, bool TwoFactorRequired)
{
    public static AuthResponse From(AuthenticatedUser user)
    {
        return new AuthResponse(
            user.UserId.Value,
            user.TeamId.Value,
            user.Name,
            user.Email,
            user.TwoFactorRequired);
    }
}

public sealed record SessionResponse(
    Guid Id,
    string AuthenticationMethod,
    string? IpAddress,
    string? UserAgent,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset ExpiresAt,
    bool Current)
{
    public static SessionResponse From(UserSessionSummary session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new SessionResponse(session.Id.Value, session.AuthenticationMethod, session.IpAddress,
            session.UserAgent, session.CreatedAt, session.LastSeenAt, session.ExpiresAt, session.IsCurrent);
    }
}

public sealed record RevokedSessionsResponse(int Revoked);
