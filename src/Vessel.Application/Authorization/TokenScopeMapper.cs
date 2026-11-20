using Vessel.Domain.Access;

namespace Vessel.Application.Authorization;

/// <summary>
///     API token abilities (stored space-separated in <c>personal_access_tokens.scopes</c>). Abilities are the access
///     levels (<c>read</c>, <c>read:sensitive</c>, <c>deploy</c>, <c>write</c>, <c>delete</c>, <c>root</c>) with the
///     <see cref="AccessLevels.Includes" /> hierarchy, plus the owner/admin-only <c>write:sensitive</c> ability.
/// </summary>
public static class TokenScopeMapper
{
    public const string Root = AccessLevels.RootAbility;
    public const string Read = AccessLevels.ReadAbility;
    public const string ReadSensitive = AccessLevels.ReadSensitiveAbility;
    public const string Write = AccessLevels.WriteAbility;
    public const string WriteSensitive = "write:sensitive";
    public const string Deploy = AccessLevels.DeployAbility;
    public const string Delete = AccessLevels.DeleteAbility;

    public static readonly IReadOnlySet<string> AllowedScopes = new HashSet<string>(StringComparer.Ordinal)
    {
        Root,
        Read,
        ReadSensitive,
        Write,
        WriteSensitive,
        Deploy,
        Delete
    };

    /// <summary>Abilities only owners and admins may put on a token.</summary>
    public static readonly IReadOnlySet<string> PrivilegedScopes = new HashSet<string>(StringComparer.Ordinal)
    {
        Root,
        WriteSensitive
    };

    /// <summary>The access levels the abilities grant, already expanded with every implied level.</summary>
    public static IReadOnlySet<AccessLevel> ToLevels(IEnumerable<string> scopes)
    {
        var levels = new List<AccessLevel>();
        foreach (var scope in scopes)
            if (AccessLevels.TryParseAbility(scope, out AccessLevel level))
                levels.Add(level);

        return AccessLevels.Expand(levels);
    }

    /// <summary>The permissions the abilities allow (before the owner's role, grants and token scope limits).</summary>
    public static IReadOnlySet<string> ToPermissions(IEnumerable<string> scopes)
    {
        var normalized = scopes.ToHashSet(StringComparer.Ordinal);
        if (normalized.Contains(Root)) return VesselPermissions.All;

        IReadOnlySet<AccessLevel> levels = ToLevels(normalized);
        var permissions = VesselPermissions.Rules
            .Where(rule => rule.Value.Reach != PermissionReach.InstanceAdmin && levels.Contains(rule.Value.Level))
            .Select(rule => rule.Key)
            .ToHashSet(StringComparer.Ordinal);

        if (normalized.Contains(WriteSensitive)) permissions.Add(VesselPermissions.SecretsWrite);

        return permissions;
    }

    public static string Serialize(IEnumerable<string> scopes)
    {
        var normalized = scopes
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Select(scope => scope.Trim())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (normalized.Length == 0) normalized = [Read];

        if (normalized.Any(scope => !AllowedScopes.Contains(scope)))
            throw new InvalidOperationException("One or more token scopes are not supported.");

        return string.Join(' ', normalized);
    }

    public static IReadOnlySet<string> Deserialize(string scopes)
    {
        return scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    ///     Data step for the IAM-04 migration (Coolify <c>2026_10_25_100000</c> parity): <c>delete</c> became its own
    ///     ability, so every stored token that holds <c>write</c> also receives <c>delete</c>. Other abilities are kept.
    /// </summary>
    public static string MigrateLegacyScopes(string storedScopes)
    {
        IReadOnlySet<string> scopes = Deserialize(storedScopes);
        return scopes.Contains(Write) && !scopes.Contains(Delete)
            ? Serialize(scopes.Append(Delete))
            : storedScopes;
    }
}
