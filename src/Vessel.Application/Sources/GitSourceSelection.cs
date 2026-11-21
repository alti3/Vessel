using Vessel.Application.Persistence;
using Vessel.Domain;
using Vessel.Domain.Common;
using Vessel.Domain.Sources;
using Vessel.Domain.ValueObjects;

namespace Vessel.Application.Sources;

/// <summary>
///     Validates the Git source chosen for an application (create, update, source selection): the source must exist,
///     be owned by the application's team or be system-wide, and accept the repository URL (host binding).
/// </summary>
public static class GitSourceSelection
{
    public static GitSourceConnection? Resolve(IVesselDbContext dbContext, TeamId teamId, Guid? sourceId,
        RepositoryUrl repository)
    {
        if (sourceId is null) return null;
        var id = new GitSourceConnectionId(sourceId.Value);
        GitSourceConnection source = dbContext.GitSourceConnections.SingleOrDefault(candidate => candidate.Id == id)
                                     ?? throw new UnauthorizedAccessException("Git source is outside the active team.");
        if (!GitCredentialResolver.IsUsableBy(source, teamId))
            throw new UnauthorizedAccessException("Git source is outside the active team.");
        var refusal = source.CheckRepository(repository);
        if (refusal is not null) throw new DomainException(refusal);
        return source;
    }
}
