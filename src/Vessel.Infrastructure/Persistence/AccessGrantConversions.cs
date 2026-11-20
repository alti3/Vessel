using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Vessel.Domain.Access;

namespace Vessel.Infrastructure.Persistence;

/// <summary>
///     Stores access grant lists (IAM-05) as <c>jsonb</c>:
///     <c>[{"scope":"project","target":"&lt;uuid&gt;","levels":["write","deploy"]}]</c>. A <c>NULL</c> column means
///     "default" (team-wide read for members, the whole team for tokens). Unknown scopes or levels are dropped when
///     read, so a downgrade never widens access.
/// </summary>
internal static class AccessGrantConversions
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly ValueConverter<IReadOnlyList<AccessGrant>, string> Converter = new(
        grants => Serialize(grants),
        json => Deserialize(json));

    private static readonly ValueComparer<IReadOnlyList<AccessGrant>?> Comparer = new(
        (left, right) => Serialize(left) == Serialize(right),
        grants => Serialize(grants).GetHashCode(StringComparison.Ordinal),
        grants => Snapshot(grants));

    public static PropertyBuilder<IReadOnlyList<AccessGrant>?> HasAccessGrantsConversion(
        this PropertyBuilder<IReadOnlyList<AccessGrant>?> builder)
    {
        builder.Metadata.SetValueConverter(Converter);
        builder.Metadata.SetValueComparer(Comparer);
        return builder.HasColumnType("jsonb").IsRequired(false);
    }

    public static string Serialize(IReadOnlyList<AccessGrant>? grants)
    {
        if (grants is null) return string.Empty;
        return JsonSerializer.Serialize(
            grants.Select(grant => new StoredGrant(
                grant.Scope.ToString().ToLowerInvariant(),
                grant.TargetId,
                grant.Levels.Select(level => level.ToAbility()).ToArray())).ToArray(),
            Json);
    }

    public static IReadOnlyList<AccessGrant> Deserialize(string json)
    {
        StoredGrant[]? stored = JsonSerializer.Deserialize<StoredGrant[]>(json, Json);
        if (stored is null) return [];

        var grants = new List<AccessGrant>(stored.Length);
        foreach (StoredGrant item in stored)
        {
            if (!Enum.TryParse(item.Scope, true, out AccessScope scope) || !Enum.IsDefined(scope)) continue;
            if (scope == AccessScope.Team ? item.Target is not null : item.Target is null || item.Target == Guid.Empty)
                continue;

            var levels = new List<AccessLevel>();
            foreach (var ability in item.Levels ?? [])
                if (AccessLevels.TryParseAbility(ability, out AccessLevel level))
                    levels.Add(level);
            grants.Add(new AccessGrant(scope, item.Target, levels));
        }

        return grants;
    }

    private static AccessGrant[]? Snapshot(IReadOnlyList<AccessGrant>? grants)
    {
        return grants?.ToArray();
    }

    private sealed record StoredGrant(string Scope, Guid? Target, string[]? Levels);
}
