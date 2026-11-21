using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Persistence;
using Vessel.Application.Settings;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Common;
using Vessel.Domain.Operations;

namespace Vessel.Application.Operations.Updates;

/// <summary>
///     Update channel and automatic-update policy of the instance (OPS-01). Instance administrators only. The policy is
///     the input of the Phase 14 <c>vessel self-update</c> CLI; this service never swaps binaries.
/// </summary>
public sealed partial class UpdatePolicyService(
    IVesselDbContext dbContext,
    SystemSettingsStore settings,
    VesselAuthorizationService authorization,
    IVesselVersionProvider versionProvider,
    IManifestSignatureVerifier signatureVerifier,
    IAuditWriter auditWriter)
{
    public const string SettingKey = "operations.update_policy";

    public async Task<UpdatePolicySummary> GetAsync(UserId actorUserId, CancellationToken cancellationToken = default)
    {
        RequireInstanceManager(authorization, actorUserId);
        return ToSummary(await LoadAsync(cancellationToken));
    }

    public async Task<UpdatePolicySummary> UpdateAsync(UserId actorUserId, UpdateUpdatePolicyRequest request,
        string? correlationId, CancellationToken cancellationToken = default)
    {
        RequireInstanceManager(authorization, actorUserId);
        ArgumentNullException.ThrowIfNull(request);
        UpdatePolicy current = await LoadAsync(cancellationToken);
        UpdatePolicy next = current;

        if (request.ManifestUrl is not null)
            next = next with { ManifestUrl = request.ManifestUrl.Length == 0 ? null : ValidateUrl(request.ManifestUrl) };
        if (request.ChecksumsUrl is not null)
            next = next with
            {
                ChecksumsUrl = request.ChecksumsUrl.Length == 0 ? null : ValidateUrl(request.ChecksumsUrl)
            };
        if (request.AutoUpdateEnabled is { } autoUpdate) next = next with { AutoUpdateEnabled = autoUpdate };
        if (request.CheckCron is not null) next = next with { CheckCron = ValidateCron(request.CheckCron) };
        if (request.CustomVersion is not null)
            next = next with
            {
                CustomVersion = request.CustomVersion.Length == 0 ? null : ValidateCustomVersion(request.CustomVersion)
            };
        if (request.Channel is { } channel && channel != current.Channel) next = SwitchChannel(next, channel);
        if (next.Channel == UpdateChannel.Custom && next.CustomVersion is null)
            throw new DomainException("The custom channel needs a version or tag to pin.");
        if (!next.AutoUpdateEnabled || next.Channel != current.Channel)
            next = next with { AutoUpdateTargetVersion = null };

        await SaveAsync(next, cancellationToken);
        await auditWriter.RecordAsync(null, actorUserId, OperationsAuditActions.UpdatePolicyChanged,
            new AuditTarget("instance_setting", SettingKey), correlationId,
            new Dictionary<string, object?>
            {
                ["channel"] = next.Channel.ToString(),
                ["previousChannel"] = next.PreviousChannel?.ToString(),
                ["customVersion"] = next.CustomVersion,
                ["manifestConfigured"] = next.ManifestUrl is not null,
                ["autoUpdateEnabled"] = next.AutoUpdateEnabled,
                ["checkCron"] = next.CheckCron
            }, cancellationToken);
        return ToSummary(next);
    }

    /// <summary>
    ///     Upstream <c>switchUpdateChannel</c>: switching to a more stable channel remembers the channel it came from, so
    ///     an instance that runs a pre-release keeps receiving its fixes until the stable channel catches up; switching
    ///     to a less stable (or custom) channel forgets it.
    /// </summary>
    public static UpdatePolicy SwitchChannel(UpdatePolicy policy, UpdateChannel channel)
    {
        if (channel == policy.Channel) return policy;
        UpdateChannel? previous = UpdateChannels.IsMoreStable(policy.Channel, channel)
            ? policy.PreviousChannel is { } older && older > policy.Channel ? older : policy.Channel
            : null;
        return policy with { Channel = channel, PreviousChannel = previous, LatestVersion = null };
    }

    /// <summary>Loads the policy, generating and persisting the randomized check schedule on first use.</summary>
    public async Task<UpdatePolicy> LoadAsync(CancellationToken cancellationToken = default)
    {
        UpdatePolicy policy = settings.Get(SettingKey, new UpdatePolicy());
        if (policy.CheckCron is not null) return policy;
        policy = policy with { CheckCron = GenerateCheckCron() };
        await SaveAsync(policy, cancellationToken);
        return policy;
    }

    public async Task SaveAsync(UpdatePolicy policy, CancellationToken cancellationToken = default)
    {
        await settings.SetAsync(SettingKey, policy, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>A daily check at a random minute and hour (upstream v4.5.0 "automatic updates are spread over time").</summary>
    public static string GenerateCheckCron()
    {
        return $"{RandomNumberGenerator.GetInt32(60)} {RandomNumberGenerator.GetInt32(24)} * * *";
    }

    public static Uri ResolveChecksumsUrl(UpdatePolicy policy)
    {
        if (policy.ChecksumsUrl is not null) return new Uri(policy.ChecksumsUrl);
        var manifest = new Uri(policy.ManifestUrl ?? throw new InvalidOperationException("No manifest URL."));
        return new Uri(manifest, "SHA256SUMS");
    }

    internal static void RequireInstanceManager(VesselAuthorizationService authorization, UserId actorUserId)
    {
        if (!authorization.IsInstanceManager(actorUserId))
            throw new UnauthorizedAccessException("Instance operations require an instance administrator.");
    }

    internal static string ValidateCron(string value)
    {
        var fields = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5 || fields.Any(field => !CronField().IsMatch(field)))
            throw new DomainException("The schedule must be a five-field cron expression (minute hour day month weekday).");
        return string.Join(' ', fields);
    }

    private UpdatePolicySummary ToSummary(UpdatePolicy policy)
    {
        return new UpdatePolicySummary(policy.Channel, policy.CustomVersion, policy.PreviousChannel,
            policy.ManifestUrl, policy.ChecksumsUrl, policy.AutoUpdateEnabled, policy.CheckCron ?? string.Empty,
            versionProvider.CurrentVersion, policy.LastCheckedAt, policy.LastCheckStatus, policy.LastCheckMessage,
            policy.LatestVersion, policy.AutoUpdateTargetVersion, policy.AutoUpdateStartedAt,
            signatureVerifier is not NotConfiguredManifestSignatureVerifier);
    }

    private static string ValidateUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? url) || url.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Fragment) || value.Length > 2048)
            throw new DomainException("Update URLs must be absolute https URLs without credentials or fragments.");
        return url.ToString();
    }

    private static string ValidateCustomVersion(string value)
    {
        var text = value.Trim();
        if (!CustomVersionPattern().IsMatch(text))
            throw new DomainException("The custom version must be 1-128 letters, digits, '.', '-', '_' or '+'.");
        return text;
    }

    [GeneratedRegex("^[0-9*,/-]{1,32}$")]
    private static partial Regex CronField();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._+-]{0,127}$")]
    private static partial Regex CustomVersionPattern();
}

