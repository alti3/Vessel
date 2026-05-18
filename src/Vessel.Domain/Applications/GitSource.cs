using Vessel.Domain.ValueObjects;

namespace Vessel.Domain.Applications;

public readonly record struct GitSource(
    RepositoryUrl RepositoryUrl,
    string Branch,
    string? CommitSha)
{
    public GitSource(RepositoryUrl repositoryUrl, string branch)
        : this(repositoryUrl, string.IsNullOrWhiteSpace(branch) ? "main" : branch.Trim(), null)
    {
    }
}
