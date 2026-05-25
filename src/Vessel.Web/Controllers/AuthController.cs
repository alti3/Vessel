using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Vessel.Application.Auditing;
using Vessel.Application.Auth;
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
    private readonly VesselAuthenticationService _authenticationService;
    private readonly VesselHostOptions _hostOptions;

    public AuthController(
        VesselAuthenticationService authenticationService,
        IAuditWriter auditWriter,
        IConfiguration configuration)
    {
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
        AuthenticatedUser user = await _authenticationService.RegisterAsync(
            request.Name,
            request.Email,
            request.Password,
            CorrelationIdMiddleware.GetCorrelationId(HttpContext),
            cancellationToken);

        await SignInAsync(user);
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

        await SignInAsync(user);
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

        await SignInAsync(user);
        return LocalRedirect(NormalizeReturnUrl(request.ReturnUrl));
    }

    [HttpPost("/auth/register")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> RegisterForm(
        [FromForm] RegisterFormRequest request,
        CancellationToken cancellationToken)
    {
        AuthenticatedUser user = await _authenticationService.RegisterAsync(
            request.Name,
            request.Email,
            request.Password,
            CorrelationIdMiddleware.GetCorrelationId(HttpContext),
            cancellationToken);

        await SignInAsync(user);
        return LocalRedirect("/");
    }

    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
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

    [Authorize]
    [HttpPost("2fa/setup")]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<TwoFactorSetup>> StartTwoFactorSetup(CancellationToken cancellationToken)
    {
        return await _authenticationService.StartTwoFactorSetupAsync(
            User.GetUserId(),
            _hostOptions.ServiceName,
            cancellationToken);
    }

    [Authorize]
    [HttpPost("2fa/confirm")]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<TwoFactorRecoveryCodes>> ConfirmTwoFactor(
        TwoFactorConfirmRequest request,
        CancellationToken cancellationToken)
    {
        TwoFactorRecoveryCodes? result = await _authenticationService.ConfirmTwoFactorAsync(
            User.GetUserId(),
            request.Code,
            CorrelationIdMiddleware.GetCorrelationId(HttpContext),
            cancellationToken);

        return result is null ? BadRequest() : Ok(result);
    }

    [Authorize]
    [HttpDelete("2fa")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> DisableTwoFactor(CancellationToken cancellationToken)
    {
        await _authenticationService.DisableTwoFactorAsync(
            User.GetUserId(),
            CorrelationIdMiddleware.GetCorrelationId(HttpContext),
            cancellationToken);
        return NoContent();
    }

    private async Task SignInAsync(AuthenticatedUser user)
    {
        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, user.UserId.Value.ToString("D")),
            new(ClaimTypes.Name, user.Name),
            new(ClaimTypes.Email, user.Email),
            new(VesselClaimTypes.TeamId, user.TeamId.Value.ToString("D"))
        ];
        var identity = new ClaimsIdentity(claims, VesselAuthenticationSchemes.Cookie);
        await HttpContext.SignInAsync(VesselAuthenticationSchemes.Cookie, new ClaimsPrincipal(identity));
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

public sealed record TwoFactorConfirmRequest(string Code);

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
