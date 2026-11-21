using System.Text.RegularExpressions;
using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Backups;
using Vessel.Application.Jobs;
using Vessel.Application.Persistence;
using Vessel.Application.Redis;
using Vessel.Application.Storage.Persistent;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Backups;
using Vessel.Domain.Common;
using Vessel.Domain.Databases;

namespace Vessel.Application.ManagedServices.DatabaseImports;

/// <summary>
///     Database imports (DATA-10, upstream <c>StartDatabaseImport</c>): chunked uploads to owned staging storage, server
///     path imports (instance administrators only) and imports of objects in team backup destinations. An import is a
///     <see cref="DatabaseRestore" /> without a backup execution, so it runs through the same restore job: checksum and
///     gzip verification, content-based format detection, engine compatibility, safety scanning and a single-transaction
///     restore, with an incrementally persisted log and realtime progress. Permission checks always run before any file
///     or path is inspected.
/// </summary>
public sealed partial class DatabaseImportService(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    IDatabaseImportStaging staging,
    IDistributedLockManager locks,
    IBackgroundJobDispatcher jobs,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
{
    /// <summary>Largest accepted chunk per request.</summary>
    public const long MaxChunkBytes = 64L * 1024 * 1024;

    public const int MaxOpenUploadsPerDatabase = 5;

    public const int MaxObjectKeyLength = 1024;

    public async Task<DatabaseImportUploadSummary> StartUploadAsync(UserId actorUserId, TeamId teamId,
        DatabaseResourceId databaseId, StartDatabaseImportUploadRequest request,
        CancellationToken cancellationToken = default)
    {
        authorization.Demand(actorUserId, teamId, ResourceRef.Database(databaseId), VesselPermissions.ProjectsWrite);
        DateTimeOffset now = Now();
        var open = dbContext.DatabaseImportUploads.Count(upload => upload.DatabaseResourceId == databaseId &&
                                                                   upload.ConsumedAt == null &&
                                                                   upload.ExpiresAt > now);
        if (open >= MaxOpenUploadsPerDatabase)
            throw new DomainException(
                $"A database can have at most {MaxOpenUploadsPerDatabase} pending uploads; finish or cancel one first.");
        var upload = DatabaseImportUpload.Start(teamId, databaseId, actorUserId, request.FileName, request.SizeBytes,
            staging.MaxImportBytes, now);
        await dbContext.DatabaseImportUploadRepository.AddAsync(upload, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(teamId, actorUserId, StorageAuditActions.DatabaseImportUploadStarted,
            new AuditTarget("database", databaseId.Value.ToString("D")), null,
            new Dictionary<string, object?>
            {
                ["uploadId"] = upload.Id.Value.ToString("D"),
                ["sizeBytes"] = upload.DeclaredSizeBytes
            }, cancellationToken);
        return ToSummary(upload);
    }

    /// <summary>
    ///     Appends one chunk. Chunks are sequential: <paramref name="offset" /> must equal the bytes received so far, so a
    ///     retried chunk is rejected with the current offset instead of being written twice.
    /// </summary>
    public async Task<DatabaseImportUploadSummary> AppendChunkAsync(UserId actorUserId, TeamId teamId,
        DatabaseResourceId databaseId, DatabaseImportUploadId uploadId, long offset, Stream content,
        CancellationToken cancellationToken = default)
    {
        authorization.Demand(actorUserId, teamId, ResourceRef.Database(databaseId), VesselPermissions.ProjectsWrite);
        await using DistributedLockHandle? handle = await locks.TryAcquireAsync(UploadLockKey(uploadId),
            TimeSpan.FromMinutes(30), TimeSpan.Zero, cancellationToken);
        if (handle is null)
            throw new DatabaseOperationConflictException("Another chunk of this upload is being written.");
        DatabaseImportUpload upload = await GetUploadAsync(teamId, databaseId, uploadId, cancellationToken);
        await dbContext.RefreshAsync(upload, cancellationToken);
        DateTimeOffset now = Now();
        upload.EnsureCanAppend(offset, 0, now);
        var limit = Math.Min(MaxChunkBytes, upload.DeclaredSizeBytes - upload.ReceivedBytes);
        var written = await staging.AppendAsync(upload.Id, offset, content, limit, cancellationToken);
        upload.RecordChunk(offset, written, Now());
        await dbContext.SaveChangesAsync(CancellationToken.None);
        return ToSummary(upload);
    }

    public async Task<DatabaseImportUploadSummary> CompleteUploadAsync(UserId actorUserId, TeamId teamId,
        DatabaseResourceId databaseId, DatabaseImportUploadId uploadId, CancellationToken cancellationToken = default)
    {
        authorization.Demand(actorUserId, teamId, ResourceRef.Database(databaseId), VesselPermissions.ProjectsWrite);
        DatabaseImportUpload upload = await GetUploadAsync(teamId, databaseId, uploadId, cancellationToken);
        upload.Complete(Now());
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToSummary(upload);
    }

    public async Task<DatabaseImportUploadSummary> GetUploadSummaryAsync(UserId actorUserId, TeamId teamId,
        DatabaseResourceId databaseId, DatabaseImportUploadId uploadId, CancellationToken cancellationToken = default)
    {
        authorization.Demand(actorUserId, teamId, ResourceRef.Database(databaseId), VesselPermissions.ProjectsRead);
        return ToSummary(await GetUploadAsync(teamId, databaseId, uploadId, cancellationToken));
    }

    public async Task CancelUploadAsync(UserId actorUserId, TeamId teamId, DatabaseResourceId databaseId,
        DatabaseImportUploadId uploadId, CancellationToken cancellationToken = default)
    {
        authorization.Demand(actorUserId, teamId, ResourceRef.Database(databaseId), VesselPermissions.ProjectsWrite);
        DatabaseImportUpload upload = await GetUploadAsync(teamId, databaseId, uploadId, cancellationToken);
        if (IsReferencedByActiveImport(upload.Id))
            throw new DatabaseOperationConflictException("An import is using this upload; wait until it finishes.");
        await staging.DeleteAsync(upload.Id, cancellationToken);
        dbContext.DatabaseImportUploadRepository.Remove(upload);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<DatabaseRestoreSummary> QueueImportAsync(UserId actorUserId, TeamId teamId,
        DatabaseResourceId databaseId, QueueDatabaseImportRequest request,
        CancellationToken cancellationToken = default)
    {
        // Permission checks come before any file, path or object is looked at (upstream v4.6.0).
        authorization.Demand(actorUserId, teamId, ResourceRef.Database(databaseId), VesselPermissions.ProjectsWrite);
        if (request.Source == DatabaseImportSourceRequestKind.Server && !authorization.IsInstanceAdmin(actorUserId))
            throw new UnauthorizedAccessException(
                "Importing from a path on the server requires an instance administrator.");
        if (!request.DryRun &&
            !string.Equals(request.Confirmation, DatabaseRestoreService.ConfirmationPhrase, StringComparison.Ordinal))
            throw new DomainException($"Import confirmation must be {DatabaseRestoreService.ConfirmationPhrase}.");

        DatabaseResource database = await dbContext.DatabaseResourceRepository.GetByIdAsync(databaseId,
                                        cancellationToken)
                                    ?? throw new InvalidOperationException("Database was not found.");
        if (database.LifecycleState != DatabaseLifecycleState.Running || string.IsNullOrWhiteSpace(database.ContainerName))
            throw new DomainException("The database must be running before data can be imported.");
        if ((request.ReplaceExisting || request.KeepOwners) && database.Engine != DatabaseEngine.PostgreSql)
            throw new DomainException("Replace-existing and keep-owners options apply to PostgreSQL imports only.");
        if (request.DatabaseName is not null && database.Engine != DatabaseEngine.PostgreSql)
            throw new DomainException("Choosing a target database applies to PostgreSQL imports only.");
        if (dbContext.DatabaseRestores.Any(restore => restore.TargetDatabaseResourceId == database.Id &&
                                                      (restore.Status == DatabaseRestoreStatus.Queued ||
                                                       restore.Status == DatabaseRestoreStatus.Running)))
            throw new DatabaseOperationConflictException(
                "Another restore or import is already queued or running for this database.");

        DatabaseImportSourceKind kind;
        string reference;
        BackupDestinationId? destinationId = null;
        switch (request.Source)
        {
            case DatabaseImportSourceRequestKind.Upload:
                {
                    if (request.UploadId is not { } id) throw new DomainException("Upload ID is required.");
                    DatabaseImportUpload upload =
                        await GetUploadAsync(teamId, databaseId, new DatabaseImportUploadId(id), cancellationToken);
                    if (!upload.IsComplete) throw new DomainException("The upload is not complete yet.");
                    if (upload.ConsumedAt is not null || upload.IsExpired(Now()))
                        throw new DomainException("The upload was already imported or has expired; upload it again.");
                    if (IsReferencedByActiveImport(upload.Id))
                        throw new DatabaseOperationConflictException("An import is already using this upload.");
                    kind = DatabaseImportSourceKind.Upload;
                    reference = upload.Id.Value.ToString("D");
                    break;
                }
            case DatabaseImportSourceRequestKind.Server:
                kind = DatabaseImportSourceKind.ServerPath;
                reference = staging.ValidateServerPath(request.Path ?? throw new DomainException("Path is required."));
                break;
            case DatabaseImportSourceRequestKind.Destination:
                {
                    if (request.DestinationId is not { } id) throw new DomainException("Backup destination is required.");
                    var requested = new BackupDestinationId(id);
                    // Unknown and other teams' destinations fail the same way, so IDs cannot be probed.
                    BackupDestination destination = dbContext.BackupDestinations.SingleOrDefault(item =>
                                                        item.Id == requested && item.TeamId == teamId)
                                                    ?? throw new DomainException("Backup destination was not found.");
                    kind = DatabaseImportSourceKind.Destination;
                    reference = ValidateObjectKey(request.Path);
                    destinationId = destination.Id;
                    break;
                }
            default:
                throw new DomainException("Unknown import source.");
        }

        var restore = DatabaseRestore.QueueImport(teamId, database.Id, actorUserId, kind, reference, destinationId,
            request.DatabaseName, request.DryRun, request.ReplaceExisting, request.KeepOwners, Now());
        restore.AppendLog(request.DryRun
            ? $"Import dry run queued from {Describe(kind)}: the dump will be verified and inspected without changing the database."
            : $"Import queued from {Describe(kind)}: data in database '{database.Name.Value}' will be overwritten.",
            Now());
        await dbContext.DatabaseRestoreRepository.AddAsync(restore, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        var jobId = jobs.Enqueue<RunDatabaseRestoreJob>(job => job.RunAsync(restore.Id.Value, CancellationToken.None));
        await auditWriter.RecordAsync(teamId, actorUserId, StorageAuditActions.DatabaseImportQueued,
            new AuditTarget("database", database.Id.Value.ToString("D")), null,
            new Dictionary<string, object?>
            {
                ["restoreId"] = restore.Id.Value.ToString("D"),
                ["source"] = kind.ToString(),
                // Server paths and object keys are operator-supplied locations, not secrets.
                ["reference"] = reference,
                ["dryRun"] = request.DryRun,
                ["jobId"] = jobId
            }, cancellationToken);
        return DatabaseRestoreService.ToSummary(restore);
    }

    public async Task<DatabaseRestoreSummary> GetImportAsync(UserId actorUserId, TeamId teamId,
        DatabaseResourceId databaseId, DatabaseRestoreId restoreId, CancellationToken cancellationToken = default)
    {
        authorization.Demand(actorUserId, teamId, ResourceRef.Database(databaseId), VesselPermissions.ProjectsRead);
        DatabaseRestore restore = await dbContext.DatabaseRestoreRepository.GetByIdAsync(restoreId, cancellationToken)
                                  ?? throw new InvalidOperationException("Import was not found.");
        if (restore.TeamId != teamId || restore.TargetDatabaseResourceId != databaseId || !restore.IsImport)
            throw new InvalidOperationException("Import was not found.");
        return DatabaseRestoreService.ToSummary(restore);
    }

    /// <summary>
    ///     Deletes staged uploads that expired (24 hours, upstream <c>CleanupDatabaseImportUploads</c>) or were imported,
    ///     unless an active import still reads them, and staged files without a record.
    /// </summary>
    public async Task<int> CleanupUploadsAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = Now();
        DatabaseImportUpload[] candidates = dbContext.DatabaseImportUploads
            .Where(upload => upload.ExpiresAt <= now || upload.ConsumedAt != null)
            .ToArray();
        var deleted = 0;
        foreach (DatabaseImportUpload upload in candidates)
        {
            if (IsReferencedByActiveImport(upload.Id)) continue;
            await staging.DeleteAsync(upload.Id, cancellationToken);
            dbContext.DatabaseImportUploadRepository.Remove(upload);
            deleted++;
        }

        if (deleted > 0) await dbContext.SaveChangesAsync(cancellationToken);
        HashSet<Guid> known = dbContext.DatabaseImportUploads.Select(upload => upload.Id).AsEnumerable()
            .Select(id => id.Value).ToHashSet();
        foreach (Guid orphan in staging.ListStaged().Where(id => !known.Contains(id)))
        {
            await staging.DeleteAsync(new DatabaseImportUploadId(orphan), cancellationToken);
            deleted++;
        }

        return deleted;
    }

    /// <summary>Object keys are relative, bounded and free of control characters and traversal segments.</summary>
    public static string ValidateObjectKey(string? key)
    {
        var value = key?.Trim() ?? string.Empty;
        if (value.Length == 0) throw new DomainException("Object key is required.");
        if (value.Length > MaxObjectKeyLength) throw new DomainException("Object key is too long.");
        if (value.StartsWith('/') || value.Any(char.IsControl) || value.Split('/').Any(part => part is "." or ".."))
            throw new DomainException("Object key is invalid.");
        if (!ObjectKeyPattern().IsMatch(value)) throw new DomainException("Object key contains unsupported characters.");
        return value;
    }

    public static string UploadLockKey(DatabaseImportUploadId uploadId)
    {
        return $"database-import-upload:{uploadId.Value:D}";
    }

    private bool IsReferencedByActiveImport(DatabaseImportUploadId uploadId)
    {
        var reference = uploadId.Value.ToString("D");
        return dbContext.DatabaseRestores.Any(restore => restore.ImportSource == DatabaseImportSourceKind.Upload &&
                                                         restore.ImportReference == reference &&
                                                         (restore.Status == DatabaseRestoreStatus.Queued ||
                                                          restore.Status == DatabaseRestoreStatus.Running));
    }

    private async Task<DatabaseImportUpload> GetUploadAsync(TeamId teamId, DatabaseResourceId databaseId,
        DatabaseImportUploadId uploadId, CancellationToken cancellationToken)
    {
        DatabaseImportUpload? upload =
            await dbContext.DatabaseImportUploadRepository.GetByIdAsync(uploadId, cancellationToken);
        // Other teams' uploads and uploads of other databases are reported as missing.
        if (upload is null || upload.TeamId != teamId || upload.DatabaseResourceId != databaseId)
            throw new InvalidOperationException("Upload was not found.");
        return upload;
    }

    private static string Describe(DatabaseImportSourceKind kind)
    {
        return kind switch
        {
            DatabaseImportSourceKind.Upload => "an uploaded file",
            DatabaseImportSourceKind.ServerPath => "a file on the server",
            _ => "a backup destination"
        };
    }

    private DateTimeOffset Now()
    {
        return timeProvider.GetUtcNow();
    }

    private static DatabaseImportUploadSummary ToSummary(DatabaseImportUpload upload)
    {
        return new DatabaseImportUploadSummary(upload.Id.Value, upload.DatabaseResourceId.Value, upload.FileName,
            upload.DeclaredSizeBytes, upload.ReceivedBytes, upload.IsComplete, upload.CreatedAt, upload.ExpiresAt,
            MaxChunkBytes);
    }

    [GeneratedRegex(@"^[A-Za-z0-9!_.*'()/+=@,:-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ObjectKeyPattern();
}

/// <summary>Opens the dump of an import and finishes its source after the run.</summary>
public sealed class DatabaseImportArtifactSource(
    IVesselDbContext dbContext,
    IDatabaseImportStaging staging,
    BackupArtifactReader artifacts,
    VesselAuthorizationService authorization,
    TimeProvider timeProvider)
{
    public async Task<Stream> OpenAsync(DatabaseRestore restore, CancellationToken cancellationToken = default)
    {
        if (!restore.IsImport || restore.ImportReference is null)
            throw new InvalidOperationException("The restore is not an import.");
        switch (restore.ImportSource)
        {
            case DatabaseImportSourceKind.Upload:
                {
                    var uploadId = new DatabaseImportUploadId(Guid.Parse(restore.ImportReference));
                    DatabaseImportUpload upload =
                        await dbContext.DatabaseImportUploadRepository.GetByIdAsync(uploadId, cancellationToken)
                        ?? throw new DomainException("The uploaded file no longer exists; upload it again.");
                    if (upload.TeamId != restore.TeamId || upload.DatabaseResourceId != restore.TargetDatabaseResourceId ||
                        !upload.IsComplete)
                        throw new DomainException("The uploaded file cannot be used for this import.");
                    return await staging.OpenReadAsync(uploadId, cancellationToken);
                }
            case DatabaseImportSourceKind.ServerPath:
                // Re-checked when the job runs: the requester must still be an instance administrator.
                if (restore.RequestedByUserId is not { } requester || !authorization.IsInstanceAdmin(requester))
                    throw new UnauthorizedAccessException(
                        "Importing from a path on the server requires an instance administrator.");
                return await staging.OpenServerFileAsync(staging.ValidateServerPath(restore.ImportReference),
                    cancellationToken);
            case DatabaseImportSourceKind.Destination:
                {
                    BackupDestination destination = dbContext.BackupDestinations.SingleOrDefault(item =>
                                                        item.Id == restore.ImportDestinationId &&
                                                        item.TeamId == restore.TeamId)
                                                    ?? throw new DomainException(
                                                        "The backup destination of this import no longer exists.");
                    return await artifacts.OpenObjectAsync(destination,
                        DatabaseImportService.ValidateObjectKey(restore.ImportReference), cancellationToken);
                }
            default:
                throw new InvalidOperationException("Unknown import source.");
        }
    }

    /// <summary>A successfully imported upload is consumed and its staged bytes are deleted.</summary>
    public async Task FinishAsync(DatabaseRestore restore, bool succeeded, CancellationToken cancellationToken)
    {
        if (!succeeded || restore.DryRun || restore.ImportSource != DatabaseImportSourceKind.Upload ||
            restore.ImportReference is null) return;
        var uploadId = new DatabaseImportUploadId(Guid.Parse(restore.ImportReference));
        DatabaseImportUpload? upload =
            await dbContext.DatabaseImportUploadRepository.GetByIdAsync(uploadId, cancellationToken);
        if (upload is null) return;
        upload.MarkConsumed(timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        try
        {
            await staging.DeleteAsync(uploadId, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The cleanup job deletes consumed uploads later.
        }
    }
}

public sealed class CleanupDatabaseImportUploadsJob(DatabaseImportService service)
{
    public const string RecurringJobId = "database-imports:cleanup-uploads";

    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        return service.CleanupUploadsAsync(cancellationToken);
    }
}
