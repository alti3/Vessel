using System.Text.RegularExpressions;
using Vessel.Domain.Common;
using Vessel.Domain.ValueObjects;

namespace Vessel.Domain.Sources;

/// <summary>
///     A private Git source (DEP-08): GitHub App, GitLab/Gitea OAuth application, deploy key or HTTPS credentials.
///     Every secret (app private key, client secret, OAuth tokens, HTTPS token, webhook secret) lives in the secret
///     vault and is referenced here by id only; the aggregate never holds secret material.
///     <para>
///         A source is owned by <see cref="TeamId" />. <see cref="IsSystemWide" /> app sources (instance-admin only)
///         are usable by every team; their secrets stay in the owning team's vault scope.
///     </para>
///     <para>
///         Credentials are bound to the provider host: <see cref="CheckRepository" /> refuses repositories on any other
///         host, scheme or path prefix, so a source's token is never sent to a host the source was not created for.
///     </para>
/// </summary>
public sealed partial class GitSourceConnection : Entity<GitSourceConnectionId>
{
    public const int MaxOrganizationLength = 512;
    public const int MaxClientIdLength = 255;
    public const int MaxUsernameLength = 255;

    private GitSourceConnection()
    {
    }

    private GitSourceConnection(GitSourceConnectionId id, TeamId teamId, ResourceName name, GitSourceKind kind,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        TeamId = teamId;
        Name = name;
        Kind = kind;
    }

    public TeamId TeamId { get; private set; }

    public ResourceName Name { get; private set; }

    public GitSourceKind Kind { get; private set; }

    /// <summary>Usable by every team (instance-admin managed). Only app kinds can be system-wide.</summary>
    public bool IsSystemWide { get; private set; }

    /// <summary>Provider web URL (<c>https://github.com</c>, GitHub Enterprise or self-hosted GitLab/Gitea).</summary>
    public string? HtmlUrl { get; private set; }

    /// <summary>Provider REST API URL (derived from <see cref="HtmlUrl" /> unless set explicitly).</summary>
    public string? ApiUrl { get; private set; }

    /// <summary>Comma-separated repository owners the source is limited to; empty allows every owner.</summary>
    public string? Organization { get; private set; }

    public string? CustomSshUser { get; private set; }

    public int? CustomSshPort { get; private set; }

    /// <summary>GitHub App id.</summary>
    public long? AppId { get; private set; }

    /// <summary>GitHub App installation id.</summary>
    public long? InstallationId { get; private set; }

    /// <summary>OAuth client id (GitHub App client id, GitLab application id, Gitea OAuth2 client id).</summary>
    public string? ClientId { get; private set; }

    /// <summary>HTTPS username (HTTPS credential sources).</summary>
    public string? Username { get; private set; }

    /// <summary>Team SSH key used as deploy key.</summary>
    public SshKeyId? SshKeyId { get; private set; }

    /// <summary>GitHub App private key (PEM).</summary>
    public SecretReferenceId? PrivateKeyReferenceId { get; private set; }

    public SecretReferenceId? ClientSecretReferenceId { get; private set; }

    public SecretReferenceId? WebhookSecretReferenceId { get; private set; }

    /// <summary>OAuth access token (GitLab/Gitea) or HTTPS password/token.</summary>
    public SecretReferenceId? AccessTokenReferenceId { get; private set; }

    public SecretReferenceId? RefreshTokenReferenceId { get; private set; }

    public DateTimeOffset? AccessTokenExpiresAt { get; private set; }

    public bool IsAppKind => Kind is GitSourceKind.GitHubApp or GitSourceKind.GitLabApp or GitSourceKind.GiteaApp;

    /// <summary>All vault references the source owns (removed with it).</summary>
    public IEnumerable<SecretReferenceId> SecretReferences()
    {
        foreach (SecretReferenceId? reference in new[]
                 {
                     PrivateKeyReferenceId, ClientSecretReferenceId, WebhookSecretReferenceId, AccessTokenReferenceId,
                     RefreshTokenReferenceId
                 })
            if (reference.HasValue)
                yield return reference.Value;
    }

