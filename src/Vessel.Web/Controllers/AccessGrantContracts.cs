using Vessel.Application.Authorization;
using Vessel.Domain;
using Vessel.Domain.Access;
using Vessel.Domain.Common;

namespace Vessel.Web.Controllers;

/// <summary>
///     One access grant in API requests and responses: <c>scope</c> is <c>team</c>, <c>project</c>,
///     <c>environment</c> or <c>resource</c>; <c>target</c> is the id (omitted for <c>team</c>); <c>levels</c> are
///     <c>read</c>, <c>read:sensitive</c>, <c>deploy</c>, <c>write</c>, <c>delete</c>, <c>root</c>.
/// </summary>
public sealed record AccessGrantDto(string Scope, Guid? Target, IReadOnlyList<string> Levels)
{
    public static IReadOnlyList<AccessGrantDto>? From(IReadOnlyList<AccessGrant>? grants)
    {
        return grants?.Select(grant => new AccessGrantDto(
            grant.Scope.ToString().ToLowerInvariant(),
            grant.TargetId,
            grant.Levels.Select(level => level.ToAbility()).ToArray())).ToArray();
    }
}

/// <summary>Access in a request: explicit grants, or a preset (<c>viewer</c>, <c>developer</c>, <c>operator</c>, <c>full</c>).</summary>
public sealed record AccessRequestDto(IReadOnlyList<AccessGrantDto>? Grants, string? Preset, Guid? ProjectId)
{
    /// <summary>Converts to domain grants; <c>null</c> when neither grants nor a preset are given.</summary>
    /// <exception cref="InvalidOperationException">Unknown scope, level or preset.</exception>
    public IReadOnlyList<AccessGrant>? ToGrants()
    {
        if (!string.IsNullOrWhiteSpace(Preset))
        {
            if (Grants is not null) throw new InvalidOperationException("Send either grants or a preset, not both.");
            return AccessPresets.Grants(Preset.Trim().ToLowerInvariant(),
                ProjectId is { } projectId ? new ProjectId(projectId) : null);
        }

        if (Grants is null) return null;

        var grants = new List<AccessGrant>(Grants.Count);
        foreach (AccessGrantDto item in Grants)
        {
            if (!Enum.TryParse(item.Scope, true, out AccessScope scope) || !Enum.IsDefined(scope))
                throw new InvalidOperationException($"Access scope '{item.Scope}' is not supported.");

            var levels = new List<AccessLevel>();
            foreach (var value in item.Levels ?? [])
            {
                if (!AccessLevels.TryParseAbility(value, out AccessLevel level))
                    throw new InvalidOperationException($"Access level '{value}' is not supported.");
                levels.Add(level);
            }

            try
            {
                grants.Add(new AccessGrant(scope, item.Target, levels));
            }
            catch (DomainException exception)
            {
                throw new InvalidOperationException(exception.Message, exception);
            }
        }

        return grants;
    }
}
