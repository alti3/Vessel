using Vessel.Domain.Sources;

namespace Vessel.Application.Sources;

/// <summary>
///     A Git source as returned by the API. Secrets are never returned; <c>Has*</c> flags say which are configured.
/// </summary>
public sealed record GitSourceSummary(
    Guid Id,
    string Name,
    GitSourceKind Kind,
    bool IsSystemWide,
    bool OwnedByActiveTeam,
    string? HtmlUrl,
    string? ApiUrl,
    string? Organization,
    string? CustomSshUser,
    int? CustomSshPort,
    long? AppId,
    long? InstallationId,
    string? ClientId,
    string? Username,
    Guid? SshKeyId,
    bool HasPrivateKey,
    bool HasClientSecret,
    bool HasWebhookSecret,
    bool IsConnected,
    DateTimeOffset? AccessTokenExpiresAt,
    int ApplicationCount,
    string? WebhookPath,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
///     Creates a Git source. Required fields per kind: GitHub App — <c>AppId</c>, <c>PrivateKey</c> (PEM) and an
///     HTML URL (default github.com); GitLab/Gitea app — HTML URL or <c>Preset</c> and an <c>AccessToken</c> (with
///     <c>RefreshToken</c>, <c>ClientId</c> and <c>ClientSecret</c> for automatic refresh); deploy key —
///     <c>SshKeyId</c> of a git-related team SSH key; HTTPS credentials — HTML URL, <c>Username</c> and
///     <c>Password</c> (password or access token).
/// </summary>
public sealed record CreateGitSourceRequest(
    string Name,
    GitSourceKind Kind,
    string? HtmlUrl = null,
    string? Preset = null,
    string? ApiUrl = null,
    bool IsSystemWide = false,
    string? Organization = null,
    string? CustomSshUser = null,
    int? CustomSshPort = null,
    long? AppId = null,
    long? InstallationId = null,
    string? ClientId = null,
    string? ClientSecret = null,
    string? PrivateKey = null,
    string? WebhookSecret = null,
    string? AccessToken = null,
    string? RefreshToken = null,
    DateTimeOffset? AccessTokenExpiresAt = null,
    string? Username = null,
    string? Password = null,
    Guid? SshKeyId = null)
{
    public override string ToString()
    {
        return $"CreateGitSourceRequest {{ Name = {Name}, Kind = {Kind}, HtmlUrl = {HtmlUrl}, Secrets = [redacted] }}";
    }
}

/// <summary>
///     Updates a Git source. <c>null</c> keeps a value; a non-empty secret replaces the stored one (secrets cannot
///     be read back, only replaced). Empty strings clear optional plain fields.
/// </summary>
public sealed record UpdateGitSourceRequest(
    string? Name = null,
    string? HtmlUrl = null,
    string? ApiUrl = null,
    bool? IsSystemWide = null,
    string? Organization = null,
    string? CustomSshUser = null,
    int? CustomSshPort = null,
    long? AppId = null,
    long? InstallationId = null,
    string? ClientId = null,
    string? ClientSecret = null,
    string? PrivateKey = null,
    string? WebhookSecret = null,
    string? AccessToken = null,
    string? RefreshToken = null,
    DateTimeOffset? AccessTokenExpiresAt = null,
    string? Username = null,
    string? Password = null,
    Guid? SshKeyId = null)
{
    public override string ToString()
    {
        return $"UpdateGitSourceRequest {{ Name = {Name}, HtmlUrl = {HtmlUrl}, Secrets = [redacted] }}";
    }
}

public sealed record GitSourceRepositorySummary(
    string FullName,
    string CloneUrl,
    string? HtmlUrl,
    string? DefaultBranch,
    bool IsPrivate);

public sealed record GitSourceBranchSummary(string Name, string? CommitSha);

public sealed record AssignGitSourceRequest(Guid? SourceId);

public sealed record GitKnownHostSummary(
    Guid? Id,
    string Host,
    int Port,
    string KeyType,
    string Key,
    string Origin,
    DateTimeOffset? CreatedAt);

/// <summary>Pins (or confirms) a host key of a Git host for the team (operator override of trust on first use).</summary>
public sealed record AddGitKnownHostRequest(string Host, int Port, string KeyType, string Key);
