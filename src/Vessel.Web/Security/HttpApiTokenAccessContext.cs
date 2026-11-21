using Vessel.Application.Authorization;

namespace Vessel.Web.Security;

/// <summary>
///     Supplies the API token of the current HTTP request to Application authorization (IAM-05 token layer). The bearer
///     handler stores the validated token in <see cref="HttpContext.Items" />; cookie sessions, Blazor circuits and
///     background work have no token layer. A token is only reported while the request principal is still the bearer
///     principal that produced it, so a cookie session can never inherit token scope limits or abilities.
/// </summary>
internal sealed class HttpApiTokenAccessContext(IHttpContextAccessor httpContextAccessor) : IApiTokenAccessContext
{
    internal const string ItemKey = "vessel:api-token-access";

    public ApiTokenAccess? Current
    {
        get
        {
            HttpContext? context = httpContextAccessor.HttpContext;
            if (context is null || !context.User.IsBearerTokenPrincipal()) return null;
            return context.Items.TryGetValue(ItemKey, out var value) ? value as ApiTokenAccess : null;
        }
    }
}
