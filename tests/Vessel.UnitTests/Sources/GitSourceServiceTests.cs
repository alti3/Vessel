using Vessel.Application.Auditing;
using Vessel.Application.Git;
using Vessel.Application.Redis;
using Vessel.Application.Security;
using Vessel.Application.Sources;
using Vessel.Domain;
using Vessel.Domain.Common;
using Vessel.Domain.Secrets;
using Vessel.Domain.Sources;
using Vessel.Domain.Ssh;
using Vessel.Domain.ValueObjects;
using Vessel.Domain.Webhooks;
using Vessel.Application.Webhooks;
using Vessel.UnitTests.Security;

namespace Vessel.UnitTests.Sources;

/// <summary>DEP-08: Git source authorization, secret handling, host binding and clone credentials.</summary>
public sealed class GitSourceServiceTests
{
    private const string Pem = "-----BEGIN PRIVATE KEY-----\nfake\n-----END PRIVATE KEY-----\n";

    private readonly CrossTeamFixture _fixture = CrossTeamFixture.Create();
    private readonly FakeVault _vault = new();
    private readonly FakeProviderApi _api = new();

    [Fact]
    public async Task Team_admin_creates_https_source_and_secret_never_appears_in_the_summary()
    {
        GitSourceService service = Service();

        GitSourceSummary summary = await service.CreateAsync(_fixture.Actor.Id, _fixture.TeamA.Id,
            new CreateGitSourceRequest("private", GitSourceKind.HttpsCredential, "https://example.com",
                Username: "deploy", Password: "s3cret-token"), TestContext.Current.CancellationToken);

        Assert.True(summary.IsConnected);
        Assert.DoesNotContain("s3cret-token", summary.ToString(), StringComparison.Ordinal);
        Assert.Contains("s3cret-token", _vault.Plaintext.Values);
        GitSourceConnection stored = Assert.Single(_fixture.Store.Set<GitSourceConnection>());
        Assert.Equal(_fixture.TeamA.Id, stored.TeamId);
    }

