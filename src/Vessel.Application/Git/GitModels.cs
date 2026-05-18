namespace Vessel.Application.Git;

public sealed record GitRepositoryRef(string Name, string Sha, bool IsTag);

public sealed record GitCommitInfo(string Sha, string AuthorName, string AuthorEmail, DateTimeOffset AuthoredAt, string Subject);

public sealed record GitCloneRequest(
    Uri RepositoryUrl,
    string TargetDirectory,
    string? BranchOrTag = null,
    int Depth = 1,
    bool RecurseSubmodules = false,
    IReadOnlyDictionary<string, string?>? Environment = null);
