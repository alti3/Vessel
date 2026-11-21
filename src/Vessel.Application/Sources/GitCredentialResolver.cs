using Vessel.Application.Auditing;
using Vessel.Application.Git;
using Vessel.Application.Persistence;
using Vessel.Application.Redis;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Common;
using Vessel.Domain.Sources;
using Vessel.Domain.Ssh;
using Vessel.Domain.ValueObjects;
using AppEntity = Vessel.Domain.Applications.Application;

namespace Vessel.Application.Sources;

/// <summary>Repository location and credentials for one git operation.</summary>
public sealed record GitCloneAccess(Uri RepositoryUri, GitAuthentication Authentication, string? SourceName);

/// <summary>
///     Produces short-lived clone credentials for an application's repository (DEP-08): a GitHub App installation token
///     minted per clone, a (refreshed) GitLab/Gitea OAuth token, an HTTPS token, or a deploy key, plus SSH host trust
///     (published provider keys, the team's pinned keys, or trust on first use).
/// </summary>
public interface IGitCredentialProvider
{
    Task<GitCloneAccess> ForApplicationAsync(AppEntity application, TeamId teamId,
        CancellationToken cancellationToken = default);

    Task<GitCloneAccess> ForRepositoryAsync(GitSourceConnection? source, RepositoryUrl repository, TeamId teamId,
        CancellationToken cancellationToken = default);

    /// <summary>Access token for provider REST calls (GitHub installation token or GitLab/Gitea OAuth token).</summary>
    Task<string> ApiTokenAsync(GitSourceConnection source, CancellationToken cancellationToken = default);
}