    [Fact]
    public async Task Read_only_member_cannot_create_sources()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service().CreateAsync(_fixture.Actor.Id,
            _fixture.TeamB.Id, new CreateGitSourceRequest("x", GitSourceKind.HttpsCredential, "https://example.com",
                Username: "u", Password: "p"), TestContext.Current.CancellationToken));
        Assert.Empty(_fixture.Store.Set<GitSourceConnection>());
    }

    [Fact]
    public async Task System_wide_sources_require_an_instance_administrator()
    {
        var request = new CreateGitSourceRequest("shared", GitSourceKind.GitLabApp, "https://gitlab.example.com",
            IsSystemWide: true, AccessToken: "token");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service().CreateAsync(_fixture.Actor.Id,
            _fixture.TeamA.Id, request, TestContext.Current.CancellationToken));

        _fixture.Actor.GrantInstanceAdmin(_fixture.Now);
        GitSourceSummary created = await Service().CreateAsync(_fixture.Actor.Id, _fixture.TeamA.Id, request,
            TestContext.Current.CancellationToken);
        Assert.True(created.IsSystemWide);
        // Visible (not editable) from another team.
        Assert.Contains(Service().List(_fixture.Actor.Id, _fixture.TeamB.Id), source => source.Id == created.Id);
    }

    [Fact]
    public async Task Another_teams_source_is_invisible_and_cannot_be_assigned()
    {
        GitSourceConnection foreign = AddHttpsSource(_fixture.TeamB.Id, "https://example.com");
        GitSourceService service = Service();

        Assert.DoesNotContain(service.List(_fixture.Actor.Id, _fixture.TeamA.Id), source => source.Id == foreign.Id.Value);
        Assert.Throws<UnauthorizedAccessException>(() =>
            service.Get(_fixture.Actor.Id, _fixture.TeamA.Id, foreign.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.AssignToApplicationAsync(
            _fixture.Actor.Id, _fixture.TeamA.Id, _fixture.ApplicationA.Id, new AssignGitSourceRequest(foreign.Id.Value),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.DeleteAsync(_fixture.Actor.Id,
            _fixture.TeamA.Id, foreign.Id, TestContext.Current.CancellationToken));
        Assert.Null(_fixture.ApplicationA.SourceId);
    }

    [Fact]
    public async Task Source_for_another_host_cannot_be_assigned()
    {
        GitSourceConnection source = AddHttpsSource(_fixture.TeamA.Id, "https://github.com");

        await Assert.ThrowsAsync<DomainException>(() => Service().AssignToApplicationAsync(_fixture.Actor.Id,
            _fixture.TeamA.Id, _fixture.ApplicationA.Id, new AssignGitSourceRequest(source.Id.Value),
            TestContext.Current.CancellationToken));
        Assert.Null(_fixture.ApplicationA.SourceId);
    }

    [Fact]
    public async Task Sources_in_use_cannot_be_deleted()
    {
        GitSourceConnection source = AddHttpsSource(_fixture.TeamA.Id, "https://example.com");
        await Service().AssignToApplicationAsync(_fixture.Actor.Id, _fixture.TeamA.Id, _fixture.ApplicationA.Id,
            new AssignGitSourceRequest(source.Id.Value), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<GitSourceInUseException>(() => Service().DeleteAsync(_fixture.Actor.Id,
            _fixture.TeamA.Id, source.Id, TestContext.Current.CancellationToken));
        Assert.Equal(source.Id, _fixture.ApplicationA.SourceId);
    }

    [Fact]
    public async Task Deploy_key_sources_need_a_git_related_key_of_the_same_team()
    {
        SshKey serverKey = AddKey(_fixture.TeamA.Id, false);
        SshKey foreignKey = AddKey(_fixture.TeamB.Id, true);

        await Assert.ThrowsAsync<DomainException>(() => Service().CreateAsync(_fixture.Actor.Id, _fixture.TeamA.Id,
            new CreateGitSourceRequest("dk", GitSourceKind.DeployKey, SshKeyId: serverKey.Id.Value),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service().CreateAsync(_fixture.Actor.Id,
            _fixture.TeamA.Id, new CreateGitSourceRequest("dk", GitSourceKind.DeployKey,
                SshKeyId: foreignKey.Id.Value), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Https_credentials_are_bound_to_the_repository_host()
    {
        GitSourceConnection source = AddHttpsSource(_fixture.TeamA.Id, "https://example.com");

        GitCloneAccess access = await Resolver().ForRepositoryAsync(source,
            new RepositoryUrl("https://example.com/repo.git"), _fixture.TeamA.Id,
            TestContext.Current.CancellationToken);

        Assert.NotNull(access.Authentication.Https);
        Assert.Equal("example.com", access.Authentication.Https!.Host);
        Assert.Equal("token-value", access.Authentication.Https.Password);
        Assert.DoesNotContain("token-value", access.RepositoryUri.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("token-value", access.Authentication.Https.ToString(), StringComparison.Ordinal);
        await Assert.ThrowsAsync<DomainException>(() => Resolver().ForRepositoryAsync(source,
            new RepositoryUrl("https://attacker.example.net/repo.git"), _fixture.TeamA.Id,
            TestContext.Current.CancellationToken));
        // A team cannot borrow another team's (non system-wide) source.
        await Assert.ThrowsAsync<DomainException>(() => Resolver().ForRepositoryAsync(source,
            new RepositoryUrl("https://example.com/repo.git"), _fixture.TeamB.Id,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GitHub_app_sources_mint_an_installation_token_per_clone()
    {
        SecretReference key = await _vault.StoreAsync(_fixture.TeamA.Id, SecretScope.Team, "k", Pem,
            new SecretPolicy(false, false, false), new SecretTarget(), TestContext.Current.CancellationToken);
        var source = GitSourceConnection.CreateGitHubApp(_fixture.TeamA.Id, new ResourceName("gh"),
            "https://github.example.com", null, 42, 7, null, key.Id, null, null, false, _fixture.Now);
        _fixture.Store.Set<GitSourceConnection>().Add(source);

        GitCloneAccess first = await Resolver().ForRepositoryAsync(source,
            new RepositoryUrl("https://github.example.com/org/app.git"), _fixture.TeamA.Id,
            TestContext.Current.CancellationToken);
        GitCloneAccess second = await Resolver().ForRepositoryAsync(source,
            new RepositoryUrl("https://github.example.com/org/app.git"), _fixture.TeamA.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal("x-access-token", first.Authentication.Https!.Username);
        Assert.NotEqual(first.Authentication.Https.Password, second.Authentication.Https!.Password);
        Assert.Equal(["https://github.example.com/api/v3|42|7", "https://github.example.com/api/v3|42|7"],
            _api.TokenRequests);
    }

    [Fact]
    public async Task Ssh_host_trust_uses_published_keys_then_team_pins_then_first_use()
    {
        GitSourceConnection source = GitSourceConnection.CreateDeployKey(_fixture.TeamA.Id, new ResourceName("dk"),
            (await AddKeyWithSecretAsync(_fixture.TeamA.Id)).Id, null, null, null, _fixture.Now);
        _fixture.Store.Set<GitSourceConnection>().Add(source);

        GitCloneAccess github = await Resolver().ForRepositoryAsync(source,
            new RepositoryUrl("git@github.com:org/app.git"), _fixture.TeamA.Id, TestContext.Current.CancellationToken);
        Assert.False(github.Authentication.HostTrust!.AllowFirstUse);
        Assert.Equal(3, github.Authentication.HostTrust.KnownHostLines.Count);
        Assert.NotNull(github.Authentication.Ssh);

        GitCloneAccess unknown = await Resolver().ForRepositoryAsync(source,
            new RepositoryUrl("ssh://git@git.example.com:2222/org/app.git"), _fixture.TeamA.Id,
            TestContext.Current.CancellationToken);
        GitSshHostTrust trust = unknown.Authentication.HostTrust!;
        Assert.True(trust.AllowFirstUse);
        Assert.Equal(2222, trust.Port);
        GitHostKeys.TryParse(
            "[git.example.com]:2222 ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl",
            out KnownHostsEntry entry);
        await trust.OnFirstUse!([entry], TestContext.Current.CancellationToken);

        GitKnownHost pinned = Assert.Single(_fixture.Store.Set<GitKnownHost>());
        Assert.Equal(_fixture.TeamA.Id, pinned.TeamId);
        GitCloneAccess again = await Resolver().ForRepositoryAsync(source,
            new RepositoryUrl("ssh://git@git.example.com:2222/org/app.git"), _fixture.TeamA.Id,
            TestContext.Current.CancellationToken);
        Assert.False(again.Authentication.HostTrust!.AllowFirstUse);
        Assert.Equal([pinned.KnownHostsLine], again.Authentication.HostTrust.KnownHostLines);
    }

    [Fact]
    public async Task Source_webhook_secret_verifies_only_applications_using_the_source()
    {
        SecretReference secret = await _vault.StoreAsync(_fixture.TeamA.Id, SecretScope.Team, "w", "hook-secret",
            new SecretPolicy(false, false, false), new SecretTarget(), TestContext.Current.CancellationToken);
        var source = GitSourceConnection.CreateGitHubApp(_fixture.TeamA.Id, new ResourceName("gh"),
            "https://example.com", null, 1, 2, null, SecretReferenceId.New(), secret.Id, null, false, _fixture.Now);
        _fixture.Store.Set<GitSourceConnection>().Add(source);
        _fixture.ApplicationA.AssignSource(source.Id, _fixture.Now);
        const string body = "{}";
        var signature = "sha256=" + Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(
            "hook-secret"u8.ToArray(), System.Text.Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        var headers = new Dictionary<string, string> { ["X-Hub-Signature-256"] = signature };

        IReadOnlyList<Vessel.Domain.ApplicationId> verified = await WebhookApplicationMatching.VerifyAsync(_fixture.Store.Context,
            _vault, WebhookProvider.GitHub, [_fixture.ApplicationA, _fixture.ApplicationB], headers, body, body,
            TestContext.Current.CancellationToken);
        IReadOnlyList<Vessel.Domain.ApplicationId> wrongProvider = await WebhookApplicationMatching.VerifyAsync(
            _fixture.Store.Context, _vault, WebhookProvider.Gitea, [_fixture.ApplicationA], headers, body, body,
            TestContext.Current.CancellationToken);

        Assert.Equal([_fixture.ApplicationA.Id], verified);
        Assert.Empty(wrongProvider);
    }

    private GitSourceService Service()
    {
        return new GitSourceService(_fixture.Store.Context, _fixture.Authorization, _vault, Resolver(), _api,
            (IGitClient)InertStub.Create(typeof(IGitClient)), (IAuditWriter)InertStub.Create(typeof(IAuditWriter)),
            TimeProvider.System);
    }

    private GitCredentialResolver Resolver()
    {
        return new GitCredentialResolver(_fixture.Store.Context, _vault, _api,
            (IDistributedLockManager)InertStub.Create(typeof(IDistributedLockManager)),
            (IAuditWriter)InertStub.Create(typeof(IAuditWriter)), TimeProvider.System);
    }

    private GitSourceConnection AddHttpsSource(TeamId teamId, string htmlUrl)
    {
        SecretReference token = SecretReference.Create(teamId, SecretScope.Team, "t", SecretProvider.Vessel,
            $"vessel://secrets/{Guid.NewGuid():D}", new SecretPolicy(false, false, false), _fixture.Now);
        _vault.Plaintext[token.Id] = "token-value";
        var source = GitSourceConnection.CreateHttpsCredential(teamId, new ResourceName("https"), htmlUrl, "deploy",
            token.Id, _fixture.Now);
        _fixture.Store.Set<GitSourceConnection>().Add(source);
        return source;
    }

    private SshKey AddKey(TeamId teamId, bool gitRelated)
    {
        var key = SshKey.Create(teamId, new ResourceName("key-" + Guid.NewGuid().ToString("N")[..6]), null,
            SecretReferenceId.New(), "ssh-ed25519 AAAA", "SHA256:abc", "ssh-ed25519", 256, gitRelated, _fixture.Now);
        _fixture.Store.Set<SshKey>().Add(key);
        return key;
    }

    private async Task<SshKey> AddKeyWithSecretAsync(TeamId teamId)
    {
        SecretReference secret = await _vault.StoreAsync(teamId, SecretScope.Team, "ssh", "PRIVATE KEY",
            new SecretPolicy(false, false, false), new SecretTarget(), TestContext.Current.CancellationToken);
        var key = SshKey.Create(teamId, new ResourceName("deploy"), null, secret.Id, "ssh-ed25519 AAAA",
            "SHA256:abc", "ssh-ed25519", 256, true, _fixture.Now);
        _fixture.Store.Set<SshKey>().Add(key);
        return key;
    }

    private sealed class FakeVault : ISecretVault
    {
        public Dictionary<SecretReferenceId, string> Plaintext { get; } = [];

        public Task<SecretReference> StoreAsync(TeamId teamId, SecretScope scope, string key, string plaintext,
            SecretPolicy policy, SecretTarget target, CancellationToken cancellationToken = default)
        {
            var reference = SecretReference.Create(teamId, scope, key, SecretProvider.Vessel,
                $"vessel://secrets/{Guid.NewGuid():D}", policy, DateTimeOffset.UtcNow);
            Plaintext[reference.Id] = plaintext;
            return Task.FromResult(reference);
        }

        public Task ReplaceAsync(SecretReferenceId secretReferenceId, string plaintext,
            CancellationToken cancellationToken = default)
        {
            Plaintext[secretReferenceId] = plaintext;
            return Task.CompletedTask;
        }

        public Task<string> RevealAsync(UserId actorUserId, TeamId teamId, SecretReferenceId secretReferenceId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<string> RevealForDeploymentAsync(TeamId teamId, SecretReferenceId secretReferenceId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Plaintext[secretReferenceId]);
        }
    }

    private sealed class FakeProviderApi : IGitProviderApi
    {
        public List<string> TokenRequests { get; } = [];

        public Task<GitProviderToken> CreateGitHubInstallationTokenAsync(string apiUrl, long appId,
            long installationId, string privateKeyPem, CancellationToken cancellationToken = default)
        {
            TokenRequests.Add($"{apiUrl}|{appId}|{installationId}");
            return Task.FromResult(new GitProviderToken("ghs_" + Guid.NewGuid().ToString("N"), null));
        }

        public Task<GitOAuthTokens> RefreshOAuthTokenAsync(GitSourceKind kind, string htmlUrl, string clientId,
            string clientSecret, string refreshToken, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<GitProviderRepository>> ListRepositoriesAsync(GitSourceKind kind, string apiUrl,
            string accessToken, int page, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<GitProviderBranch>> ListBranchesAsync(GitSourceKind kind, string apiUrl,
            string accessToken, string repositoryPath, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
