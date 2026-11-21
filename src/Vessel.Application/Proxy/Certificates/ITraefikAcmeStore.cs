using Vessel.Application.Servers;

namespace Vessel.Application.Proxy.Certificates;

/// <summary>
///     Metadata of one certificate in Traefik's ACME storage. Key material and the certificate body are never exposed.
///     <see cref="Id" /> is a stable SHA-256 identity of (resolver, entry).
/// </summary>
public sealed record AcmeCertificateEntry(
    string Id,
    string Resolver,
    string MainDomain,
    IReadOnlyList<string> SubjectAlternativeNames,
    DateTimeOffset? NotBefore,
    DateTimeOffset? ExpiresAt)
{
    public IEnumerable<string> AllNames => SubjectAlternativeNames.Prepend(MainDomain);
}

public sealed record AcmeBackup(string Name, DateTimeOffset CreatedAt);

/// <summary>Outcome of a write to <c>acme.json</c>; Traefik only reloads ACME storage on restart.</summary>
public sealed record AcmeWriteResult(int RemovedCertificates, string BackupName, bool RestartRequired);

/// <summary>
///     Reads and edits Traefik's <c>acme.json</c> on a server through the server file channel. Every write first backs the
///     current file up (owner-only, newest <see cref="MaxBackups" /> kept) and replaces the file atomically with mode 0600.
///     Callers must hold the <c>proxy:{serverId}</c> lock. Implementations never log or return key material.
/// </summary>
public interface ITraefikAcmeStore
{
    public const int MaxBackups = 10;

    /// <summary>Certificates per resolver; empty when the storage file is missing or empty.</summary>
    Task<IReadOnlyList<AcmeCertificateEntry>> ListCertificatesAsync(
        ServerExecutionTarget target,
        CancellationToken cancellationToken = default);

    /// <summary>Removes the given certificates (by <see cref="AcmeCertificateEntry.Id" />) from every resolver.</summary>
    /// <exception cref="InvalidOperationException">A certificate was not found or the storage is not valid JSON.</exception>
    Task<AcmeWriteResult> DeleteCertificatesAsync(
        ServerExecutionTarget target,
        IReadOnlyCollection<string> certificateIds,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AcmeBackup>> ListBackupsAsync(
        ServerExecutionTarget target,
        CancellationToken cancellationToken = default);

    /// <summary>Restores a backup (the current file is backed up first).</summary>
    Task<AcmeWriteResult> RestoreBackupAsync(
        ServerExecutionTarget target,
        string backupName,
        CancellationToken cancellationToken = default);

    Task DeleteBackupAsync(
        ServerExecutionTarget target,
        string backupName,
        CancellationToken cancellationToken = default);
}
