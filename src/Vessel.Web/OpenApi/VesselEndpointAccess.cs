using Microsoft.AspNetCore.Authorization;
using Vessel.Application.Authorization;
using Vessel.Web.Security;

namespace Vessel.Web.OpenApi;

/// <summary>
///     What an endpoint requires, read from its authorization metadata (controller and action attributes). Used for the
///     OpenAPI <c>x-vessel-*</c> annotations and by the API contract tests.
/// </summary>
public sealed record VesselEndpointAccess(
    bool Anonymous,
    IReadOnlyList<string> Permissions,
    bool SessionOnly,
    IReadOnlyList<string> TokenAbilities,
    IReadOnlyList<string> OtherPolicies)
{
    /// <summary>Token abilities in their canonical (least to most powerful) order.</summary>
    private static readonly string[] AbilityOrder =
    [
        TokenScopeMapper.Read,
        TokenScopeMapper.ReadSensitive,
        TokenScopeMapper.Deploy,
        TokenScopeMapper.Write,
        TokenScopeMapper.WriteSensitive,
        TokenScopeMapper.Delete,
        TokenScopeMapper.Root
    ];

    /// <summary>True when the endpoint declares how it is protected: a permission, a session policy or anonymity.</summary>
    public bool DeclaresAccessLevel => Anonymous || SessionOnly || Permissions.Count > 0 || OtherPolicies.Count > 0;

    public static VesselEndpointAccess From(IEnumerable<object> metadata)
    {
        var items = metadata as IReadOnlyCollection<object> ?? metadata.ToList();
        var authorizeData = items.OfType<IAuthorizeData>().ToList();
        if (items.OfType<IAllowAnonymous>().Any() || authorizeData.Count == 0)
            return new VesselEndpointAccess(true, [], false, [], []);

        var policies = authorizeData
            .Select(data => data.Policy)
            .Where(policy => !string.IsNullOrWhiteSpace(policy))
            .Select(policy => policy!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var permissions = policies.Where(VesselPermissions.All.Contains).Order(StringComparer.Ordinal).ToList();
        var sessionOnly = policies.Contains(VesselAuthorizationPolicies.InteractiveSession, StringComparer.Ordinal);
        var rootTokenOnly = policies.Contains(VesselAuthorizationPolicies.InteractiveSessionOrRootToken,
            StringComparer.Ordinal);
        var other = policies
            .Where(policy => !VesselPermissions.All.Contains(policy)
                             && policy != VesselAuthorizationPolicies.InteractiveSession)
            .Order(StringComparer.Ordinal)
            .ToList();

        IReadOnlyList<string> abilities = sessionOnly
            ? []
            : AbilityOrder
                .Where(ability => !rootTokenOnly || ability == TokenScopeMapper.Root)
                .Where(ability => permissions.Count == 0 || permissions.All(
                    TokenScopeMapper.ToPermissions([ability]).Contains))
                .ToList();

        return new VesselEndpointAccess(false, permissions, sessionOnly, abilities, other);
    }
}
