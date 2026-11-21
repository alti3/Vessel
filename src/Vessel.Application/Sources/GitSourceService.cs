using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Git;
using Vessel.Application.Persistence;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Common;
using Vessel.Domain.Secrets;
using Vessel.Domain.Sources;
using Vessel.Domain.Ssh;
using Vessel.Domain.ValueObjects;
using AppEntity = Vessel.Domain.Applications.Application;
using ApplicationId = Vessel.Domain.ApplicationId;

namespace Vessel.Application.Sources;

/// <summary>
///     Git source management (DEP-08). Team sources need team-wide <c>applications.write</c>; system-wide sources
///     (shared with every team, upstream v4.5.0 #12164) need an instance administrator. Secrets are written to the
///     vault and never returned. Repository and branch listing use short-lived provider tokens.
/// </summary>
public sealed partial class GitSourceService(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    ISecretVault secretVault,
    IGitCredentialProvider credentials,
    IGitProviderApi providerApi,
    IGitClient git,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
{
    public IReadOnlyList<GitSourceSummary> List(UserId actorUserId, TeamId teamId)
    {
        RequireRead(actorUserId, teamId);
        return dbContext.GitSourceConnections
            .Where(source => source.TeamId == teamId || source.IsSystemWide)
            .AsEnumerable()
            .Where(source => GitCredentialResolver.IsUsableBy(source, teamId))
            .OrderBy(source => source.Name.Value, StringComparer.OrdinalIgnoreCase)
            .Select(source => ToSummary(source, teamId))
            .ToArray();
    }

    public GitSourceSummary Get(UserId actorUserId, TeamId teamId, GitSourceConnectionId sourceId)
    {
        RequireRead(actorUserId, teamId);
        return ToSummary(LoadVisible(teamId, sourceId), teamId);
    }

    public async Task<GitSourceSummary> CreateAsync(UserId actorUserId, TeamId teamId,
        CreateGitSourceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireWrite(actorUserId, teamId, request.IsSystemWide);
        var name = new ResourceName(request.Name);
        EnsureUniqueName(teamId, name.Value, null);
        DateTimeOffset now = timeProvider.GetUtcNow();
        var htmlUrl = ResolveHtmlUrl(request.Kind, request.HtmlUrl, request.Preset);

        GitSourceConnection source;
        switch (request.Kind)
        {
            case GitSourceKind.GitHubApp:
            {
                if (request.AppId is not { } appId) throw new DomainException("GitHub App id is required.");
                var privateKey = ValidateGitHubPrivateKey(request.PrivateKey);
                SecretReference key = await StoreAsync(teamId, "github-app-private-key", privateKey,
                    cancellationToken);
                SecretReference? webhook = await StoreOptionalAsync(teamId, "webhook-secret", request.WebhookSecret,
                    cancellationToken);
                source = GitSourceConnection.CreateGitHubApp(teamId, name, htmlUrl!, request.ApiUrl, appId,
                    request.InstallationId, request.ClientId, key.Id, webhook?.Id, request.Organization,
                    request.IsSystemWide, now);
                break;
            }
            case GitSourceKind.GitLabApp or GitSourceKind.GiteaApp:
            {
                SecretReference? clientSecret = await StoreOptionalAsync(teamId, "oauth-client-secret",
                    request.ClientSecret, cancellationToken);
                SecretReference? webhook = await StoreOptionalAsync(teamId, "webhook-secret", request.WebhookSecret,
                    cancellationToken);
                source = GitSourceConnection.CreateOAuthApp(request.Kind, teamId, name, htmlUrl!, request.ApiUrl,
                    request.ClientId, clientSecret?.Id, webhook?.Id, request.Organization, request.CustomSshUser,
                    request.CustomSshPort, request.IsSystemWide, now);
                if (!string.IsNullOrWhiteSpace(request.AccessToken))
                {
                    SecretReference access = await StoreAsync(teamId, "oauth-access-token", request.AccessToken,
                        cancellationToken);
                    SecretReference? refresh = await StoreOptionalAsync(teamId, "oauth-refresh-token",
                        request.RefreshToken, cancellationToken);
                    source.AttachAccessToken(access.Id, refresh?.Id, request.AccessTokenExpiresAt, now);
                }

                break;
            }
            case GitSourceKind.DeployKey:
            {
                if (request.IsSystemWide) throw new DomainException("Deploy key sources cannot be system-wide.");
                SshKeyId keyId = RequireDeployKey(teamId, request.SshKeyId);
                source = GitSourceConnection.CreateDeployKey(teamId, name, keyId, request.HtmlUrl,
                    request.CustomSshUser, request.CustomSshPort, now);
                break;
            }
            case GitSourceKind.HttpsCredential:
            {
                if (request.IsSystemWide) throw new DomainException("HTTPS credential sources cannot be system-wide.");
                if (string.IsNullOrWhiteSpace(request.Username)) throw new DomainException("Username is required.");
                SecretReference token = await StoreAsync(teamId, "https-token",
                    RequireSecret(request.Password, "Password or access token"), cancellationToken);
                source = GitSourceConnection.CreateHttpsCredential(teamId, name, htmlUrl!, request.Username, token.Id,
                    now);
                break;
            }
            default:
                throw new DomainException("Unsupported Git source kind.");
        }

        await dbContext.GitSourceConnectionRepository.AddAsync(source, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await AuditAsync(teamId, actorUserId, GitSourceAuditActions.Created, source, cancellationToken);
        return ToSummary(source, teamId);
    }

    public async Task<GitSourceSummary> UpdateAsync(UserId actorUserId, TeamId teamId,
        GitSourceConnectionId sourceId, UpdateGitSourceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        GitSourceConnection source = LoadOwned(teamId, sourceId);
        RequireWrite(actorUserId, teamId, source.IsSystemWide || request.IsSystemWide == true);
        DateTimeOffset now = timeProvider.GetUtcNow();

        var name = request.Name is null ? source.Name : new ResourceName(request.Name);
        EnsureUniqueName(teamId, name.Value, source.Id);
        source.UpdateDetails(name, Keep(request.Organization, source.Organization),
            Keep(request.CustomSshUser, source.CustomSshUser), request.CustomSshPort ?? source.CustomSshPort, now);
        if (request.HtmlUrl is not null || request.ApiUrl is not null)
            source.UpdateEndpoints(request.HtmlUrl ?? source.HtmlUrl ?? string.Empty,
                request.ApiUrl ?? (request.HtmlUrl is null ? source.ApiUrl : null), now);
        if (request.IsSystemWide is { } systemWide) source.SetSystemWide(systemWide, now);

        switch (source.Kind)
        {
            case GitSourceKind.GitHubApp:
                if (request.AppId is not null || request.InstallationId is not null || request.ClientId is not null)
                    source.UpdateGitHubApp(request.AppId ?? source.AppId!.Value,
                        request.InstallationId ?? source.InstallationId, Keep(request.ClientId, source.ClientId), now);
                if (!string.IsNullOrWhiteSpace(request.PrivateKey))
                    await ReplaceOrAttachAsync(source, source.PrivateKeyReferenceId, "github-app-private-key",
                        ValidateGitHubPrivateKey(request.PrivateKey), reference => source.AttachPrivateKey(reference, now),
                        cancellationToken);
                break;
            case GitSourceKind.GitLabApp or GitSourceKind.GiteaApp:
                if (request.ClientId is not null) source.UpdateOAuthClient(Keep(request.ClientId, null), now);
                if (!string.IsNullOrWhiteSpace(request.ClientSecret))
                    await ReplaceOrAttachAsync(source, source.ClientSecretReferenceId, "oauth-client-secret",
                        request.ClientSecret, reference => source.AttachClientSecret(reference, now),
                        cancellationToken);
                if (!string.IsNullOrWhiteSpace(request.AccessToken))
                {
                    SecretReferenceId access = await ReplaceOrStoreAsync(source, source.AccessTokenReferenceId,
                        "oauth-access-token", request.AccessToken, cancellationToken);
                    SecretReferenceId? refresh = string.IsNullOrWhiteSpace(request.RefreshToken)
                        ? null
                        : await ReplaceOrStoreAsync(source, source.RefreshTokenReferenceId, "oauth-refresh-token",
                            request.RefreshToken, cancellationToken);
                    source.AttachAccessToken(access, refresh, request.AccessTokenExpiresAt, now);
                }

                break;
            case GitSourceKind.DeployKey:
                if (request.SshKeyId is not null)
                    source.ChangeDeployKey(RequireDeployKey(teamId, request.SshKeyId), now);
                break;
            case GitSourceKind.HttpsCredential:
                if (!string.IsNullOrWhiteSpace(request.Username)) source.UpdateUsername(request.Username, now);
                if (!string.IsNullOrWhiteSpace(request.Password))
                    source.AttachAccessToken(
                        await ReplaceOrStoreAsync(source, source.AccessTokenReferenceId, "https-token",
                            request.Password, cancellationToken), null, null, now);
                break;
        }

        if (!string.IsNullOrWhiteSpace(request.WebhookSecret))
            await ReplaceOrAttachAsync(source, source.WebhookSecretReferenceId, "webhook-secret",
                request.WebhookSecret, reference => source.AttachWebhookSecret(reference, now), cancellationToken);

        EnsureApplicationsStillAllowed(source);
        await dbContext.SaveChangesAsync(cancellationToken);
        await AuditAsync(teamId, actorUserId, GitSourceAuditActions.Updated, source, cancellationToken);
        return ToSummary(source, teamId);
    }

    public async Task DeleteAsync(UserId actorUserId, TeamId teamId, GitSourceConnectionId sourceId,
        CancellationToken cancellationToken = default)
    {
        GitSourceConnection source = LoadOwned(teamId, sourceId);
        RequireWrite(actorUserId, teamId, source.IsSystemWide);
        var used = dbContext.Applications.Count(application => application.SourceId == source.Id);
        if (used > 0)
            throw new GitSourceInUseException(
                $"The Git source is used by {used} application(s). Choose another source for them first.");

        dbContext.GitSourceConnectionRepository.Remove(source);
        foreach (SecretReferenceId referenceId in source.SecretReferences().ToArray())
        {
            foreach (SecretValue value in dbContext.SecretValues
                         .Where(value => value.SecretReferenceId == referenceId).ToArray())
                dbContext.SecretValueRepository.Remove(value);
            SecretReference? reference =
                dbContext.SecretReferences.SingleOrDefault(candidate => candidate.Id == referenceId);
            if (reference is not null) dbContext.SecretReferenceRepository.Remove(reference);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await AuditAsync(teamId, actorUserId, GitSourceAuditActions.Deleted, source, cancellationToken);
    }

    /// <summary>Repositories the app source can access (GitHub installation, GitLab membership, Gitea user repos).</summary>
    public async Task<IReadOnlyList<GitSourceRepositorySummary>> ListRepositoriesAsync(UserId actorUserId,
        TeamId teamId, GitSourceConnectionId sourceId, int page = 1, CancellationToken cancellationToken = default)
    {
        RequireRead(actorUserId, teamId);
        GitSourceConnection source = LoadVisible(teamId, sourceId);
        if (!source.IsAppKind)
            throw new DomainException("Repository listing is available for GitHub, GitLab and Gitea app sources.");
        var token = await credentials.ApiTokenAsync(source, cancellationToken);
        IReadOnlyList<GitProviderRepository> repositories = await providerApi.ListRepositoriesAsync(source.Kind,
            source.ApiUrl!, token, Math.Clamp(page, 1, 1000), cancellationToken);
        return repositories
            .Where(repository => source.AllowsOwner(repository.FullName.Split('/', 2)[0]))
            .Where(repository => RepositoryUrl.TryParse(repository.CloneUrl, out _, out _))
            .Select(repository => new GitSourceRepositorySummary(repository.FullName, repository.CloneUrl,
                repository.HtmlUrl, ValidBranchOrNull(repository.DefaultBranch), repository.IsPrivate))
            .ToArray();
    }

    /// <summary>
    ///     Branches of a repository: <paramref name="repository" /> is <c>owner/name</c> (app sources) or a repository
    ///     URL. App sources use the provider API; deploy keys and HTTPS credentials use <c>git ls-remote</c> with the
    ///     source credentials. Branch names that are not valid git refs are dropped (SEC-10).
    /// </summary>
    public async Task<IReadOnlyList<GitSourceBranchSummary>> ListBranchesAsync(UserId actorUserId, TeamId teamId,
        GitSourceConnectionId sourceId, string repository, CancellationToken cancellationToken = default)
    {
        RequireRead(actorUserId, teamId);
        GitSourceConnection source = LoadVisible(teamId, sourceId);
        if (string.IsNullOrWhiteSpace(repository)) throw new DomainException("Repository is required.");
        var value = repository.Trim();
        var isUrl = value.Contains("://", StringComparison.Ordinal) || value.Contains('@', StringComparison.Ordinal);

        if (source.IsAppKind)
        {
            string path;
            if (isUrl)
            {
                RepositoryUrl url = RepositoryUrl.ParseInput(value);
                var refusal = source.CheckRepository(url);
                if (refusal is not null) throw new DomainException(refusal);
                path = source.RepositoryPath(url);
            }
            else
            {
                if (!RepositoryPathPattern().IsMatch(value) || value.Split('/').Any(part => part is "." or ".."))
                    throw new DomainException("Repository must be owner/name.");
                if (!source.AllowsOwner(value.Split('/', 2)[0]))
                    throw new DomainException("Repository owner is not allowed by this source.");
                path = value;
            }

            var token = await credentials.ApiTokenAsync(source, cancellationToken);
            IReadOnlyList<GitProviderBranch> branches = await providerApi.ListBranchesAsync(source.Kind,
                source.ApiUrl!, token, path, cancellationToken);
            return branches.Where(branch => GitRef.TryParse(branch.Name, out _))
                .Select(branch => new GitSourceBranchSummary(branch.Name, branch.CommitSha))
                .ToArray();
        }

        if (!isUrl) throw new DomainException("Use the repository URL for deploy key and HTTPS sources.");
        RepositoryUrl repositoryUrl = RepositoryUrl.ParseInput(value);
        GitCloneAccess access = await credentials.ForRepositoryAsync(source, repositoryUrl, teamId,
            cancellationToken);
        IReadOnlyList<GitRepositoryRef> refs = await git.ListRefsAsync(access.RepositoryUri, access.Authentication,
            cancellationToken);
        return refs.Where(item => !item.IsTag && GitRef.TryParse(item.Name, out _))
            .Select(item => new GitSourceBranchSummary(item.Name, item.Sha))
            .ToArray();
    }

    /// <summary>Selects (or clears) the Git source an application clones with.</summary>
    public async Task<GitSourceSummary?> AssignToApplicationAsync(UserId actorUserId, TeamId teamId,
        ApplicationId applicationId, AssignGitSourceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        authorization.Demand(actorUserId, teamId, ResourceRef.Application(applicationId),
            VesselPermissions.ApplicationsWrite);
        AppEntity application = dbContext.Applications.Single(candidate => candidate.Id == applicationId);
        GitSourceConnection? source = GitSourceSelection.Resolve(dbContext, teamId, request.SourceId,
            application.GitSource.RepositoryUrl);
        application.AssignSource(source?.Id, timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(teamId, actorUserId, GitSourceAuditActions.AssignedToApplication,
            new AuditTarget("application", applicationId.Value.ToString("D")), null,
            new Dictionary<string, object?> { ["sourceId"] = source?.Id.Value }, cancellationToken);
        return source is null ? null : ToSummary(source, teamId);
    }

    /// <summary>
    ///     SSH host keys of Git hosts as seen by this team: the provider-published keys (read-only) and the team's
    ///     pinned keys (first use or operator).
    /// </summary>
    public IReadOnlyList<GitKnownHostSummary> ListKnownHosts(UserId actorUserId, TeamId teamId)
    {
        RequireRead(actorUserId, teamId);
        var published = GitHostKeys.Published
            .Select(line => GitHostKeys.TryParse(line, out KnownHostsEntry entry) ? entry : null)
            .OfType<KnownHostsEntry>()
            .Select(entry => new GitKnownHostSummary(null, entry.Hosts[0], GitHostKeys.DefaultSshPort,
                entry.KeyType, entry.Key, "Published", null));
        var pinned = dbContext.GitKnownHosts.Where(host => host.TeamId == teamId)
            .AsEnumerable()
            .OrderBy(host => host.Host, StringComparer.Ordinal).ThenBy(host => host.Port)
            .Select(host => new GitKnownHostSummary(host.Id.Value, host.Host, host.Port, host.KeyType, host.Key,
                host.Origin.ToString(), host.CreatedAt));
        return published.Concat(pinned).ToArray();
    }

    /// <summary>
    ///     Operator override: pins a host key for the team. A pinned key of the same type is replaced, so a rotated host
    ///     key can be accepted after it was verified out of band.
    /// </summary>
    public async Task<GitKnownHostSummary> PinKnownHostAsync(UserId actorUserId, TeamId teamId,
        AddGitKnownHostRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireWrite(actorUserId, teamId, false);
        var host = (request.Host ?? string.Empty).Trim().Trim('[', ']').ToLowerInvariant();
        if (GitHostKeys.HasPublishedKeys(host, request.Port))
            throw new DomainException("This host's keys are published by the provider and cannot be pinned.");
        DateTimeOffset now = timeProvider.GetUtcNow();
        GitKnownHost entry = GitKnownHost.Create(teamId, host, request.Port, request.KeyType?.Trim() ?? string.Empty,
            request.Key?.Trim() ?? string.Empty, GitKnownHostOrigin.Operator, now);
        foreach (GitKnownHost existing in dbContext.GitKnownHosts.Where(candidate =>
                     candidate.TeamId == teamId && candidate.Host == entry.Host && candidate.Port == entry.Port &&
                     candidate.KeyType == entry.KeyType).ToArray())
            dbContext.GitKnownHostRepository.Remove(existing);
        await dbContext.GitKnownHostRepository.AddAsync(entry, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(teamId, actorUserId, GitSourceAuditActions.HostKeyPinned,
            new AuditTarget("git-host", GitHostKeys.HostPattern(entry.Host, entry.Port)), null,
            new Dictionary<string, object?> { ["keyType"] = entry.KeyType, ["origin"] = "operator" },
            cancellationToken);
        return new GitKnownHostSummary(entry.Id.Value, entry.Host, entry.Port, entry.KeyType, entry.Key,
            entry.Origin.ToString(), entry.CreatedAt);
    }

    /// <summary>Removes a pinned key; the next connection to the host pins its key again on first use.</summary>
    public async Task RemoveKnownHostAsync(UserId actorUserId, TeamId teamId, GitKnownHostId hostId,
        CancellationToken cancellationToken = default)
    {
        RequireWrite(actorUserId, teamId, false);
        GitKnownHost entry = dbContext.GitKnownHosts.SingleOrDefault(candidate =>
                                 candidate.Id == hostId && candidate.TeamId == teamId)
                             ?? throw new UnauthorizedAccessException("Known host is outside the active team.");
        dbContext.GitKnownHostRepository.Remove(entry);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(teamId, actorUserId, GitSourceAuditActions.HostKeyRemoved,
            new AuditTarget("git-host", GitHostKeys.HostPattern(entry.Host, entry.Port)), null,
            new Dictionary<string, object?> { ["keyType"] = entry.KeyType }, cancellationToken);
    }

    private void RequireRead(UserId actorUserId, TeamId teamId)
    {
        if (!authorization.HasPermissionSomewhere(actorUserId, teamId, VesselPermissions.ApplicationsRead))
            throw new UnauthorizedAccessException(
                $"Missing required permission '{VesselPermissions.ApplicationsRead}'.");
    }

    /// <summary>Team sources need team-wide write; system-wide sources need an instance administrator.</summary>
    private void RequireWrite(UserId actorUserId, TeamId teamId, bool systemWide)
    {
        if (!authorization.HasPermission(actorUserId, teamId, VesselPermissions.ApplicationsWrite))
            throw new UnauthorizedAccessException(
                $"Missing required permission '{VesselPermissions.ApplicationsWrite}' on the whole team.");
        if (systemWide && !authorization.IsInstanceManager(actorUserId))
            throw new UnauthorizedAccessException("System-wide Git sources can only be managed by instance administrators.");
    }

    private GitSourceConnection LoadVisible(TeamId teamId, GitSourceConnectionId sourceId)
    {
        GitSourceConnection? source =
            dbContext.GitSourceConnections.SingleOrDefault(candidate => candidate.Id == sourceId);
        if (source is null || !GitCredentialResolver.IsUsableBy(source, teamId))
            throw new UnauthorizedAccessException("Git source is outside the active team.");
        return source;
    }

    private GitSourceConnection LoadOwned(TeamId teamId, GitSourceConnectionId sourceId)
    {
        return dbContext.GitSourceConnections.SingleOrDefault(candidate =>
                   candidate.Id == sourceId && candidate.TeamId == teamId)
               ?? throw new UnauthorizedAccessException("Git source is outside the active team.");
    }

    private void EnsureUniqueName(TeamId teamId, string name, GitSourceConnectionId? except)
    {
        if (dbContext.GitSourceConnections.AsEnumerable().Any(source =>
                source.TeamId == teamId && source.Id != except &&
                string.Equals(source.Name.Value, name, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException("A Git source with this name already exists in the team.");
    }

    private SshKeyId RequireDeployKey(TeamId teamId, Guid? sshKeyId)
    {
        if (sshKeyId is null) throw new DomainException("SSH key is required for a deploy key source.");
        var id = new SshKeyId(sshKeyId.Value);
        SshKey key = dbContext.SshKeys.SingleOrDefault(candidate => candidate.Id == id && candidate.TeamId == teamId)
                     ?? throw new UnauthorizedAccessException("SSH key is outside the active team.");
        if (!key.IsGitRelated)
            throw new DomainException("Mark the SSH key as a Git deploy key before using it in a deploy key source.");
        return key.Id;
    }

    /// <summary>A changed host, organization or kind setting must not silently re-target applications.</summary>
    private void EnsureApplicationsStillAllowed(GitSourceConnection source)
    {
        foreach (AppEntity application in dbContext.Applications.Where(app => app.SourceId == source.Id).ToArray())
        {
            var refusal = source.CheckRepository(application.GitSource.RepositoryUrl);
            if (refusal is not null)
                throw new DomainException(
                    $"Application '{application.Name.Value}' would no longer be allowed by this source: {refusal}");
        }
    }

    private static string? ResolveHtmlUrl(GitSourceKind kind, string? htmlUrl, string? preset)
    {
        if (!string.IsNullOrWhiteSpace(htmlUrl)) return GitProviderEndpoints.NormalizeHtmlUrl(htmlUrl);
        if (!string.IsNullOrWhiteSpace(preset))
            return GitProviderEndpoints.Presets.TryGetValue(preset.Trim(), out var url)
                ? url
                : throw new DomainException("Unknown provider preset.");
        return kind switch
        {
            GitSourceKind.GitHubApp => GitProviderEndpoints.Presets["github"],
            GitSourceKind.GitLabApp => GitProviderEndpoints.Presets["gitlab"],
            GitSourceKind.GiteaApp => GitProviderEndpoints.Presets["gitea"],
            GitSourceKind.DeployKey => null,
            _ => throw new DomainException("Provider URL is required.")
        };
    }

    /// <summary>GitHub App private keys are RSA keys in PEM form.</summary>
    public static string ValidateGitHubPrivateKey(string? pem)
    {
        var value = RequireSecret(pem, "GitHub App private key").Replace("\r\n", "\n", StringComparison.Ordinal)
            .Trim();
        if (value.Length > 16 * 1024 || !value.Contains("PRIVATE KEY-----", StringComparison.Ordinal))
            throw new DomainException("GitHub App private key must be the PEM file downloaded from GitHub.");
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(value);
            if (rsa.KeySize < 2048) throw new DomainException("GitHub App private key must be at least 2048 bits.");
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException)
        {
            throw new DomainException("GitHub App private key must be an unencrypted RSA private key in PEM form.");
        }

        return value + "\n";
    }

    private static string RequireSecret(string? value, string what)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new DomainException($"{what} is required.");
        if (value.Length > 64 * 1024) throw new DomainException($"{what} is too long.");
        return value.Trim();
    }

    private static string? Keep(string? requested, string? current)
    {
        return requested is null ? current : string.IsNullOrWhiteSpace(requested) ? null : requested.Trim();
    }

    private static string? ValidBranchOrNull(string? branch)
    {
        return GitRef.TryParse(branch, out GitRef value) ? value.Value : null;
    }

    private Task<SecretReference> StoreAsync(TeamId teamId, string purpose, string plaintext,
        CancellationToken cancellationToken)
    {
        return secretVault.StoreAsync(teamId, SecretScope.Team, $"git-source:{Guid.NewGuid():N}:{purpose}",
            RequireSecret(plaintext, purpose), new SecretPolicy(false, false, false), new SecretTarget(),
            cancellationToken);
    }

    private async Task<SecretReference?> StoreOptionalAsync(TeamId teamId, string purpose, string? plaintext,
        CancellationToken cancellationToken)
    {
        return string.IsNullOrWhiteSpace(plaintext)
            ? null
            : await StoreAsync(teamId, purpose, plaintext, cancellationToken);
    }

    private async Task<SecretReferenceId> ReplaceOrStoreAsync(GitSourceConnection source,
        SecretReferenceId? existing, string purpose, string plaintext, CancellationToken cancellationToken)
    {
        if (existing is { } reference)
        {
            await secretVault.ReplaceAsync(reference, RequireSecret(plaintext, purpose), cancellationToken);
            return reference;
        }

        return (await StoreAsync(source.TeamId, purpose, plaintext, cancellationToken)).Id;
    }

    private async Task ReplaceOrAttachAsync(GitSourceConnection source, SecretReferenceId? existing, string purpose,
        string plaintext, Action<SecretReferenceId> attach, CancellationToken cancellationToken)
    {
        SecretReferenceId reference = await ReplaceOrStoreAsync(source, existing, purpose, plaintext,
            cancellationToken);
        if (existing is null) attach(reference);
    }

    private int ApplicationCount(GitSourceConnectionId sourceId, TeamId teamId)
    {
        return (from application in dbContext.Applications
            join environment in dbContext.Environments on application.EnvironmentId equals environment.Id
            join project in dbContext.Projects on environment.ProjectId equals project.Id
            where application.SourceId == sourceId && project.TeamId == teamId
            select application.Id).Count();
    }

    private GitSourceSummary ToSummary(GitSourceConnection source, TeamId teamId)
    {
        var webhookPath = source.Kind switch
        {
            GitSourceKind.GitHubApp => "/webhooks/github",
            GitSourceKind.GitLabApp => "/webhooks/gitlab",
            GitSourceKind.GiteaApp => "/webhooks/gitea",
            _ => null
        };
        return new GitSourceSummary(source.Id.Value, source.Name.Value, source.Kind, source.IsSystemWide,
            source.TeamId == teamId, source.HtmlUrl, source.ApiUrl, source.Organization, source.CustomSshUser,
            source.CustomSshPort, source.AppId, source.InstallationId, source.ClientId, source.Username,
            source.SshKeyId?.Value, source.PrivateKeyReferenceId.HasValue, source.ClientSecretReferenceId.HasValue,
            source.WebhookSecretReferenceId.HasValue,
            source.Kind switch
            {
                GitSourceKind.GitHubApp => source.PrivateKeyReferenceId.HasValue && source.InstallationId.HasValue,
                GitSourceKind.DeployKey => source.SshKeyId.HasValue,
                _ => source.AccessTokenReferenceId.HasValue
            },
            source.AccessTokenExpiresAt, ApplicationCount(source.Id, teamId), webhookPath, source.CreatedAt,
            source.UpdatedAt);
    }

    private Task AuditAsync(TeamId teamId, UserId actorUserId, string action, GitSourceConnection source,
        CancellationToken cancellationToken)
    {
        return auditWriter.RecordAsync(teamId, actorUserId, action,
            new AuditTarget("git-source", source.Id.Value.ToString("D")), null,
            new Dictionary<string, object?>
            {
                ["kind"] = source.Kind.ToString(), ["name"] = source.Name.Value, ["systemWide"] = source.IsSystemWide
            }, cancellationToken);
    }

    [GeneratedRegex("^[A-Za-z0-9_.][A-Za-z0-9_.-]*(/[A-Za-z0-9_.][A-Za-z0-9_.-]*){1,4}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryPathPattern();
}

/// <summary>The source is still used by applications.</summary>
public sealed class GitSourceInUseException(string message) : InvalidOperationException(message);
