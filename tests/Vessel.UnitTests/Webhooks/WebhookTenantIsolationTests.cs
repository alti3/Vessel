using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using Vessel.Application.Auditing;
using Vessel.Application.Jobs;
using Vessel.Application.Persistence;
using Vessel.Application.Security;
using Vessel.Application.Deployments;
using Vessel.Application.Deployments.Queue;
using Vessel.Application.Webhooks;
using Vessel.Domain;
using Vessel.Domain.Applications;
using Vessel.Domain.Auditing;
using Vessel.Domain.Common;
using Vessel.Domain.Deployments;
using Vessel.Domain.Projects;
using Vessel.Domain.Secrets;
using Vessel.Domain.Servers;
using Vessel.Domain.ValueObjects;
using Vessel.Domain.Webhooks;
using AppEntity = Vessel.Domain.Applications.Application;
using EnvironmentEntity = Vessel.Domain.Projects.Environment;

namespace Vessel.UnitTests.Webhooks;

/// <summary>SEC-01 / DEP-02: per-application webhook verification, exact repository matching and fork gating.</summary>
public sealed class WebhookTenantIsolationTests
{
    private const string VictimSecret = "victim-secret-value";
    private const string AttackerSecret = "attacker-secret-value";
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public async Task Attacker_signature_never_deploys_another_teams_application_with_the_same_repository()
    {
        var scenario = new Scenario();
        AppEntity victim = scenario.AddApplication("https://github.com/org/app.git", VictimSecret, autoDeploy: true);
        // The attacker registers the victim's repository, disables auto-deploy and signs with a secret they know.
        AppEntity attacker =
            scenario.AddApplication("https://github.com/org/app", AttackerSecret, autoDeploy: false);

        WebhookReceiptResult receipt = await scenario.ReceivePushAsync("org/app", AttackerSecret);
        WebhookEvent stored = scenario.Db.Events.Single();

        Assert.Equal(WebhookSignatureStatus.Verified, receipt.SignatureStatus);
        Assert.True(stored.IsVerifiedFor(attacker.Id));
        Assert.False(stored.IsVerifiedFor(victim.Id));

        WebhookProcessingResult result = await scenario.ProcessAsync(stored);

        Assert.DoesNotContain(scenario.Db.DeploymentItems, deployment => deployment.ApplicationId == victim.Id);
        Assert.Empty(scenario.Db.DeploymentItems);
        Assert.Equal(WebhookEventStatus.Ignored, result.Status);
    }

    [Fact]
    public async Task Victims_own_signature_deploys_only_the_victim_at_the_pushed_commit()
    {
        var scenario = new Scenario();
        AppEntity victim = scenario.AddApplication("https://github.com/org/app.git", VictimSecret, autoDeploy: true);
        AppEntity other = scenario.AddApplication("https://github.com/org/app.git", AttackerSecret, autoDeploy: true);

        await scenario.ReceivePushAsync("org/app", VictimSecret);
        WebhookProcessingResult result = await scenario.ProcessAsync(scenario.Db.Events.Single());

        Deployment deployment = Assert.Single(scenario.Db.DeploymentItems);
        Assert.Equal(victim.Id, deployment.ApplicationId);
        Assert.NotEqual(other.Id, deployment.ApplicationId);
        Assert.Equal(Commit, deployment.CommitSha);
        Assert.Equal(WebhookEventStatus.Processed, result.Status);
    }

    [Fact]
    public async Task Repository_matching_is_exact_not_substring()
    {
        var scenario = new Scenario();
        AppEntity similar =
            scenario.AddApplication("https://github.com/org/app-private.git", VictimSecret, autoDeploy: true);

        WebhookReceiptResult receipt = await scenario.ReceivePushAsync("org/app", VictimSecret);

        Assert.Equal(WebhookEventStatus.Rejected, receipt.Status);
        Assert.False(scenario.Db.Events.Single().IsVerifiedFor(similar.Id));
        Assert.Empty(scenario.Db.DeploymentItems);
    }

