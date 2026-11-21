using Vessel.Application.Deployments;
using Vessel.Application.Git;
using Vessel.Application.Proxy;
using Vessel.Domain;
using Vessel.Domain.Applications;
using Vessel.Domain.Common;
using Vessel.Domain.Deployments;
using Vessel.Domain.EnvironmentVariables;
using Vessel.Domain.ValueObjects;
using Vessel.Domain.Webhooks;
using AppEntity = Vessel.Domain.Applications.Application;
using AppId = Vessel.Domain.ApplicationId;

namespace Vessel.UnitTests.Deployments;

public sealed class DeploymentPlanBuilderTests
{
    private const string FullSha = "0123456789abcdef0123456789abcdef01234567";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-20T12:00:00Z");

    [Fact]
    public void Preview_naming_is_disjoint_from_production()
    {
        AppEntity application = CreateApplication();
        var serverId = ServerId.New();
        var deploymentId = DeploymentId.New();
        ApplicationPreview preview = CreatePreview(application.Id, 7);

        DeploymentNaming production = DeploymentPlanBuilder.Naming(application.Id, serverId, deploymentId, null);
        DeploymentNaming previewNaming =
            DeploymentPlanBuilder.Naming(application.Id, serverId, deploymentId, preview);

        Assert.False(production.IsPreview);
        Assert.True(previewNaming.IsPreview);
        Assert.Equal(ProxyNaming.UpstreamAlias(application.Id), production.UpstreamAlias);
        Assert.Equal(ProxyNaming.PreviewUpstreamAlias(application.Id, 7), previewNaming.UpstreamAlias);
        Assert.NotEqual(production.ProjectName, previewNaming.ProjectName);
        Assert.NotEqual(production.ServiceName, previewNaming.ServiceName);
        Assert.NotEqual(production.ImageName, previewNaming.ImageName);
        Assert.NotEqual(production.UpstreamAlias, previewNaming.UpstreamAlias);
        Assert.Equal(production.NetworkName, previewNaming.NetworkName);
        Assert.Equal("pr-7", previewNaming.PreviewSuffix);
        Assert.DoesNotContain("web", production.ServiceName, StringComparison.Ordinal);
    }

    [Fact]
    public void Naming_rejects_preview_of_another_application()
    {
        ApplicationPreview foreign = CreatePreview(AppId.New(), 3);

        Assert.Throws<DomainException>(() =>
            DeploymentPlanBuilder.Naming(AppId.New(), ServerId.New(), DeploymentId.New(), foreign));
    }

    [Theory]
    [InlineData(WebhookProvider.GitHub, "refs/pull/12/head")]
    [InlineData(WebhookProvider.Gitea, "refs/pull/12/head")]
    [InlineData(WebhookProvider.GitLab, "refs/merge-requests/12/head")]
    [InlineData(WebhookProvider.Bitbucket, "feature/login")]
    public void Pull_request_fetch_ref_is_provider_specific(WebhookProvider provider, string expected)
    {
        Assert.Equal(expected, DeploymentPlanBuilder.PullRequestFetchRef(provider, 12, "feature/login"));
    }

    [Fact]
    public void Bitbucket_source_branch_is_validated()
    {
        Assert.Throws<DomainException>(() =>
            DeploymentPlanBuilder.PullRequestFetchRef(WebhookProvider.Bitbucket, 1, "--upload-pack=x"));
    }

    [Fact]
    public void Commit_pinned_deployment_fetches_the_commit_with_branch_fallback()
    {
        AppEntity application = CreateApplication();
        Deployment deployment = Deployment.Queue(application.Id, application.ServerId, null, FullSha, Now);

        GitCheckoutRequest checkout = DeploymentPlanBuilder.Checkout(application, deployment, null, "/tmp/repo");

        Assert.Equal(GitCheckoutKind.Commit, checkout.Kind);
        Assert.Equal(FullSha, checkout.Reference);
        Assert.Equal("main", checkout.FallbackBranch);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("pending")]
    [InlineData("HEAD")]
    public void Unresolved_deployment_fetches_the_branch_head(string? commit)
    {
        AppEntity application = CreateApplication();
        Deployment deployment = Deployment.Queue(application.Id, application.ServerId, null, commit, Now);

        GitCheckoutRequest checkout = DeploymentPlanBuilder.Checkout(application, deployment, null, "/tmp/repo");

        Assert.Equal(GitCheckoutKind.Named, checkout.Kind);
        Assert.Equal("main", checkout.Reference);
    }

    [Fact]
    public void Tag_deployment_fetches_the_named_ref()
    {
        AppEntity application = CreateApplication();
        Deployment deployment = Deployment.Queue(application.Id, application.ServerId, null, "v1.2.3", Now);

        GitCheckoutRequest checkout = DeploymentPlanBuilder.Checkout(application, deployment, null, "/tmp/repo");

        Assert.Equal(GitCheckoutKind.Named, checkout.Kind);
        Assert.Equal("v1.2.3", checkout.Reference);
    }

    [Fact]
    public void Preview_deployment_fetches_the_pull_request_ref_and_pins_its_head()
    {
        AppEntity application = CreateApplication();
        ApplicationPreview preview = CreatePreview(application.Id, 9);
        Deployment deployment = Deployment.Queue(application.Id, application.ServerId, null, FullSha, preview.Id,
            null, Now);

        GitCheckoutRequest checkout = DeploymentPlanBuilder.Checkout(application, deployment, preview, "/tmp/repo");

        Assert.Equal(GitCheckoutKind.PullRequest, checkout.Kind);
        Assert.Equal("refs/pull/9/head", checkout.Reference);
        Assert.Equal(FullSha, checkout.ExpectedCommitSha);
    }