    public static GitSourceConnection CreateGitHubApp(TeamId teamId, ResourceName name, string htmlUrl,
        string? apiUrl, long appId, long? installationId, string? clientId, SecretReferenceId privateKeyReferenceId,
        SecretReferenceId? webhookSecretReferenceId, string? organization, bool isSystemWide, DateTimeOffset now)
    {
        var source = new GitSourceConnection(GitSourceConnectionId.New(), teamId, name, GitSourceKind.GitHubApp, now);
        source.SetEndpoints(htmlUrl, apiUrl);
        source.SetGitHubApp(appId, installationId, clientId);
        source.PrivateKeyReferenceId = privateKeyReferenceId;
        source.WebhookSecretReferenceId = webhookSecretReferenceId;
        source.Organization = NormalizeOrganization(organization);
        source.IsSystemWide = isSystemWide;
        return source;
    }

    public static GitSourceConnection CreateOAuthApp(GitSourceKind kind, TeamId teamId, ResourceName name,
        string htmlUrl, string? apiUrl, string? clientId, SecretReferenceId? clientSecretReferenceId,
        SecretReferenceId? webhookSecretReferenceId, string? organization, string? customSshUser, int? customSshPort,
        bool isSystemWide, DateTimeOffset now)
    {
        if (kind is not (GitSourceKind.GitLabApp or GitSourceKind.GiteaApp))
            throw new DomainException("OAuth application sources must be GitLab or Gitea sources.");
        var source = new GitSourceConnection(GitSourceConnectionId.New(), teamId, name, kind, now);
        source.SetEndpoints(htmlUrl, apiUrl);
        source.ClientId = DomainValidation.Optional(clientId, nameof(ClientId), MaxClientIdLength);
        source.ClientSecretReferenceId = clientSecretReferenceId;
        source.WebhookSecretReferenceId = webhookSecretReferenceId;
        source.Organization = NormalizeOrganization(organization);
        source.SetSsh(customSshUser, customSshPort);
        source.IsSystemWide = isSystemWide;
        return source;
    }

    public static GitSourceConnection CreateDeployKey(TeamId teamId, ResourceName name, SshKeyId sshKeyId,
        string? htmlUrl, string? customSshUser, int? customSshPort, DateTimeOffset now)
    {
        var source = new GitSourceConnection(GitSourceConnectionId.New(), teamId, name, GitSourceKind.DeployKey, now);
        source.SshKeyId = sshKeyId;
        if (!string.IsNullOrWhiteSpace(htmlUrl)) source.SetEndpoints(htmlUrl, null);
        source.SetSsh(customSshUser, customSshPort);
        return source;
    }

    public static GitSourceConnection CreateHttpsCredential(TeamId teamId, ResourceName name, string htmlUrl,
        string username, SecretReferenceId tokenReferenceId, DateTimeOffset now)
    {
        var source = new GitSourceConnection(GitSourceConnectionId.New(), teamId, name,
            GitSourceKind.HttpsCredential, now);
        source.SetEndpoints(htmlUrl, null);
        source.Username = NormalizeUsername(username);
        source.AccessTokenReferenceId = tokenReferenceId;
        return source;
    }

    public void UpdateDetails(ResourceName name, string? organization, string? customSshUser, int? customSshPort,
        DateTimeOffset now)
    {
        Name = name;
        Organization = NormalizeOrganization(organization);
        SetSsh(customSshUser, customSshPort);
        Touch(now);
    }

    public void UpdateEndpoints(string htmlUrl, string? apiUrl, DateTimeOffset now)
    {
        if (Kind == GitSourceKind.DeployKey && string.IsNullOrWhiteSpace(htmlUrl))
        {
            HtmlUrl = null;
            ApiUrl = null;
        }
        else
        {
            SetEndpoints(htmlUrl, apiUrl);
        }

        Touch(now);
    }