    [Fact]
    public async Task Event_marked_verified_without_this_application_is_rejected_at_processing()
    {
        var scenario = new Scenario();
        AppEntity victim = scenario.AddApplication("https://github.com/org/app.git", VictimSecret, autoDeploy: true);
        AppEntity attacker = scenario.AddApplication("https://github.com/org/app.git", AttackerSecret,
            autoDeploy: true);
        attacker.UpdateSettings(attacker.Name, null,
            new GitSource(new RepositoryUrl("https://github.com/org/other.git"), "main"),
            attacker.BuildConfiguration, attacker.RuntimeConfiguration, attacker.DeploymentSettings, scenario.Now);

        // Simulates a delivery verified for an unrelated application only (legacy event-level "Verified").
        var webhookEvent = WebhookEvent.Receive(WebhookProvider.GitHub, "push", "d-1", "k-1",
            Scenario.Envelope(Scenario.PushBody("org/app"), "sha256=00"), WebhookSignatureStatus.Missing,
            scenario.Now);
        webhookEvent.MarkSignatureVerified([attacker.Id], scenario.Now);
        webhookEvent.MarkQueued(scenario.Now);
        scenario.Db.Events.Add(webhookEvent);

        WebhookProcessingResult result = await scenario.ProcessAsync(webhookEvent);

        Assert.Equal(WebhookEventStatus.Rejected, result.Status);
        Assert.DoesNotContain(scenario.Db.DeploymentItems, deployment => deployment.ApplicationId == victim.Id);
    }

    [Fact]
    public async Task Fork_pull_request_is_not_deployed_unless_public_previews_are_enabled()
    {
        var scenario = new Scenario();
        AppEntity application = scenario.AddApplication("https://github.com/org/app.git", VictimSecret,
            autoDeploy: true, previews: true);

        await scenario.ReceivePullRequestAsync("org/app", "attacker/app-fork", VictimSecret, "delivery-pr-1");
        WebhookProcessingResult denied = await scenario.ProcessAsync(scenario.Db.Events.Last());

        Assert.Equal(WebhookEventStatus.Ignored, denied.Status);
        Assert.Empty(scenario.Db.Previews);
        Assert.Empty(scenario.Db.DeploymentItems);

        application.UpdateSettings(application.Name, null, application.GitSource, application.BuildConfiguration,
            application.RuntimeConfiguration,
            application.DeploymentSettings with { PublicPreviewsEnabled = true }, scenario.Now);
        await scenario.ReceivePullRequestAsync("org/app", "attacker/app-fork", VictimSecret, "delivery-pr-2");
        WebhookProcessingResult allowed = await scenario.ProcessAsync(scenario.Db.Events.Last());

        Assert.Equal(WebhookEventStatus.Processed, allowed.Status);
        ApplicationPreview preview = Assert.Single(scenario.Db.Previews);
        Assert.False(preview.IsSameRepository);
        Assert.Equal("attacker/app-fork", preview.SourceRepository);
        Deployment deployment = Assert.Single(scenario.Db.DeploymentItems);
        Assert.Equal(preview.Id, deployment.PreviewId);
    }

    [Fact]
    public async Task Same_repository_pull_request_opens_an_isolated_preview()
    {
        var scenario = new Scenario();
        scenario.AddApplication("https://github.com/org/app.git", VictimSecret, autoDeploy: true, previews: true);

        await scenario.ReceivePullRequestAsync("org/app", "org/app", VictimSecret, "delivery-pr-3");
        WebhookProcessingResult result = await scenario.ProcessAsync(scenario.Db.Events.Single());

        Assert.Equal(WebhookEventStatus.Processed, result.Status);
        ApplicationPreview preview = Assert.Single(scenario.Db.Previews);
        Assert.True(preview.IsSameRepository);
        Assert.Equal(Commit, Assert.Single(scenario.Db.DeploymentItems).CommitSha);
    }

    [Fact]
    public async Task Push_with_option_like_commit_ref_is_not_queued()
    {
        var scenario = new Scenario();
        scenario.AddApplication("https://github.com/org/app.git", VictimSecret, autoDeploy: true);

        await scenario.ReceivePushAsync("org/app", VictimSecret, "--upload-pack=touch /tmp/pwned");
        WebhookProcessingResult result = await scenario.ProcessAsync(scenario.Db.Events.Single());

        Assert.Empty(scenario.Db.DeploymentItems);
        Assert.NotEqual(WebhookEventStatus.Processed, result.Status);
    }

