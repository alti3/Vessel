using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Vessel.Application.Authorization;
using Vessel.Application.Notifications;
using Vessel.Application.Redis;
using Vessel.Application.Security.ApiAbuse;
using Vessel.Domain;
using Vessel.Web.Configuration;
using Vessel.Web.Controllers;
using Vessel.Web.Controllers.Api.V1;
using Vessel.Web.Middleware;
using Vessel.Web.Security;

namespace Vessel.E2ETests.Security;

/// <summary>Web-boundary tests for IAM-04/05/06 endpoints, the SEC-13 failed-request throttle and SEC-07 proxy trust.</summary>
public sealed class AccessAndThrottleEndpointTests
{
    [Theory]
    [InlineData(nameof(TokensController.Update))]
    [InlineData(nameof(TokensController.Pause))]
    [InlineData(nameof(TokensController.Resume))]
    [InlineData(nameof(TokensController.Roll))]
    public void TokenLifecycleEndpoints_RequireAnInteractiveSession(string action)
    {
        AssertPolicy(typeof(TokensController).GetMethod(action)!, VesselAuthorizationPolicies.InteractiveSession);
    }

    [Fact]
    public void InstanceEndpoints_RequireAnInteractiveInstanceAdministrator()
    {
        AssertPolicy(typeof(InstanceController), VesselAuthorizationPolicies.InteractiveSession);
        AssertPolicy(typeof(InstanceController), VesselPermissions.InstanceManage);
        AssertPolicy(typeof(TeamsController).GetMethod(nameof(TeamsController.ChangeAccess))!,
            VesselAuthorizationPolicies.InteractiveSession);
    }

    [Fact]
    public void DestructiveAndLifecycleEndpoints_UseDeleteAndDeployPermissions()
    {
        AssertPolicy(typeof(ProjectsController).GetMethod(nameof(ProjectsController.Archive))!,
            VesselPermissions.ProjectsDelete);
        AssertPolicy(typeof(DatabasesController).GetMethod(nameof(DatabasesController.Lifecycle))!,
            VesselPermissions.ResourcesLifecycle);
    }

    [Fact]
    public void TokenContext_IsOnlyReportedForBearerPrincipals()
    {
        var token = new ApiTokenAccess(PersonalAccessTokenId.New(), UserId.New(), TeamId.New(),
            new HashSet<string> { TokenScopeMapper.Read }, null);
        var context = new DefaultHttpContext();
        context.Items[HttpApiTokenAccessContext.ItemKey] = token;
        var accessor = new HttpContextAccessor { HttpContext = context };
        var tokenContext = new HttpApiTokenAccessContext(accessor);

        context.User = VesselSessionPrincipal.Create(token.UserId, "u", "u@example.com", token.TeamId);
        Assert.Null(tokenContext.Current);

        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier,
            token.UserId.Value.ToString("D"))], VesselAuthenticationSchemes.Bearer));
        Assert.Same(token, tokenContext.Current);
    }

    [Fact]
    public async Task FailedRequestThrottle_Answers429AfterTheLimit_AndIgnoresSuccessfulRequests()
    {
        ApiFailedRequestThrottle throttle = CreateThrottle(limit: 2);
        var forbidden = new ApiFailedRequestThrottleMiddleware(context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        });
        var ok = new ApiFailedRequestThrottleMiddleware(_ => Task.CompletedTask);

        for (var i = 0; i < 5; i++) Assert.Equal(200, await InvokeAsync(ok, throttle, "/api/v1/projects"));
        Assert.Equal(403, await InvokeAsync(forbidden, throttle, "/api/v1/projects"));
        Assert.Equal(403, await InvokeAsync(forbidden, throttle, "/api/v1/projects"));

        HttpContext blocked = Context("/api/v1/projects");
        await ok.InvokeAsync(blocked, throttle);
        Assert.Equal(StatusCodes.Status429TooManyRequests, blocked.Response.StatusCode);
        Assert.True(int.Parse(blocked.Response.Headers.RetryAfter.ToString(), System.Globalization.CultureInfo.InvariantCulture) > 0);

        // Health endpoints and non-API paths are never throttled; other clients have their own bucket.
        Assert.Equal(200, await InvokeAsync(ok, throttle, "/api/health"));
        Assert.Equal(200, await InvokeAsync(ok, throttle, "/applications"));
        Assert.Equal(200, await InvokeAsync(ok, throttle, "/api/v1/projects", "198.51.100.20"));
    }

    [Fact]
    public async Task FailedRequestThrottle_CountsApplicationAuthorizationFailures()
    {
        ApiFailedRequestThrottle throttle = CreateThrottle(limit: 1);
        var denied = new ApiFailedRequestThrottleMiddleware(_ => throw new UnauthorizedAccessException("denied"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            denied.InvokeAsync(Context("/api/v1/projects"), throttle));

        HttpContext blocked = Context("/api/v1/projects");
        await denied.InvokeAsync(blocked, throttle);
        Assert.Equal(StatusCodes.Status429TooManyRequests, blocked.Response.StatusCode);
    }

    [Theory]
    [InlineData(true, new string[0], new string[0], false)]
    [InlineData(true, new[] { "127.0.0.1" }, new string[0], true)]
    [InlineData(true, new string[0], new[] { "172.18.0.0/16" }, true)]
    [InlineData(false, new[] { "not-an-ip" }, new string[0], false)]
    [InlineData(false, new string[0], new[] { "10.0.0.0/99" }, false)]
    [InlineData(false, new string[0], new string[0], true)]
    public void ForwardedHeaders_RequireAnExplicitTrustedProxyList(bool enabled, string[] proxies, string[] networks,
        bool valid)
    {
        var settings = new ForwardedHeadersSettings
        {
            Enabled = enabled,
            KnownProxies = proxies,
            KnownNetworks = networks
        };

        Assert.Equal(valid, settings.Validate().Count == 0);
    }

    private static async Task<int> InvokeAsync(ApiFailedRequestThrottleMiddleware middleware,
        ApiFailedRequestThrottle throttle, string path, string ip = "203.0.113.50")
    {
        HttpContext context = Context(path, ip);
        await middleware.InvokeAsync(context, throttle);
        return context.Response.StatusCode;
    }

    private static HttpContext Context(string path, string ip = "203.0.113.50")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = "GET";
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ip);
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static ApiFailedRequestThrottle CreateThrottle(int limit)
    {
        var time = new FixedTime(DateTimeOffset.Parse("2026-11-20T12:00:10Z",
            System.Globalization.CultureInfo.InvariantCulture));
        return new ApiFailedRequestThrottle(new InMemoryRedisCache(time), time,
            new NullPublisher(), new ApiFailedRequestThrottleSettings(limit, TimeSpan.FromSeconds(60)),
            NullLogger<ApiFailedRequestThrottle>.Instance);
    }

    private static void AssertPolicy(MemberInfo member, string policy)
    {
        Assert.Contains(member.GetCustomAttributes<AuthorizeAttribute>(true), attribute => attribute.Policy == policy);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return now;
        }
    }

    private sealed class NullPublisher : INotificationEventPublisher
    {
        public Task PublishAsync(TeamId teamId, RaiseNotificationRequest notification,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task PublishAsync(Func<TeamNotification?> build, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
