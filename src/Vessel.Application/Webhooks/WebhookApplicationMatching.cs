using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vessel.Application.Persistence;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Sources;
using Vessel.Domain.ValueObjects;
using Vessel.Domain.Webhooks;
using AppEntity = Vessel.Domain.Applications.Application;
using AppId = Vessel.Domain.ApplicationId;

namespace Vessel.Application.Webhooks;

/// <summary>
///     Shared webhook application matching and per-application signature verification (SEC-01).
///     A delivery is verified for an application only when that application's own enabled webhook secret validates it;
///     a valid signature for one application never authorizes another, even when both use the same repository.
/// </summary>
public static class WebhookApplicationMatching
{
    /// <summary>
    ///     Applications a delivery is addressed to: the explicit application for generic webhooks, otherwise every
    ///     application whose normalized repository identity equals the webhook repository exactly and whose branch equals
    ///     the pushed branch (or the pull request target branch).
    /// </summary>
    public static IReadOnlyList<AppEntity> Candidates(IVesselDbContext dbContext, ParsedWebhook parsed)
    {
        if (parsed.ApplicationId.HasValue)
        {
            var applicationId = new AppId(parsed.ApplicationId.Value);
            return dbContext.Applications.Where(application => application.Id == applicationId).ToArray();
        }

        // SEC-10: a branch that is not a valid git ref never matches (and never reaches git).
        if (parsed.Branch is null || !GitRef.TryParse(parsed.Branch, out _)) return [];
        RepositoryIdentity? webhookRepository =
            RepositoryIdentity.FromFullName(parsed.Repository, parsed.RepositoryWebUrl);
        if (webhookRepository is null) return [];

        return dbContext.Applications.AsEnumerable()
            .Where(application => string.Equals(application.GitSource.Branch, parsed.Branch, StringComparison.Ordinal))
            .Where(application => RepositoryMatches(application.GitSource.RepositoryUrl.Value, webhookRepository.Value))
            .ToArray();
    }

    public static bool RepositoryMatches(string applicationRepositoryUrl, RepositoryIdentity webhookRepository)
    {
        RepositoryIdentity? applicationRepository = RepositoryIdentity.FromRepositoryUrl(applicationRepositoryUrl);
        return applicationRepository is not null && applicationRepository.Value.Matches(webhookRepository);
    }

    /// <summary>Returns the candidate applications whose own secret verifies the delivery.</summary>
    public static async Task<IReadOnlyList<AppId>> VerifyAsync(
        IVesselDbContext dbContext,
        ISecretVault secretVault,
        WebhookProvider provider,
        IEnumerable<AppEntity> candidates,
        IReadOnlyDictionary<string, string> headers,
        string rawBody,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        var supplied = SuppliedCredential(provider, headers, payloadJson);
        if (string.IsNullOrEmpty(supplied)) return [];

        var verified = new List<AppId>();
        foreach (AppEntity application in candidates)
        {
            ApplicationWebhookConfiguration? configuration =
                dbContext.ApplicationWebhookConfigurations.SingleOrDefault(configuration =>
                    configuration.ApplicationId == application.Id &&
                    configuration.Provider == provider &&
                    configuration.IsEnabled);
            if (configuration is not null)
            {
                var secret = await secretVault.RevealForDeploymentAsync(TeamForApplication(dbContext, application),
                    configuration.SecretReferenceId, cancellationToken);
                if (Verify(provider, supplied, rawBody, secret))
                {
                    verified.Add(application.Id);
                    continue;
                }
            }

            // DEP-08: app sources (GitHub App, GitLab/Gitea app) sign deliveries with the source webhook secret. It
            // verifies only applications that use that source and only for the source's own provider.
            if (await VerifiedBySourceAsync(dbContext, secretVault, provider, application, supplied, rawBody,
                    cancellationToken))
                verified.Add(application.Id);
        }

        return verified;
    }

    private static async Task<bool> VerifiedBySourceAsync(IVesselDbContext dbContext, ISecretVault secretVault,
        WebhookProvider provider, AppEntity application, string supplied, string rawBody,
        CancellationToken cancellationToken)
    {
        if (application.SourceId is not { } sourceId) return false;
        GitSourceConnection? source = dbContext.GitSourceConnections.SingleOrDefault(item => item.Id == sourceId);
        if (source?.WebhookSecretReferenceId is not { } secretReference) return false;
        var sourceProvider = source.Kind switch
        {
            GitSourceKind.GitHubApp => WebhookProvider.GitHub,
            GitSourceKind.GitLabApp => WebhookProvider.GitLab,
            GitSourceKind.GiteaApp => WebhookProvider.Gitea,
            _ => (WebhookProvider?)null
        };
        if (sourceProvider != provider) return false;
        // The source must still be usable by the application's team (system-wide or owned by it).
        TeamId applicationTeam = TeamForApplication(dbContext, application);
        if (source.TeamId != applicationTeam && !source.IsSystemWide) return false;
        var secret = await secretVault.RevealForDeploymentAsync(source.TeamId, secretReference, cancellationToken);
        return Verify(provider, supplied, rawBody, secret);
    }

    public static bool Verify(WebhookProvider provider, string? supplied, string rawBody, string secret)
    {
        return provider switch
        {
            WebhookProvider.GitHub or WebhookProvider.Gitea or WebhookProvider.Bitbucket =>
                VerifySha256Signature(supplied, rawBody, secret),
            WebhookProvider.GitLab or WebhookProvider.Generic => FixedEquals(secret, supplied),
            _ => false
        };
    }

    public static string? SuppliedCredential(WebhookProvider provider, IReadOnlyDictionary<string, string> headers,
        string payloadJson)
    {
        return provider switch
        {
            WebhookProvider.GitHub => WebhookPayloadReader.Header(headers, "X-Hub-Signature-256"),
            WebhookProvider.Gitea => WebhookPayloadReader.Header(headers, "X-Hub-Signature-256"),
            WebhookProvider.GitLab => WebhookPayloadReader.Header(headers, "X-Gitlab-Token"),
            WebhookProvider.Bitbucket => WebhookPayloadReader.Header(headers, "X-Hub-Signature"),
            WebhookProvider.Generic => GenericSecret(payloadJson),
            _ => null
        };
    }

    public static TeamId TeamForApplication(IVesselDbContext dbContext, AppEntity application)
    {
        return (from environment in dbContext.Environments
                join project in dbContext.Projects on environment.ProjectId equals project.Id
                where environment.Id == application.EnvironmentId
                select project.TeamId)
            .Single();
    }

    private static string? GenericSecret(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return WebhookPayloadReader.Text(WebhookPayloadReader.PayloadRoot(document.RootElement), "secret");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool VerifySha256Signature(string? header, string payload, string secret)
    {
        if (string.IsNullOrWhiteSpace(header)) return false;
        var signature = header.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)
            ? header["sha256=".Length..]
            : header;
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload));
        return FixedEquals(Convert.ToHexString(hash).ToLowerInvariant(), signature.ToLowerInvariant());
    }

    private static bool FixedEquals(string? expected, string? actual)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(actual)) return false;
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        return expectedBytes.Length == actualBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}