    [Fact]
    public async Task One_delivery_evaluates_every_matching_application_independently_with_per_app_outcomes()
    {
        var scenario = new Scenario();
        AppEntity staging = scenario.AddApplication("https://github.com/org/app.git", VictimSecret, autoDeploy: true);
        AppEntity production =
            scenario.AddApplication("https://github.com/org/app.git", VictimSecret, autoDeploy: true);
        AppEntity docs = scenario.AddApplication("https://github.com/org/app.git", VictimSecret, autoDeploy: true);
        docs.UpdateDeploymentSettings(docs.DeploymentSettings with { WatchPaths = "docs/**" }, scenario.Now);
        AppEntity manual = scenario.AddApplication("https://github.com/org/app.git", VictimSecret, autoDeploy: false);

        await scenario.ReceivePushAsync("org/app", VictimSecret);
        WebhookProcessingResult result = await scenario.ProcessAsync(scenario.Db.Events.Single());

        Assert.Equal(WebhookEventStatus.Processed, result.Status);
        Assert.Equal(2, scenario.Db.DeploymentItems.Count);
        Assert.All(scenario.Db.DeploymentItems, item => Assert.Equal(DeploymentTrigger.Webhook, item.Trigger));
        IReadOnlyList<WebhookApplicationOutcome> outcomes = Assert.IsAssignableFrom<IReadOnlyList<WebhookApplicationOutcome>>(
            result.Applications);
        Assert.Equal(4, outcomes.Count);
        Assert.Equal(WebhookApplicationOutcomeKind.Queued, Outcome(outcomes, staging).Outcome);
        Assert.Equal(WebhookApplicationOutcomeKind.Queued, Outcome(outcomes, production).Outcome);
        Assert.Equal(WebhookApplicationOutcomeKind.Skipped, Outcome(outcomes, docs).Outcome);
        Assert.Contains("watch paths", Outcome(outcomes, docs).Reason);
        Assert.Equal(WebhookApplicationOutcomeKind.Skipped, Outcome(outcomes, manual).Outcome);
        Assert.Equal(outcomes, scenario.Db.Events.Single().ApplicationOutcomes);
    }

    [Fact]
    public async Task Redelivered_push_for_an_active_commit_is_deduplicated_not_failed()
    {
        var scenario = new Scenario();
        AppEntity app = scenario.AddApplication("https://github.com/org/app.git", VictimSecret, autoDeploy: true);
        await scenario.ReceivePushAsync("org/app", VictimSecret);
        await scenario.ProcessAsync(scenario.Db.Events.Single());
        // A second delivery (different id) of the same commit while the first deployment is still active.
        WebhookEvent second = WebhookEvent.Receive(WebhookProvider.GitHub, "push", "delivery-2", "dedupe-2",
            scenario.Db.Events.Single().PayloadJson, WebhookSignatureStatus.Missing, scenario.Now);
        second.MarkSignatureVerified([app.Id], scenario.Now);
        scenario.Db.Events.Add(second);

        WebhookProcessingResult result = await scenario.ProcessAsync(second);

        Assert.Single(scenario.Db.DeploymentItems);
        Assert.Equal(WebhookApplicationOutcomeKind.Deduplicated, Assert.Single(result.Applications!).Outcome);
        Assert.Equal(WebhookEventStatus.Processed, result.Status);
    }

    [Fact]
    public void Watch_paths_use_the_net_change_of_a_push()
    {
        IReadOnlyList<string> files = WebhookPayloadReader.NetChangedFiles(
        [
            (["tmp/scratch.txt", "src/a.cs"], [], []),
            ([], ["src/a.cs"], ["tmp/scratch.txt"]),
            ([], [], ["docs/old.md"])
        ]);

        Assert.Equal(["src/a.cs", "docs/old.md"], files);
    }

    private static WebhookApplicationOutcome Outcome(IReadOnlyList<WebhookApplicationOutcome> outcomes,
        AppEntity application)
    {
        return outcomes.Single(item => item.ApplicationId == application.Id.Value);
    }

    private sealed class Scenario
    {
        private readonly Dictionary<SecretReferenceId, string> _secrets = [];

        public DateTimeOffset Now { get; } = DateTimeOffset.Parse("2026-11-20T12:00:00Z");

        public TestDb Db { get; } = new();

