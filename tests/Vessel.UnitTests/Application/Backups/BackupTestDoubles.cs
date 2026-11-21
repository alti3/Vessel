using System.Linq.Expressions;
using System.Net;
using System.Text;
using Vessel.Application.Backups;
using Vessel.Application.Jobs;
using Vessel.Application.ManagedServices;
using Vessel.Application.OutboundRequests;
using Vessel.Application.Security;
using Vessel.Application.Storage;
using Vessel.Domain;
using Vessel.Domain.Secrets;

namespace Vessel.UnitTests.Application.Backups;

internal sealed class InMemoryObjectStorage : IObjectStorage
{
    public Dictionary<(string Bucket, string Key), byte[]> Objects { get; } = [];

    public bool FailPuts { get; set; }

    public async Task PutAsync(ObjectStoragePutRequest request, CancellationToken cancellationToken = default)
    {
        if (FailPuts) throw new BackupDestinationUnavailableException("The destination rejected the request (403).");
        using var buffer = new MemoryStream();
        await request.Content.CopyToAsync(buffer, cancellationToken);
        Objects[(request.Location.Bucket, request.Location.Key)] = buffer.ToArray();
    }

    public Task<Stream> OpenReadAsync(ObjectStorageKey location, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<Stream>(new MemoryStream(Objects[(location.Bucket, location.Key)]));
    }

    public Task<bool> ExistsAsync(ObjectStorageKey location, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Objects.ContainsKey((location.Bucket, location.Key)));
    }

    public Task DeleteAsync(ObjectStorageKey location, CancellationToken cancellationToken = default)
    {
        Objects.Remove((location.Bucket, location.Key));
        return Task.CompletedTask;
    }
}

internal sealed class FakeDestinationClient(InMemoryObjectStorage storage, bool failValidation) : IBackupDestinationClient
{
    public bool Disposed { get; private set; }

    public Task PutAsync(ObjectStoragePutRequest request, CancellationToken cancellationToken = default) =>
        storage.PutAsync(request, cancellationToken);

    public Task<Stream> OpenReadAsync(ObjectStorageKey location, CancellationToken cancellationToken = default) =>
        storage.OpenReadAsync(location, cancellationToken);

    public Task<bool> ExistsAsync(ObjectStorageKey location, CancellationToken cancellationToken = default) =>
        storage.ExistsAsync(location, cancellationToken);

    public Task DeleteAsync(ObjectStorageKey location, CancellationToken cancellationToken = default) =>
        storage.DeleteAsync(location, cancellationToken);

    public Task ValidateAsync(CancellationToken cancellationToken = default)
    {
        return failValidation
            ? throw new BackupDestinationUnavailableException(
                "The destination rejected the request (403 AccessDenied).")
            : Task.CompletedTask;
    }

    public void Dispose()
    {
        Disposed = true;
    }
}

internal sealed class FakeBackupStorageProvider : IBackupStorageProvider
{
    public InMemoryObjectStorage LocalStorage { get; } = new();

    public InMemoryObjectStorage Remote { get; } = new();

    public List<BackupDestinationConnection> Connections { get; } = [];

    public bool FailValidation { get; set; }

    public BackupLocalStore Local => new(LocalStorage, "database-backups");

    public IBackupDestinationClient Connect(BackupDestinationConnection connection)
    {
        Connections.Add(connection);
        return new FakeDestinationClient(Remote, FailValidation);
    }
}

internal sealed class FakeDatabaseBackupProvider : IDatabaseBackupProvider
{
    public List<DatabaseBackupRequest> Requests { get; } = [];

    public IReadOnlyList<string> Databases { get; set; } = [];

    public Exception? Failure { get; set; }

    public Task<BackupArtifactFile> BackupAsync(DatabaseBackupRequest request,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        if (Failure is not null) throw Failure;
        var bytes = Encoding.UTF8.GetBytes($"dump of {request.DatabaseName ?? "default"}");
        var key = $"database/{request.Database.Id.Value:D}/{request.ExecutionId.Value:N}.pgdump";
        return Task.FromResult(new BackupArtifactFile(key, bytes.Length, "ab12", "application/octet-stream",
            "postgresql-custom", () => new MemoryStream(bytes), () => ValueTask.CompletedTask));
    }

    public Task<DatabaseRestoreOutcome> RestoreAsync(DatabaseRestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<IReadOnlyList<string>> ListDatabasesAsync(DatabaseListRequest request,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Databases);
    }
}

internal sealed class DictionaryVault(BackupTestDbContext db) : ISecretVault
{
    public Dictionary<SecretReferenceId, string> Secrets { get; } = [];

    public Task<SecretReference> StoreAsync(TeamId teamId, SecretScope scope, string key, string plaintext,
        SecretPolicy policy, SecretTarget target, CancellationToken cancellationToken = default)
    {
        SecretReference reference = SecretReference.Create(teamId, scope, key, SecretProvider.Vessel, "ref", policy,
            DateTimeOffset.UtcNow);
        Secrets[reference.Id] = plaintext;
        db.SecretReferenceItems.Add(reference);
        return Task.FromResult(reference);
    }

    public Task ReplaceAsync(SecretReferenceId secretReferenceId, string plaintext,
        CancellationToken cancellationToken = default)
    {
        Secrets[secretReferenceId] = plaintext;
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
        return Task.FromResult(Secrets.TryGetValue(secretReferenceId, out var value) ? value : "db-password");
    }
}

internal sealed class RecordingJobs : IBackgroundJobDispatcher, IRecurringJobScheduler
{
    public List<string> Enqueued { get; } = [];

    public Dictionary<string, string> Recurring { get; } = [];

    public List<string> Removed { get; } = [];

    public string Enqueue<TJob>(Expression<Func<TJob, Task>> methodCall)
    {
        Enqueued.Add(typeof(TJob).Name);
        return $"job-{Enqueued.Count}";
    }

    public string Schedule<TJob>(Expression<Func<TJob, Task>> methodCall, TimeSpan delay)
    {
        return Enqueue(methodCall);
    }

    public void AddOrUpdate<TJob>(string recurringJobId, Expression<Func<TJob, Task>> methodCall,
        string cronExpression)
    {
        Recurring[recurringJobId] = cronExpression;
    }

    public void RemoveIfExists(string recurringJobId)
    {
        Removed.Add(recurringJobId);
        Recurring.Remove(recurringJobId);
    }
}

internal sealed class AllowAllOutboundPolicy : IOutboundUrlPolicy
{
    public string? Deny { get; set; }

    public Task<OutboundTargetCheck> CheckUrlAsync(Uri url, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Deny is null ? OutboundTargetCheck.Allow() : OutboundTargetCheck.Deny(Deny));
    }

    public Task<OutboundTargetCheck> CheckHostAsync(string host, CancellationToken cancellationToken = default)
    {
        return CheckUrlAsync(new Uri("https://" + host), cancellationToken);
    }

    public bool IsAddressAllowed(string host, IPAddress address)
    {
        return Deny is null;
    }
}