public sealed class GitCredentialResolver(
    IVesselDbContext dbContext,
    ISecretVault secretVault,
    IGitProviderApi providerApi,
    IDistributedLockManager locks,
    IAuditWriter auditWriter,
    TimeProvider timeProvider) : IGitCredentialProvider
{
    /// <summary>OAuth tokens expiring within this window are refreshed before use.</summary>
    public static readonly TimeSpan RefreshSkew = TimeSpan.FromSeconds(90);

    public async Task<GitCloneAccess> ForApplicationAsync(AppEntity application, TeamId teamId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(application);
        GitSourceConnection? source = null;
        if (application.SourceId is { } sourceId)
        {
            source = dbContext.GitSourceConnections.SingleOrDefault(candidate => candidate.Id == sourceId)
                     ?? throw new DomainException("The application's Git source no longer exists.");
            if (!IsUsableBy(source, teamId))
                throw new DomainException("The application's Git source is not available to this team.");
        }

        return await ForRepositoryAsync(source, application.GitSource.RepositoryUrl, teamId, cancellationToken);
    }

    public async Task<GitCloneAccess> ForRepositoryAsync(GitSourceConnection? source, RepositoryUrl repository,
        TeamId teamId, CancellationToken cancellationToken = default)
    {
        if (source is not null)
        {
            if (!IsUsableBy(source, teamId))
                throw new DomainException("The Git source is not available to this team.");
            var refusal = source.CheckRepository(repository);
            if (refusal is not null) throw new DomainException(refusal);
        }

        Uri uri = TransportUri(repository, source);
        GitHttpsCredential? https = null;
        GitSshCredential? ssh = null;
        switch (source?.Kind)
        {
            case GitSourceKind.GitHubApp:
                https = new GitHttpsCredential(Authority(repository), "x-access-token",
                    await GitHubInstallationTokenAsync(source, cancellationToken));
                break;
            case GitSourceKind.GitLabApp or GitSourceKind.GiteaApp:
                https = new GitHttpsCredential(Authority(repository), "oauth2",
                    await OAuthAccessTokenAsync(source, cancellationToken));
                break;
            case GitSourceKind.HttpsCredential:
                https = new GitHttpsCredential(Authority(repository), source.Username!,
                    await RevealAsync(source, source.AccessTokenReferenceId, "HTTPS token", cancellationToken));
                break;
            case GitSourceKind.DeployKey:
                ssh = new GitSshCredential(await DeployKeyAsync(source, cancellationToken));
                break;
        }

        GitSshHostTrust? trust = repository.IsSsh
            ? HostTrust(teamId, uri.Host.Trim('[', ']'), uri.IsDefaultPort || uri.Port < 0 ? 22 : uri.Port)
            : null;
        var authentication = https is null && ssh is null && trust is null
            ? GitAuthentication.None
            : new GitAuthentication(https, ssh, trust, source?.Name.Value);
        return new GitCloneAccess(uri, authentication, source?.Name.Value);
    }

    public async Task<string> ApiTokenAsync(GitSourceConnection source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Kind switch
        {
            GitSourceKind.GitHubApp => await GitHubInstallationTokenAsync(source, cancellationToken),
            GitSourceKind.GitLabApp or GitSourceKind.GiteaApp =>
                await OAuthAccessTokenAsync(source, cancellationToken),
            _ => throw new DomainException("Repository listing needs a GitHub, GitLab or Gitea app source.")
        };
    }

    public static bool IsUsableBy(GitSourceConnection source, TeamId teamId)
    {
        return source.TeamId == teamId || (source.IsSystemWide && source.IsAppKind);
    }

    /// <summary>
    ///     The URL git connects to. SCP-like SSH URLs become <c>ssh://</c> URLs; a deploy key or OAuth source with a
    ///     custom SSH user or port (upstream <c>custom_user</c>/<c>custom_port</c>) applies them when the repository URL
    ///     does not name a port itself.
    /// </summary>
    public static Uri TransportUri(RepositoryUrl repository, GitSourceConnection? source)
    {
        Uri uri = repository.ToTransportUri();
        if (!repository.IsSsh || source is null || (source.CustomSshPort is null && source.CustomSshUser is null))
            return uri;
        var builder = new UriBuilder(uri);
        if (source.CustomSshUser is not null) builder.UserName = source.CustomSshUser;
        if (repository.Port is null && source.CustomSshPort is { } port) builder.Port = port;
        return builder.Uri;
    }

    private GitSshHostTrust HostTrust(TeamId teamId, string host, int port)
    {
        var normalizedHost = host.ToLowerInvariant();
        IReadOnlyList<string> published = GitHostKeys.PublishedFor(normalizedHost, port);
        if (published.Count > 0) return new GitSshHostTrust(normalizedHost, port, published, false);

        var pinned = dbContext.GitKnownHosts
            .Where(entry => entry.TeamId == teamId && entry.Host == normalizedHost && entry.Port == port)
            .AsEnumerable()
            .Select(entry => entry.KnownHostsLine)
            .ToArray();
        if (pinned.Length > 0) return new GitSshHostTrust(normalizedHost, port, pinned, false);

        return new GitSshHostTrust(normalizedHost, port, [], true,
            (entries, token) => PinFirstUseAsync(teamId, normalizedHost, port, entries, token));
    }

    private async Task PinFirstUseAsync(TeamId teamId, string host, int port, IReadOnlyList<KnownHostsEntry> entries,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        var added = new List<string>();
        foreach (KnownHostsEntry entry in entries)
        {
            if (dbContext.GitKnownHosts.Any(existing => existing.TeamId == teamId && existing.Host == host &&
                                                        existing.Port == port && existing.KeyType == entry.KeyType))
                continue;
            await dbContext.GitKnownHostRepository.AddAsync(
                GitKnownHost.Create(teamId, host, port, entry.KeyType, entry.Key, GitKnownHostOrigin.FirstUse, now),
                cancellationToken);
            added.Add(entry.KeyType);
        }

        if (added.Count == 0) return;
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(teamId, null, GitSourceAuditActions.HostKeyPinned,
            new AuditTarget("git-host", GitHostKeys.HostPattern(host, port)), null,
            new Dictionary<string, object?> { ["keyTypes"] = added.ToArray(), ["origin"] = "first-use" },
            cancellationToken);
    }

    private async Task<string> GitHubInstallationTokenAsync(GitSourceConnection source,
        CancellationToken cancellationToken)
    {
        if (source.AppId is not { } appId || source.InstallationId is not { } installationId)
            throw new DomainException(
                "The GitHub App has no installation id. Install the app on the account and set its installation id.");
        var privateKey = await RevealAsync(source, source.PrivateKeyReferenceId, "GitHub App private key",
            cancellationToken);
        GitProviderToken token = await providerApi.CreateGitHubInstallationTokenAsync(
            source.ApiUrl ?? GitProviderEndpoints.GitHubApiUrl(source.HtmlUrl!), appId, installationId, privateKey,
            cancellationToken);
        return token.Token;
    }

    private async Task<string> OAuthAccessTokenAsync(GitSourceConnection source, CancellationToken cancellationToken)
    {
        if (source.AccessTokenReferenceId is null)
            throw new DomainException("The Git source is not connected yet: add its OAuth access token.");
        if (!NeedsRefresh(source))
            return await RevealAsync(source, source.AccessTokenReferenceId, "access token", cancellationToken);

        // Refresh tokens are single use on GitLab: serialize refreshes of one source across workers.
        await using DistributedLockHandle? handle = await locks.TryAcquireAsync(
            $"git-source-token:{source.Id.Value:D}", TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(30),
            cancellationToken);
        if (handle is null) throw new DomainException("Another worker is refreshing the Git source token.");
        await dbContext.RefreshAsync(source, cancellationToken);
        if (!NeedsRefresh(source))
            return await RevealAsync(source, source.AccessTokenReferenceId, "access token", cancellationToken);

        if (source.RefreshTokenReferenceId is null || source.ClientSecretReferenceId is null ||
            string.IsNullOrWhiteSpace(source.ClientId))
            throw new DomainException(
                "The Git source access token expired and cannot be refreshed: reconnect the source with a new token.");
        var refreshToken = await RevealAsync(source, source.RefreshTokenReferenceId, "refresh token",
            cancellationToken);
        var clientSecret = await RevealAsync(source, source.ClientSecretReferenceId, "client secret",
            cancellationToken);
        GitOAuthTokens tokens = await providerApi.RefreshOAuthTokenAsync(source.Kind, source.HtmlUrl!,
            source.ClientId, clientSecret, refreshToken, cancellationToken);
        await secretVault.ReplaceAsync(source.AccessTokenReferenceId.Value, tokens.AccessToken, cancellationToken);
        if (!string.IsNullOrEmpty(tokens.RefreshToken))
            await secretVault.ReplaceAsync(source.RefreshTokenReferenceId.Value, tokens.RefreshToken,
                cancellationToken);
        source.RecordTokenRefresh(tokens.ExpiresAt, timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(source.TeamId, null, GitSourceAuditActions.TokenRefreshed,
            new AuditTarget("git-source", source.Id.Value.ToString("D")), null,
            new Dictionary<string, object?> { ["kind"] = source.Kind.ToString() }, cancellationToken);
        return tokens.AccessToken;
    }

    private bool NeedsRefresh(GitSourceConnection source)
    {
        return source.AccessTokenExpiresAt is { } expiresAt && expiresAt - RefreshSkew <= timeProvider.GetUtcNow();
    }

    private async Task<string> DeployKeyAsync(GitSourceConnection source, CancellationToken cancellationToken)
    {
        SshKeyId keyId = source.SshKeyId ?? throw new DomainException("The deploy key source has no SSH key.");
        // Upstream v4.4.0: a source may only use private keys of its own team.
        SshKey key = dbContext.SshKeys.SingleOrDefault(candidate =>
                         candidate.Id == keyId && candidate.TeamId == source.TeamId)
                     ?? throw new DomainException("The deploy key of the Git source no longer exists.");
        if (!key.IsGitRelated)
            throw new DomainException("The SSH key of the deploy key source is not marked as a Git deploy key.");
        return await secretVault.RevealForDeploymentAsync(key.TeamId, key.PrivateKeyReferenceId, cancellationToken);
    }

    private async Task<string> RevealAsync(GitSourceConnection source, SecretReferenceId? reference, string what,
        CancellationToken cancellationToken)
    {
        if (reference is null) throw new DomainException($"The Git source has no {what}.");
        return await secretVault.RevealForDeploymentAsync(source.TeamId, reference.Value, cancellationToken);
    }

    private static string Authority(RepositoryUrl repository)
    {
        var host = repository.Host.Contains(':', StringComparison.Ordinal) ? $"[{repository.Host}]" : repository.Host;
        return repository.Port is { } port ? $"{host}:{port}" : host;
    }
}

public static class GitSourceAuditActions
{
    public const string Created = "git_source.created";
    public const string Updated = "git_source.updated";
    public const string Deleted = "git_source.deleted";
    public const string AssignedToApplication = "git_source.assigned";
    public const string TokenRefreshed = "git_source.token_refreshed";
    public const string HostKeyPinned = "git_source.host_key_pinned";
    public const string HostKeyRemoved = "git_source.host_key_removed";
}