        public AppEntity AddApplication(string repositoryUrl, string secret, bool autoDeploy, bool previews = false)
        {
            var teamId = TeamId.New();
            var project = Project.Create(teamId, new ResourceName("project"), Now);
            var environment = EnvironmentEntity.CreateProduction(project.Id, Now);
            var server = Server.Create(teamId, new ResourceName("server"),
                new ServerAddress("127.0.0.1", new PortNumber(22)), ServerConnectionType.Local,
                ContainerRuntimeKind.Docker, Now);
            server.ChangeStatus(ServerStatus.Reachable, Now);
            var application = AppEntity.Create(environment.Id, server.Id, new ResourceName("web"),
                new GitSource(new RepositoryUrl(repositoryUrl), "main"),
                BuildConfiguration.Default(ApplicationBuildPack.Dockerfile), Now);
            application.UpdateSettings(application.Name, null, application.GitSource,
                application.BuildConfiguration, application.RuntimeConfiguration,
                new DeploymentSettings(autoDeploy, previews, false, null), Now);
            var secretId = SecretReferenceId.New();
            _secrets[secretId] = secret;

            Db.ProjectItems.Add(project);
            Db.EnvironmentItems.Add(environment);
            Db.ServerItems.Add(server);
            Db.ApplicationItems.Add(application);
            Db.Configurations.Add(ApplicationWebhookConfiguration.Create(application.Id, WebhookProvider.GitHub,
                secretId, Now));
            return application;
        }

        public Task<WebhookReceiptResult> ReceivePushAsync(string repository, string signingSecret,
            string commit = Commit)
        {
            var body = PushBody(repository, commit);
            return ReceiveAsync(body, signingSecret, "push", "delivery-push");
        }

        public Task<WebhookReceiptResult> ReceivePullRequestAsync(string repository, string sourceRepository,
            string signingSecret, string deliveryId)
        {
            var body = $$$"""
                          {"action":"opened","number":7,
                           "repository":{"full_name":"{{{repository}}}","html_url":"https://github.com/{{{repository}}}"},
                           "pull_request":{"title":"Feature","html_url":"https://github.com/{{{repository}}}/pull/7",
                             "head":{"ref":"feature","sha":"{{{Commit}}}","repo":{"full_name":"{{{sourceRepository}}}"}},
                             "base":{"ref":"main","repo":{"full_name":"{{{repository}}}"} } } }
                          """;
            return ReceiveAsync(body, signingSecret, "pull_request", deliveryId);
        }

        public static string PushBody(string repository, string commit = Commit)
        {
            return $$"""
                     {"ref":"refs/heads/main","after":"{{commit}}",
                      "repository":{"full_name":"{{repository}}","html_url":"https://github.com/{{repository}}"},
                      "commits":[{"message":"Update","added":[],"modified":["a.cs"],"removed":[]}]}
                     """;
        }

        public static string Envelope(string body, string signature)
        {
            return System.Text.Json.JsonSerializer.Serialize(new
            {
                headers = new Dictionary<string, string>
                { ["X-GitHub-Event"] = "push", ["X-Hub-Signature-256"] = signature },
                rawBody = body,
                payload = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(body)
            });
        }

        public Task<WebhookProcessingResult> ProcessAsync(WebhookEvent webhookEvent)
        {
            var dispatcher = new NullDispatcher();
            var queue = new DeploymentQueueService(Db, new InMemoryDistributedLockManager(TimeProvider.System),
                dispatcher, new DeploymentCancellationRegistry(), new NullRealtime(), new FixedTime(Now));
            var service = new WebhookProcessingService(Db, dispatcher, queue, new NullAuditWriter(),
                new FixedTime(Now));
            return service.ProcessAsync(webhookEvent.Id, TestContext.Current.CancellationToken);
        }

        private Task<WebhookReceiptResult> ReceiveAsync(string body, string signingSecret, string eventType,
            string deliveryId)
        {
            var signature = "sha256=" + Convert.ToHexString(
                HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingSecret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
            var service = new WebhookReceiptService(Db, new NullDispatcher(), new Vault(_secrets),
                new NullAuditWriter(), new FixedTime(Now));
            return service.ReceiveAsync(new WebhookReceiptRequest(WebhookProvider.GitHub,
                new Dictionary<string, string>
                {
                    ["X-GitHub-Event"] = eventType,
                    ["X-GitHub-Delivery"] = deliveryId,
                    ["X-Hub-Signature-256"] = signature
                }, body), TestContext.Current.CancellationToken);
        }
    }

