using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Vessel.Web.OpenApi;

/// <summary>
///     Annotates every operation with what it requires: <c>x-vessel-permissions</c>, <c>x-vessel-token-abilities</c>,
///     <c>x-vessel-session-only</c> / <c>x-vessel-anonymous</c>, and the matching per-operation <c>security</c>.
/// </summary>
internal sealed class VesselOpenApiOperationTransformer : IOpenApiOperationTransformer
{
    public const string PermissionsExtension = "x-vessel-permissions";
    public const string TokenAbilitiesExtension = "x-vessel-token-abilities";
    public const string SessionOnlyExtension = "x-vessel-session-only";
    public const string AnonymousExtension = "x-vessel-anonymous";
    public const string PoliciesExtension = "x-vessel-policies";

    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        VesselEndpointAccess access = VesselEndpointAccess.From(context.Description.ActionDescriptor.EndpointMetadata);
        operation.Extensions ??= new Dictionary<string, IOpenApiExtension>(StringComparer.Ordinal);

        if (access.Anonymous)
        {
            operation.Extensions[AnonymousExtension] = new JsonNodeExtension(JsonValue.Create(true));
            return Task.CompletedTask;
        }

        operation.Extensions[PermissionsExtension] = new JsonNodeExtension(ToArray(access.Permissions));
        operation.Extensions[TokenAbilitiesExtension] = new JsonNodeExtension(ToArray(access.TokenAbilities));
        if (access.SessionOnly)
            operation.Extensions[SessionOnlyExtension] = new JsonNodeExtension(JsonValue.Create(true));
        if (access.OtherPolicies.Count > 0)
            operation.Extensions[PoliciesExtension] = new JsonNodeExtension(ToArray(access.OtherPolicies));

        OpenApiDocument? document = context.Document;
        if (document is null) return Task.CompletedTask;

        VesselOpenApiSecuritySchemes.Ensure(document);
        operation.Security ??= [];
        // Alternatives: a session always works; a token works unless the operation is session-only.
        operation.Security.Add(Requirement(VesselOpenApiSecuritySchemes.Cookie, document));
        if (!access.SessionOnly && access.TokenAbilities.Count > 0)
            operation.Security.Add(Requirement(VesselOpenApiSecuritySchemes.Bearer, document));

        return Task.CompletedTask;
    }

    private static OpenApiSecurityRequirement Requirement(string scheme, OpenApiDocument document)
    {
        return new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(scheme, document)] = [] };
    }

    private static JsonArray ToArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add(value);
        return array;
    }
}
