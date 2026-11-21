using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Vessel.Domain;
using Vessel.Domain.Proxy;

namespace Vessel.Infrastructure.Persistence;

/// <summary>
///     Persistence conversions for routing types (route policies, routing ids). A route policy is stored as JSON; only
///     the basic-auth username and secret reference id are stored, never a password or hash. Missing fields (for
///     example rows created before policies existed, stored as <c>{}</c>) take the upstream defaults.
/// </summary>
internal static class RoutingConversions
{
    public const string EmptyPolicyJson = "{}";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static readonly ValueConverter<RoutePolicy, string> Policy = new(
        value => Serialize(value),
        value => Deserialize(value));

    public static readonly ValueConverter<ServiceDomainId, Guid> ServiceDomainId =
        new(id => id.Value, value => new ServiceDomainId(value));

    public static readonly ValueConverter<ProxyDynamicConfigurationId, Guid> ProxyDynamicConfigurationId =
        new(id => id.Value, value => new ProxyDynamicConfigurationId(value));

    public static string Serialize(RoutePolicy policy)
    {
        var document = new PolicyDocument(policy.ForceHttps, policy.RedirectMode, policy.PermanentRedirect,
            policy.Compression, policy.NoIndex, policy.StripPathPrefix,
            policy.BasicAuth is null
                ? null
                : new BasicAuthDocument(policy.BasicAuth.Username, policy.BasicAuth.PasswordSecretReferenceId.Value));
        return JsonSerializer.Serialize(document, JsonOptions);
    }

    public static RoutePolicy Deserialize(string json)
    {
        RoutePolicy defaults = RoutePolicy.Default;
        if (string.IsNullOrWhiteSpace(json)) return defaults;
        PolicyDocument document = JsonSerializer.Deserialize<PolicyDocument>(json, JsonOptions)
                                  ?? new PolicyDocument(null, null, null, null, null, null, null);
        return new RoutePolicy(
            document.ForceHttps ?? defaults.ForceHttps,
            document.RedirectMode ?? defaults.RedirectMode,
            document.PermanentRedirect ?? defaults.PermanentRedirect,
            document.Compression ?? defaults.Compression,
            document.NoIndex ?? defaults.NoIndex,
            document.StripPathPrefix ?? defaults.StripPathPrefix,
            document.BasicAuth is null
                ? null
                : new RouteBasicAuth(document.BasicAuth.Username,
                    new SecretReferenceId(document.BasicAuth.PasswordSecretReferenceId)));
    }

    private sealed record PolicyDocument(
        bool? ForceHttps,
        RouteRedirectMode? RedirectMode,
        bool? PermanentRedirect,
        bool? Compression,
        bool? NoIndex,
        bool? StripPathPrefix,
        BasicAuthDocument? BasicAuth);

    private sealed record BasicAuthDocument(string Username, Guid PasswordSecretReferenceId);
}
