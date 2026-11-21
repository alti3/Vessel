using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.Extensions.Caching.Memory;
using Vessel.Application.Auth;
using Vessel.Application.Auth.Sessions;
using Vessel.Domain;
using Vessel.Domain.Auditing;

namespace Vessel.Web.Security;

/// <summary>
///     Issues and validates interactive sessions (IAM-09). Sign-in creates a server-side <c>user_sessions</c> row and a
///     cookie carrying its id. Every cookie request re-validates the row (cached for <see cref="ValidationCacheLifetime" />
///     per session): revoked or expired sessions are rejected, and the active team comes from the row, so a removed
///     member or a stale concurrent request never keeps an old team.
/// </summary>
public sealed class VesselSessionManager(UserSessionService sessions, IMemoryCache cache)
{
    public static readonly TimeSpan ValidationCacheLifetime = TimeSpan.FromSeconds(15);

    public async Task SignInAsync(HttpContext context, AuthenticatedUser user, string method)
    {
        string? userAgent = context.Request.Headers.UserAgent;
        UserSessionId sessionId = await sessions.StartAsync(user.UserId, user.TeamId, method,
            new SessionClient(context.Connection.RemoteIpAddress?.ToString(),
                userAgent is { Length: > AuditLog.UserAgentMaxLength } ? userAgent[..AuditLog.UserAgentMaxLength] : userAgent),
            context.RequestAborted);
        await context.SignInAsync(VesselAuthenticationSchemes.Cookie,
            VesselSessionPrincipal.Create(user.UserId, user.Name, user.Email, user.TeamId, sessionId));
    }

    public void Forget(UserSessionId sessionId)
    {
        cache.Remove(CacheKey(sessionId));
    }

    /// <summary>Validates a cookie principal; returns the active team, or <c>null</c> when the session is over.</summary>
    public async Task<TeamId?> ValidateAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        if (principal.GetSessionId() is not { } sessionId) return null;
        UserId userId;
        try
        {
            userId = principal.GetUserId();
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException)
        {
            return null;
        }

        if (cache.TryGetValue(CacheKey(sessionId), out SessionValidationResult? cached) && cached is not null)
            return cached.IsActive ? cached.ActiveTeamId : null;

        SessionValidationResult result = await sessions.ValidateAsync(sessionId, userId, cancellationToken);
        cache.Set(CacheKey(sessionId), result, ValidationCacheLifetime);
        return result.IsActive ? result.ActiveTeamId : null;
    }

    private static string CacheKey(UserSessionId sessionId)
    {
        return "vessel:session:" + sessionId.Value.ToString("N");
    }
}

/// <summary>Cookie events: re-validates the server-side session on every request (IAM-09).</summary>
internal sealed class VesselCookieAuthenticationEvents : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        if (context.Principal is null)
        {
            context.RejectPrincipal();
            return;
        }

        VesselSessionManager manager = context.HttpContext.RequestServices.GetRequiredService<VesselSessionManager>();
        TeamId? activeTeam = await manager.ValidateAsync(context.Principal, context.HttpContext.RequestAborted);
        if (activeTeam is not { } teamId)
        {
            // Sessions issued before server-side sessions existed have no id and must sign in again.
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(VesselAuthenticationSchemes.Cookie);
            return;
        }

        if (context.Principal.GetTeamId() != teamId)
        {
            context.ReplacePrincipal(VesselSessionPrincipal.WithTeam(context.Principal, teamId));
            context.ShouldRenew = true;
        }
    }
}

/// <summary>Ends Blazor circuits whose session was revoked (re-checked every minute).</summary>
internal sealed class VesselRevalidatingAuthenticationStateProvider(
    ILoggerFactory loggerFactory,
    IServiceScopeFactory scopeFactory) : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState,
        CancellationToken cancellationToken)
    {
        ClaimsPrincipal user = authenticationState.User;
        if (user.Identity?.IsAuthenticated != true) return true;
        if (user.IsBearerTokenPrincipal()) return true;
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        VesselSessionManager manager = scope.ServiceProvider.GetRequiredService<VesselSessionManager>();
        return await manager.ValidateAsync(user, cancellationToken) is { } teamId && teamId == user.GetTeamId();
    }
}
