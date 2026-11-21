using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Vessel.Web.OpenApi;

/// <summary>Sets the document info and registers the two ways to authenticate: API tokens and the interactive session.</summary>
internal sealed class VesselOpenApiDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Info ??= new OpenApiInfo();
        document.Info.Title = "Vessel API";
        document.Info.Version = VesselOpenApiExtensions.ApiVersion();
        document.Info.Description =
            "Vessel-native REST contract (GUID identifiers, ApiErrorResponse errors). Operations carry " +
            "x-vessel-permissions (the permission policies they require), x-vessel-token-abilities (the API token " +
            "abilities that can satisfy them; the token owner's team role and access grants still apply) and " +
            "x-vessel-session-only (interactive browser session required, API tokens rejected). " +
            "Versioning and deprecation: docs/api/compatibility-policy.md.";

        VesselOpenApiSecuritySchemes.Ensure(document);
        return Task.CompletedTask;
    }
}

internal static class VesselOpenApiSecuritySchemes
{
    public const string Bearer = "bearer";
    public const string Cookie = "cookie";
    public const string SessionCookieName = "__Host-Vessel";

    public static void Ensure(OpenApiDocument document)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal);

        document.Components.SecuritySchemes.TryAdd(Bearer, new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            Description = "Vessel personal access token: `Authorization: Bearer <token>`. Abilities: read, " +
                          "read:sensitive, deploy, write, write:sensitive, delete, root."
        });
        document.Components.SecuritySchemes.TryAdd(Cookie, new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Cookie,
            Name = SessionCookieName,
            Description = "Interactive browser session established by signing in to the Vessel UI."
        });
    }
}
