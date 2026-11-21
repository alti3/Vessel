using System.Text.Json.Serialization;
using Vessel.Domain.Operations;

namespace Vessel.Application.Operations.Updates;

/// <summary>
///     Instance update policy (OPS-01), stored as one System setting. Defaults: stable channel, no manifest URL (so
///     Vessel makes no outbound update requests until an administrator configures one), automatic updates off, and a
///     per-instance randomized daily check time so a fleet does not check the release server at the same minute.
/// </summary>
public sealed record UpdatePolicy
{
    public UpdateChannel Channel { get; init; } = UpdateChannel.Stable;

    /// <summary>Pinned version or tag when <see cref="Channel" /> is <see cref="UpdateChannel.Custom" />.</summary>
    public string? CustomVersion { get; init; }

    /// <summary>
    ///     The less stable channel the instance came from. Kept after a switch to a more stable channel until that channel
    ///     ships a release at least as new as the installed version (upstream <c>previous_update_channel</c>).
    /// </summary>
    public UpdateChannel? PreviousChannel { get; init; }

    public string? ManifestUrl { get; init; }

    /// <summary>The SHA-256 checksum list; defaults to <c>SHA256SUMS</c> next to the manifest.</summary>
    public string? ChecksumsUrl { get; init; }

    public bool AutoUpdateEnabled { get; init; }

    /// <summary>Five-field cron (UTC) of the update check, randomized once per instance.</summary>
    public string? CheckCron { get; init; }

    public DateTimeOffset? LastCheckedAt { get; init; }

    public UpdateCheckStatus? LastCheckStatus { get; init; }

    public string? LastCheckMessage { get; init; }

    public string? LatestVersion { get; init; }

    /// <summary>The newest version an update-available notification was sent for (one notification per version).</summary>
    public string? NotifiedVersion { get; init; }

    /// <summary>Version the next automatic update should install (consumed by the Phase 14 CLI).</summary>
    public string? AutoUpdateTargetVersion { get; init; }

    public DateTimeOffset? AutoUpdateStartedAt { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<UpdateCheckStatus>))]
public enum UpdateCheckStatus
{
    NotConfigured,
    UpToDate,
    UpdateAvailable,
    Failed
}

public sealed record UpdatePolicySummary(
    UpdateChannel Channel,
    string? CustomVersion,
    UpdateChannel? PreviousChannel,
    string? ManifestUrl,
    string? ChecksumsUrl,
    bool AutoUpdateEnabled,
    string CheckCron,
    string CurrentVersion,
    DateTimeOffset? LastCheckedAt,
    UpdateCheckStatus? LastCheckStatus,
    string? LastCheckMessage,
    string? LatestVersion,
    string? AutoUpdateTargetVersion,
    DateTimeOffset? AutoUpdateStartedAt,
    bool SignatureVerificationAvailable);

/// <summary>Changes the update policy. Null fields keep their value; <see cref="ManifestUrl" /> "" clears it.</summary>
public sealed record UpdateUpdatePolicyRequest(
    UpdateChannel? Channel = null,
    string? CustomVersion = null,
    string? ManifestUrl = null,
    string? ChecksumsUrl = null,
    bool? AutoUpdateEnabled = null,
    string? CheckCron = null);

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    string CurrentVersion,
    string? LatestVersion,
    UpdateChannel Channel,
    string? Message,
    DateTimeOffset CheckedAt);

/// <summary>Raw bytes of a fetched release manifest and its checksum list (verified before parsing).</summary>
public sealed record VersionManifestDocument(Uri ManifestUrl, byte[] ManifestBytes, string ChecksumList);

/// <summary>
///     <c>releases.json</c>: channel key (<c>stable</c>, <c>preview</c>, <c>nightly</c>) to the latest release of that
///     channel. See docs/deployment/phase-14-inputs.md.
/// </summary>
public sealed record VersionManifest(
    int SchemaVersion,
    DateTimeOffset? GeneratedAt,
    IReadOnlyDictionary<string, VersionManifestRelease> Channels,
    VersionManifestSignature? Signature);

public sealed record VersionManifestRelease(
    string Version,
    DateTimeOffset? PublishedAt,
    string? NotesUrl,
    IReadOnlyList<VersionManifestArtifact>? Artifacts);

public sealed record VersionManifestArtifact(string Os, string Arch, string Url, string Sha256);

/// <summary>Detached signature metadata over the checksum list (verification is a Phase 14 TODO).</summary>
public sealed record VersionManifestSignature(string Scheme, string? KeyId, string? SignatureUrl);

/// <summary>Fetches the manifest and checksum list through the SSRF-guarded outbound HTTP path.</summary>
public interface IVersionManifestClient
{
    Task<VersionManifestDocument> FetchAsync(Uri manifestUrl, Uri checksumsUrl,
        CancellationToken cancellationToken = default);
}

/// <summary>The version of the running Vessel binary.</summary>
public interface IVesselVersionProvider
{
    string CurrentVersion { get; }
}

public enum ManifestSignatureStatus
{
    /// <summary>No signature scheme is implemented or no public key is pinned yet (Phase 14 TODO).</summary>
    NotConfigured,
    Valid,
    Invalid
}

/// <summary>
///     Seam for detached-signature verification of the checksum list. Chosen scheme: minisign (Ed25519) signature
///     over <c>SHA256SUMS</c>, public key pinned in the binary. Until Phase 14 implements it, the default verifier
///     reports <see cref="ManifestSignatureStatus.NotConfigured" /> and only checksum integrity is enforced.
/// </summary>
public interface IManifestSignatureVerifier
{
    Task<ManifestSignatureStatus> VerifyAsync(VersionManifestDocument document, VersionManifestSignature? signature,
        CancellationToken cancellationToken = default);
}

public sealed class NotConfiguredManifestSignatureVerifier : IManifestSignatureVerifier
{
    public Task<ManifestSignatureStatus> VerifyAsync(VersionManifestDocument document,
        VersionManifestSignature? signature, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(ManifestSignatureStatus.NotConfigured);
    }
}

public sealed class ManifestVerificationException(string message) : InvalidOperationException(message);
