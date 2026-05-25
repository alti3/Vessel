namespace Vessel.Application.Authorization;

public static class TokenScopeMapper
{
    public const string Root = "root";
    public const string Read = "read";
    public const string ReadSensitive = "read:sensitive";
    public const string Write = "write";
    public const string WriteSensitive = "write:sensitive";
    public const string Deploy = "deploy";

    public static readonly IReadOnlySet<string> AllowedScopes = new HashSet<string>(StringComparer.Ordinal)
    {
        Root,
        Read,
        ReadSensitive,
        Write,
        WriteSensitive,
        Deploy
    };

    public static IReadOnlySet<string> ToPermissions(IEnumerable<string> scopes)
    {
        var normalized = scopes.ToHashSet(StringComparer.Ordinal);
        if (normalized.Contains(Root)) return VesselPermissions.All;

        var permissions = new HashSet<string>(StringComparer.Ordinal);
        if (normalized.Contains(Read))
        {
            permissions.Add(VesselPermissions.ProjectsRead);
            permissions.Add(VesselPermissions.ServersRead);
            permissions.Add(VesselPermissions.ApplicationsRead);
            permissions.Add(VesselPermissions.DeploymentsReadLogs);
        }

        if (normalized.Contains(ReadSensitive)) permissions.Add(VesselPermissions.SecretsRead);

        if (normalized.Contains(Write))
        {
            permissions.Add(VesselPermissions.ProjectsWrite);
            permissions.Add(VesselPermissions.ServersWrite);
            permissions.Add(VesselPermissions.ApplicationsWrite);
            permissions.Add(VesselPermissions.DeploymentsStart);
            permissions.Add(VesselPermissions.DeploymentsCancel);
            permissions.Add(VesselPermissions.SettingsManage);
            permissions.Add(VesselPermissions.TeamsManage);
        }

        if (normalized.Contains(WriteSensitive)) permissions.Add(VesselPermissions.SecretsWrite);
        if (normalized.Contains(Deploy)) permissions.Add(VesselPermissions.DeploymentsStart);

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
}