/// <summary>
///     Checks the configured release manifest for a newer version of the followed channel (upstream
///     <c>CheckForUpdatesJob</c>). Without a manifest URL nothing is fetched. A newer version raises one
///     <c>platform.update_available</c> notification per version; with automatic updates on it becomes the target of
///     the next automatic update (installed by the Phase 14 CLI).
/// </summary>
public sealed class UpdateCheckService(
    UpdatePolicyService policies,
    VesselAuthorizationService authorization,
    IVersionManifestClient manifests,
    IManifestSignatureVerifier signatureVerifier,
    IVesselVersionProvider versionProvider,
    PlatformNotificationPublisher notifications,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
{
    public async Task<UpdateCheckResult> CheckAsync(UserId actorUserId, string? correlationId,
        CancellationToken cancellationToken = default)
    {
        UpdatePolicyService.RequireInstanceManager(authorization, actorUserId);
        UpdateCheckResult result = await RunCheckAsync(cancellationToken);
        await auditWriter.RecordAsync(null, actorUserId, OperationsAuditActions.UpdateChecked,
            new AuditTarget("instance_setting", UpdatePolicyService.SettingKey), correlationId,
            new Dictionary<string, object?>
            {
                ["status"] = result.Status.ToString(),
                ["latestVersion"] = result.LatestVersion
            }, cancellationToken);
        return result;
    }

    /// <summary>Entry point of the scheduled check (hooked into the scheduled-job ledger with the policy's cron).</summary>
    public Task<UpdateCheckResult> RunScheduledCheckAsync(CancellationToken cancellationToken = default)
    {
        return RunCheckAsync(cancellationToken);
    }

    private async Task<UpdateCheckResult> RunCheckAsync(CancellationToken cancellationToken)
    {
        UpdatePolicy policy = await policies.LoadAsync(cancellationToken);
        DateTimeOffset now = timeProvider.GetUtcNow();
        var current = versionProvider.CurrentVersion;
        UpdateCheckResult result;
        try
        {
            (result, policy) = await EvaluateAsync(policy, current, now, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException ||
                                          !cancellationToken.IsCancellationRequested)
        {
            var message = exception is ManifestVerificationException
                ? exception.Message
                : "The release manifest could not be fetched.";
            result = new UpdateCheckResult(UpdateCheckStatus.Failed, current, policy.LatestVersion, policy.Channel,
                message, now);
        }

        policy = policy with
        {
            LastCheckedAt = now,
            LastCheckStatus = result.Status,
            LastCheckMessage = result.Message,
            LatestVersion = result.Status == UpdateCheckStatus.Failed ? policy.LatestVersion : result.LatestVersion
        };
        if (result is { Status: UpdateCheckStatus.UpdateAvailable, LatestVersion: { } latest })
        {
            if (policy.AutoUpdateEnabled) policy = policy with { AutoUpdateTargetVersion = latest };
            if (!string.Equals(policy.NotifiedVersion, latest, StringComparison.Ordinal))
            {
                await notifications.PublishAsync(
                    PlatformNotifications.UpdateAvailable(current, latest, policy.Channel.ToString().ToLowerInvariant()),
                    cancellationToken);
                policy = policy with { NotifiedVersion = latest };
            }
        }

        await policies.SaveAsync(policy, cancellationToken);
        return result;
    }

    private async Task<(UpdateCheckResult Result, UpdatePolicy Policy)> EvaluateAsync(UpdatePolicy policy,
        string current, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (policy.Channel == UpdateChannel.Custom)
        {
            var pinned = policy.CustomVersion;
            if (pinned is null)
                return (new UpdateCheckResult(UpdateCheckStatus.NotConfigured, current, null, policy.Channel,
                    "No custom version is pinned.", now), policy);
            var status = string.Equals(Normalize(pinned), Normalize(current), StringComparison.OrdinalIgnoreCase)
                ? UpdateCheckStatus.UpToDate
                : UpdateCheckStatus.UpdateAvailable;
            return (new UpdateCheckResult(status, current, pinned, policy.Channel, null, now), policy);
        }

        if (policy.ManifestUrl is null)
            return (new UpdateCheckResult(UpdateCheckStatus.NotConfigured, current, null, policy.Channel,
                "No release manifest URL is configured.", now), policy);

        VersionManifestDocument document = await manifests.FetchAsync(new Uri(policy.ManifestUrl),
            UpdatePolicyService.ResolveChecksumsUrl(policy), cancellationToken);
        VersionManifest manifest = ManifestChecksumVerifier.VerifyAndParse(document);
        if (await signatureVerifier.VerifyAsync(document, manifest.Signature, cancellationToken) ==
            ManifestSignatureStatus.Invalid)
            throw new ManifestVerificationException("The release manifest signature is not valid.");

        VesselVersion installed = VesselVersion.TryParse(current, out VesselVersion? parsed)
            ? parsed!
            : VesselVersion.Zero;
        VesselVersion? best = ReleaseOf(manifest, policy.Channel);
        if (policy.PreviousChannel is { } previous)
        {
            if (best is not null && best >= installed.Core)
                policy = policy with { PreviousChannel = null };
            else if (ReleaseOf(manifest, previous) is { } fromPrevious && (best is null || fromPrevious > best))
                best = fromPrevious;
        }

        if (best is null)
            return (new UpdateCheckResult(UpdateCheckStatus.Failed, current, null, policy.Channel,
                "The release manifest has no release for this channel.", now), policy);
        UpdateCheckStatus outcome = best > installed ? UpdateCheckStatus.UpdateAvailable : UpdateCheckStatus.UpToDate;
        return (new UpdateCheckResult(outcome, current, best.ToString(), policy.Channel, null, now), policy);
    }

    private static VesselVersion? ReleaseOf(VersionManifest manifest, UpdateChannel channel)
    {
        if (channel == UpdateChannel.Custom) return null;
        return manifest.Channels.TryGetValue(UpdateChannels.ToManifestKey(channel), out VersionManifestRelease? release)
               && VesselVersion.TryParse(release.Version, out VesselVersion? version)
            ? version
            : null;
    }

    private static string Normalize(string version)
    {
        var text = version.Trim();
        return text.StartsWith('v') || text.StartsWith('V') ? text[1..] : text;
    }
}