    private sealed class TestDb : UnavailableVesselDbContext, IVesselDbContext
    {
        public List<Project> ProjectItems { get; } = [];
        public List<EnvironmentEntity> EnvironmentItems { get; } = [];
        public List<Server> ServerItems { get; } = [];
        public List<AppEntity> ApplicationItems { get; } = [];
        public List<ApplicationWebhookConfiguration> Configurations { get; } = [];
        public List<WebhookEvent> Events { get; } = [];
        public List<ApplicationPreview> Previews { get; } = [];
        public List<Deployment> DeploymentItems { get; } = [];

        IQueryable<Project> IVesselDbContext.Projects => ProjectItems.AsQueryable();
        IQueryable<EnvironmentEntity> IVesselDbContext.Environments => EnvironmentItems.AsQueryable();
        IQueryable<Server> IVesselDbContext.Servers => ServerItems.AsQueryable();
        IQueryable<AppEntity> IVesselDbContext.Applications => ApplicationItems.AsQueryable();

        IQueryable<ApplicationWebhookConfiguration> IVesselDbContext.ApplicationWebhookConfigurations =>
            Configurations.AsQueryable();

        IQueryable<WebhookEvent> IVesselDbContext.WebhookEvents => Events.AsQueryable();
        IQueryable<ApplicationPreview> IVesselDbContext.ApplicationPreviews => Previews.AsQueryable();
        IQueryable<Deployment> IVesselDbContext.Deployments => DeploymentItems.AsQueryable();

        IRepository<WebhookEvent, WebhookEventId> IVesselDbContext.WebhookEventRepository =>
            new ListRepository<WebhookEvent, WebhookEventId>(Events);

        IRepository<ApplicationPreview, ApplicationPreviewId> IVesselDbContext.ApplicationPreviewRepository =>
            new ListRepository<ApplicationPreview, ApplicationPreviewId>(Previews);

        IRepository<Deployment, DeploymentId> IVesselDbContext.DeploymentRepository =>
            new ListRepository<Deployment, DeploymentId>(DeploymentItems);

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(1);
        }
    }

    private sealed class ListRepository<TEntity, TId>(List<TEntity> items) : IRepository<TEntity, TId>
        where TEntity : Entity<TId>
        where TId : notnull
    {
        public Task<TEntity?> GetByIdAsync(TId id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(items.FirstOrDefault(item => EqualityComparer<TId>.Default.Equals(item.Id, id)));
        }

        public Task AddAsync(TEntity entity, CancellationToken cancellationToken = default)
        {
            if (!items.Contains(entity)) items.Add(entity);
            return Task.CompletedTask;
        }

        public void Remove(TEntity entity)
        {
            items.Remove(entity);
        }
    }

    private sealed class Vault(Dictionary<SecretReferenceId, string> secrets) : ISecretVault
    {
        public Task<SecretReference> StoreAsync(TeamId teamId, SecretScope scope, string key, string plaintext,
            SecretPolicy policy, SecretTarget target, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task ReplaceAsync(SecretReferenceId secretReferenceId, string plaintext,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<string> RevealAsync(UserId actorUserId, TeamId teamId, SecretReferenceId secretReferenceId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<string> RevealForDeploymentAsync(TeamId teamId, SecretReferenceId secretReferenceId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(secrets[secretReferenceId]);
        }
    }

    private sealed class NullDispatcher : IBackgroundJobDispatcher
    {
        public string Enqueue<TJob>(Expression<Func<TJob, Task>> methodCall)
        {
            return "job";
        }

        public string Schedule<TJob>(Expression<Func<TJob, Task>> methodCall, TimeSpan delay)
        {
            return "job";
        }
    }

    private sealed class NullRealtime : Vessel.Application.Realtime.IRealtimeNotifier
    {
        public Task PublishAsync(Vessel.Application.Realtime.RealtimeGroup group,
            Vessel.Application.Realtime.RealtimeMessage message, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class NullAuditWriter : IAuditWriter
    {
        public Task RecordAsync(TeamId? teamId, UserId? actorUserId, string action, AuditTarget target,
            string? correlationId, IReadOnlyDictionary<string, object?> metadata,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return now;
        }
    }
}