    public void UpdateGitHubApp(long appId, long? installationId, string? clientId, DateTimeOffset now)
    {
        RequireKind(GitSourceKind.GitHubApp);
        SetGitHubApp(appId, installationId, clientId);
        Touch(now);
    }

    public void UpdateOAuthClient(string? clientId, DateTimeOffset now)
    {
        RequireKind(GitSourceKind.GitLabApp, GitSourceKind.GiteaApp);
        ClientId = DomainValidation.Optional(clientId, nameof(ClientId), MaxClientIdLength);
        Touch(now);
    }

    public void UpdateUsername(string username, DateTimeOffset now)
    {
        RequireKind(GitSourceKind.HttpsCredential);
        Username = NormalizeUsername(username);
        Touch(now);
    }

    public void ChangeDeployKey(SshKeyId sshKeyId, DateTimeOffset now)
    {
        RequireKind(GitSourceKind.DeployKey);
        SshKeyId = sshKeyId;
        Touch(now);
    }

    public void SetSystemWide(bool isSystemWide, DateTimeOffset now)
    {
        if (isSystemWide && !IsAppKind)
            throw new DomainException("Only GitHub, GitLab and Gitea app sources can be system-wide.");
        IsSystemWide = isSystemWide;
        Touch(now);
    }

    public void AttachPrivateKey(SecretReferenceId reference, DateTimeOffset now)
    {
        RequireKind(GitSourceKind.GitHubApp);
        PrivateKeyReferenceId = reference;
        Touch(now);
    }

    public void AttachClientSecret(SecretReferenceId reference, DateTimeOffset now)
    {
        RequireKind(GitSourceKind.GitLabApp, GitSourceKind.GiteaApp);
        ClientSecretReferenceId = reference;
        Touch(now);
    }

    public void AttachWebhookSecret(SecretReferenceId reference, DateTimeOffset now)
    {
        if (!IsAppKind) throw new DomainException("Only app sources receive provider webhooks.");
        WebhookSecretReferenceId = reference;
        Touch(now);
    }

    /// <summary>Records OAuth tokens (GitLab/Gitea) or the HTTPS token reference.</summary>
    public void AttachAccessToken(SecretReferenceId accessToken, SecretReferenceId? refreshToken,
        DateTimeOffset? expiresAt, DateTimeOffset now)
    {
        RequireKind(GitSourceKind.GitLabApp, GitSourceKind.GiteaApp, GitSourceKind.HttpsCredential);
        AccessTokenReferenceId = accessToken;
        if (refreshToken.HasValue) RefreshTokenReferenceId = refreshToken;
        AccessTokenExpiresAt = expiresAt;
        Touch(now);
    }

    public void RecordTokenRefresh(DateTimeOffset? expiresAt, DateTimeOffset now)
    {
        AccessTokenExpiresAt = expiresAt;
        Touch(now);
    }

    /// <summary>
    ///     Returns <c>null</c> when <paramref name="repository" /> may be cloned with this source, otherwise the reason it
    ///     may not. Token-based sources require HTTPS on exactly the provider host, port and path prefix; deploy keys
    ///     require an SSH URL (on the provider host when one is configured). The organization filter limits owners.
    /// </summary>
    public string? CheckRepository(RepositoryUrl repository)
    {
        if (repository.HasEmbeddedPassword)
            return "Repository URL must not contain credentials when a Git source is used.";

        if (Kind == GitSourceKind.DeployKey)
        {
            if (!repository.IsSsh) return "Deploy key sources clone over SSH; use an SSH repository URL.";
            if (HtmlUrl is not null &&
                !string.Equals(new Uri(HtmlUrl).Host, repository.Host, StringComparison.OrdinalIgnoreCase))
                return "Repository host does not match the deploy key source host.";
            return OwnerAllowed(repository.Path) ? null : "Repository owner is not allowed by this source.";
        }

        if (HtmlUrl is null) return "Source has no provider URL.";
        if (repository.Transport != RepositoryTransport.Https)
            return "This source clones over HTTPS; use the https:// repository URL.";
        var provider = new Uri(HtmlUrl);
        if (!string.Equals(provider.Host, repository.Host, StringComparison.OrdinalIgnoreCase) ||
            provider.Port != repository.EffectivePort)
            return "Repository host does not match the source provider URL.";

        var prefix = provider.AbsolutePath.Trim('/');
        var path = repository.Path;
        if (prefix.Length > 0)
        {
            if (!path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                return "Repository path is outside the source provider URL.";
            path = path[(prefix.Length + 1)..];
        }

        return OwnerAllowed(path) ? null : "Repository owner is not allowed by this source.";
    }

    /// <summary>The <c>owner/name</c> path of <paramref name="repository" /> relative to the provider path prefix.</summary>
    public string RepositoryPath(RepositoryUrl repository)
    {
        var path = repository.Path.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        if (HtmlUrl is null) return path;
        var prefix = new Uri(HtmlUrl).AbsolutePath.Trim('/');
        return prefix.Length > 0 && path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)
            ? path[(prefix.Length + 1)..]
            : path;
    }

