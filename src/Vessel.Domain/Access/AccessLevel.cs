namespace Vessel.Domain.Access;

/// <summary>
///     Access levels shared by member grants, invitation grants and API token abilities (Coolify parity:
///     <c>read</c>, <c>read:sensitive</c>, <c>deploy</c>, <c>write</c>, <c>delete</c>, <c>root</c>).
///     A level implies the levels returned by <see cref="AccessLevels.Includes" />.
/// </summary>
public enum AccessLevel
{
    Read = 0,
    ReadSensitive = 1,
    Deploy = 2,
    Write = 3,
    Delete = 4,
    Root = 5
}

public static class AccessLevels
{
    public const string ReadAbility = "read";
    public const string ReadSensitiveAbility = "read:sensitive";
    public const string DeployAbility = "deploy";
    public const string WriteAbility = "write";
    public const string DeleteAbility = "delete";
    public const string RootAbility = "root";

    public static IReadOnlyList<AccessLevel> All { get; } =
    [
        AccessLevel.Read, AccessLevel.ReadSensitive, AccessLevel.Deploy, AccessLevel.Write, AccessLevel.Delete,
        AccessLevel.Root
    ];

    /// <summary>
    ///     The levels a grant of <paramref name="level" /> also gives. Write includes ReadSensitive because whoever can
    ///     change a resource can read its secrets back through a deploy; Delete and Deploy only include Read.
    /// </summary>
    public static IReadOnlyList<AccessLevel> Includes(this AccessLevel level)
    {
        return level switch
        {
            AccessLevel.Root => All,
            AccessLevel.Write => [AccessLevel.Write, AccessLevel.Deploy, AccessLevel.ReadSensitive, AccessLevel.Read],
            AccessLevel.Delete => [AccessLevel.Delete, AccessLevel.Read],
            AccessLevel.Deploy => [AccessLevel.Deploy, AccessLevel.Read],
            AccessLevel.ReadSensitive => [AccessLevel.ReadSensitive, AccessLevel.Read],
            _ => [AccessLevel.Read]
        };
    }

    /// <summary>Expands a set of granted levels with every implied level.</summary>
    public static IReadOnlySet<AccessLevel> Expand(IEnumerable<AccessLevel> levels)
    {
        var expanded = new HashSet<AccessLevel>();
        foreach (AccessLevel level in levels) expanded.UnionWith(level.Includes());
        return expanded;
    }

    public static string ToAbility(this AccessLevel level)
    {
        return level switch
        {
            AccessLevel.Read => ReadAbility,
            AccessLevel.ReadSensitive => ReadSensitiveAbility,
            AccessLevel.Deploy => DeployAbility,
            AccessLevel.Write => WriteAbility,
            AccessLevel.Delete => DeleteAbility,
            AccessLevel.Root => RootAbility,
            _ => throw new ArgumentOutOfRangeException(nameof(level), level, null)
        };
    }

    public static bool TryParseAbility(string? value, out AccessLevel level)
    {
        switch (value)
        {
            case ReadAbility:
                level = AccessLevel.Read;
                return true;
            case ReadSensitiveAbility:
                level = AccessLevel.ReadSensitive;
                return true;
            case DeployAbility:
                level = AccessLevel.Deploy;
                return true;
            case WriteAbility:
                level = AccessLevel.Write;
                return true;
            case DeleteAbility:
                level = AccessLevel.Delete;
                return true;
            case RootAbility:
                level = AccessLevel.Root;
                return true;
            default:
                level = default;
                return false;
        }
    }
}
