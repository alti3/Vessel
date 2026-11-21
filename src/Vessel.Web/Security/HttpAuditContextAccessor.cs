using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Domain.Auditing;

namespace Vessel.Web.Security;

/// <summary>
///     Audit context of the current HTTP request (IAM-08): <c>api</c> for bearer-token requests, <c>webhook</c> for
///     inbound webhooks, otherwise <c>ui</c>. The client address is <see cref="ConnectionInfo.RemoteIpAddress" />, which
///     the forwarded-headers middleware rewrites only for explicitly trusted proxies; a spoofed
///     <c>X-Forwarded-For</c> never reaches the audit log. Outside a request (Blazor circuits after the first render,
///     jobs) the ambient <see cref="AuditContextScope" /> or <see cref="AuditContext.System" /> applies.
/// </summary>
internal sealed class HttpAuditContextAccessor(
    IHttpContextAccessor httpContextAccessor,
    IApiTokenAccessContext tokenAccess) : IAuditContextAccessor
{
    public AuditContext Current
    {
        get
        {
            if (AuditContextScope.Ambient is { } ambient) return ambient;
            HttpContext? context = httpContextAccessor.HttpContext;
            if (context is null) return AuditContext.System;

            ApiTokenAccess? token = tokenAccess.Current;
            var source = context.Request.Path.StartsWithSegments("/webhooks", StringComparison.OrdinalIgnoreCase)
                ? AuditSources.Webhook
                : token is not null || context.User.IsBearerTokenPrincipal()
                    ? AuditSources.Api
                    : AuditSources.Ui;

            string? userAgent = context.Request.Headers.UserAgent;
            return new AuditContext(
                source,
                context.Connection.RemoteIpAddress?.ToString(),
                string.IsNullOrWhiteSpace(userAgent)
                    ? null
                    : userAgent.Length <= AuditLog.UserAgentMaxLength
                        ? userAgent
                        : userAgent[..AuditLog.UserAgentMaxLength],
                token?.TokenId);
        }
    }
}