    public bool AllowsOwner(string? owner)
    {
        if (string.IsNullOrWhiteSpace(Organization)) return true;
        return Organization.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(candidate => string.Equals(candidate, owner?.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private bool OwnerAllowed(string path)
    {
        var owner = path.Trim('/').Split('/', 2)[0];
        return AllowsOwner(owner);
    }

    private void SetEndpoints(string htmlUrl, string? apiUrl)
    {
        HtmlUrl = GitProviderEndpoints.NormalizeHtmlUrl(htmlUrl);
        ApiUrl = string.IsNullOrWhiteSpace(apiUrl)
            ? IsAppKind ? GitProviderEndpoints.ApiUrlFor(Kind, HtmlUrl) : null
            : GitProviderEndpoints.NormalizeApiUrl(apiUrl);
    }

    private void SetGitHubApp(long appId, long? installationId, string? clientId)
    {
        if (appId <= 0) throw new DomainException("GitHub App id must be positive.");
        if (installationId is <= 0) throw new DomainException("GitHub App installation id must be positive.");
        AppId = appId;
        InstallationId = installationId;
        ClientId = DomainValidation.Optional(clientId, nameof(ClientId), MaxClientIdLength);
    }

    private void SetSsh(string? customSshUser, int? customSshPort)
    {
        var user = string.IsNullOrWhiteSpace(customSshUser) ? null : customSshUser.Trim();
        if (user is not null && !SshUserPattern().IsMatch(user))
            throw new DomainException("Custom SSH user may only contain letters, digits, '.', '_' and '-'.");
        if (customSshPort is < 1 or > 65535) throw new DomainException("Custom SSH port must be 1-65535.");
        CustomSshUser = user;
        CustomSshPort = customSshPort;
    }

    private void RequireKind(params GitSourceKind[] kinds)
    {
        if (!kinds.Contains(Kind))
            throw new DomainException($"This operation is not supported for {Kind} sources.");
    }

    private static string? NormalizeOrganization(string? organization)
    {
        if (string.IsNullOrWhiteSpace(organization)) return null;
        var owners = organization.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(owner => owner.Trim('/'))
            .Where(owner => owner.Length > 0)
            .ToArray();
        if (owners.Any(owner => !OwnerPattern().IsMatch(owner)))
            throw new DomainException("Organization must be a comma-separated list of owner names.");
        var normalized = string.Join(',', owners);
        return DomainValidation.Optional(normalized, nameof(Organization), MaxOrganizationLength);
    }

    private static string NormalizeUsername(string username)
    {
        var value = DomainValidation.Required(username, nameof(Username), MaxUsernameLength);
        // Upstream api.php: the username must not contain ':' or line breaks (it is the user-info user).
        if (value.Contains(':', StringComparison.Ordinal) || value.Any(char.IsControl) || value.StartsWith('-'))
            throw new DomainException("HTTPS username must not contain ':' or control characters.");
        return value;
    }

    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SshUserPattern();

    [GeneratedRegex("^[A-Za-z0-9_.][A-Za-z0-9_.-]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex OwnerPattern();
}