    [Theory]
    [InlineData("--upload-pack=touch /tmp/x")]
    [InlineData("main;rm -rf /")]
    [InlineData("a..b")]
    public void Invalid_refs_are_rejected_at_start(string reference)
    {
        AppEntity application = CreateApplication();
        Deployment deployment = Deployment.Queue(application.Id, application.ServerId, null, reference, Now);

        Assert.Throws<DomainException>(() =>
            DeploymentPlanBuilder.Checkout(application, deployment, null, "/tmp/repo"));
        Assert.Throws<DomainException>(() => DeploymentPlanBuilder.NormalizeRequestedRef(reference));
    }

    [Fact]
    public void Production_never_receives_preview_copies()
    {
        EnvironmentVariable[] variables = [Variable("DB_URL", "prod", false), Variable("DB_URL", "preview", true)];

        DeploymentVariableSelection selection = DeploymentPlanBuilder.SelectVariables(variables, false, false);

        EnvironmentVariable selected = Assert.Single(selection.Variables);
        Assert.Equal("prod", selected.PlainValue);
    }

    [Fact]
    public void Same_repository_preview_falls_back_to_production_values_without_preview_copy()
    {
        EnvironmentVariable[] variables =
        [
            Variable("DB_URL", "prod", false), Variable("DB_URL", "preview", true), Variable("API_KEY", "key", false)
        ];

        DeploymentVariableSelection selection = DeploymentPlanBuilder.SelectVariables(variables, true, false);

        Assert.True(selection.UsesProductionFallback);
        Assert.Equal("preview", Value(selection, "DB_URL"));
        Assert.Equal("key", Value(selection, "API_KEY"));
        Assert.Empty(selection.WithheldKeys);
    }

    [Fact]
    public void Preview_without_preview_copies_gets_no_production_values()
    {
        EnvironmentVariable[] variables = [Variable("SECRET", "prod", false)];

        DeploymentVariableSelection selection = DeploymentPlanBuilder.SelectVariables(variables, true, false);

        Assert.Empty(selection.Variables);
        Assert.Equal(["SECRET"], selection.WithheldKeys);
    }

    [Fact]
    public void Public_previews_never_receive_production_values()
    {
        EnvironmentVariable[] variables =
            [Variable("DB_URL", "prod", false), Variable("TOKEN", "prod-token", false), Variable("DB_URL", "pv", true)];

        DeploymentVariableSelection selection = DeploymentPlanBuilder.SelectVariables(variables, true, true);

        Assert.False(selection.UsesProductionFallback);
        EnvironmentVariable selected = Assert.Single(selection.Variables);
        Assert.Equal("pv", selected.PlainValue);
        Assert.Equal(["TOKEN"], selection.WithheldKeys);
    }

    [Fact]
    public void Preview_route_uses_its_own_host_derived_from_the_canonical_domain()
    {
        AppEntity application = CreateApplication();
        application.UpsertDomain(new DomainName("other.example.com"), null, false, false, false, Now);
        application.UpsertDomain(new DomainName("app.example.com"), 3000, true, true, false, Now);

        PreviewRoute? route = DeploymentPlanBuilder.PreviewRoute(application.Domains, 42, 8080);

        Assert.NotNull(route);
        Assert.Equal("42.app.example.com", route.Host);
        Assert.Equal("https://42.app.example.com", route.Url);
        Assert.Equal(3000, route.TargetPort);
        Assert.Null(DeploymentPlanBuilder.PreviewRoute([], 42, 8080));
    }

    [Theory]
    [InlineData("Dockerfile", "Dockerfile")]
    [InlineData("./docker/Dockerfile.prod", "docker/Dockerfile.prod")]
    [InlineData("/compose/app..v2.yml", "compose/app..v2.yml")]
    public void Relative_paths_allow_dots_that_are_not_traversal(string value, string expected)
    {
        Assert.Equal(expected, DeploymentRunner.NormalizeRelativePath(value, "Dockerfile"));
    }

    [Theory]
    [InlineData("../Dockerfile")]
    [InlineData("docker/../../etc/passwd")]
    [InlineData("C:\\Windows\\file")]
    [InlineData("Dockerfile\nprivileged: true")]
    public void Relative_paths_reject_traversal_and_injection(string value)
    {
        Assert.Throws<DomainException>(() => DeploymentRunner.NormalizeRelativePath(value, "Dockerfile"));
    }

    private static string? Value(DeploymentVariableSelection selection, string key)
    {
        return selection.Variables.Last(variable => variable.Key.Value == key).PlainValue;
    }

    private static EnvironmentVariable Variable(string key, string value, bool preview)
    {
        return EnvironmentVariable.Create(TeamId.New(), EnvironmentVariableTargetType.Team,
            new EnvironmentVariableKey(key), EnvironmentVariableValueKind.Plain, value, null, false, true, preview,
            false, false, null, Now);
    }

    private static AppEntity CreateApplication()
    {
        return AppEntity.Create(EnvironmentId.New(), ServerId.New(), new ResourceName("web"),
            new GitSource(new RepositoryUrl("https://github.com/org/app.git"), "main"),
            BuildConfiguration.Default(ApplicationBuildPack.Dockerfile), Now);
    }

    private static ApplicationPreview CreatePreview(AppId applicationId, int number)
    {
        return ApplicationPreview.Open(applicationId, WebhookProvider.GitHub, number, "feature", "main", FullSha,
            null, "Feature", Now, "org/app", true);
    }
}
