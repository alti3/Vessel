using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Jobs;
using Vessel.Application.Email;
using Vessel.Application.Persistence;
using Vessel.Application.Proxy;
using Vessel.Application.Redis;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Secrets;
using Vessel.Domain.Users;

namespace Vessel.Application.Security;

public sealed record SecretReencryptionResult(
    string ActiveKeyVersion,
    bool DryRun,
    int SecretValuesUsingOldKey,
    int SecretValuesReencrypted,
    int TwoFactorSecretsNeedingUpgrade,
    int TwoFactorSecretsReencrypted,
    int Undecryptable,
    int ProtectedEnvelopesNeedingUpgrade = 0,
    int ProtectedEnvelopesReencrypted = 0)
{
    public bool Succeeded => Undecryptable == 0;
}

/// <summary>
/// Re-encrypts every stored secret with the active key after a key rotation (Vessel's equivalent of
/// Coolify's <c>app-key:reencrypt</c>). Old keys must stay in <c>Secrets:Keys</c> until this completes
/// with zero undecryptable values. Batched, idempotent, exclusive, audited; plaintext is never logged.
/// </summary>
public sealed class SecretReencryptionService(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    ISecretCipher cipher,
    IDistributedLockManager locks,
    IBackgroundJobDispatcher backgroundJobs,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
{
    public const int DefaultBatchSize = 200;
    public const string LockKey = "vessel:secrets:reencrypt";

    /// <summary>Interactive entry point; requires an instance administrator.</summary>
    public Task<SecretReencryptionResult> ReencryptAsync(
        UserId actorUserId,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        if (!authorization.IsInstanceAdmin(actorUserId))
            throw new UnauthorizedAccessException("Secret re-encryption requires an instance administrator.");

        return RunAsync(actorUserId, dryRun, DefaultBatchSize, cancellationToken);
    }

    /// <summary>Queues a full re-encryption as a background job; requires an instance administrator.</summary>
    public string QueueReencryption(UserId actorUserId)
    {
        if (!authorization.IsInstanceAdmin(actorUserId))
            throw new UnauthorizedAccessException("Secret re-encryption requires an instance administrator.");

        Guid requestedBy = actorUserId.Value;
        return backgroundJobs.Enqueue<SecretReencryptionJob>(job => job.RunAsync(requestedBy, CancellationToken.None));
    }

    /// <summary>Unattended entry point for the background job (actor is recorded in the audit trail when known).</summary>
    public async Task<SecretReencryptionResult> RunAsync(
        UserId? actorUserId,
        bool dryRun,
        int batchSize = DefaultBatchSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        await using DistributedLockHandle? handle = await locks.TryAcquireAsync(
            LockKey,
            TimeSpan.FromMinutes(30),
            TimeSpan.FromSeconds(5),
            cancellationToken);
        if (handle is null)
            throw new InvalidOperationException("Secret re-encryption is already running.");

        string active = cipher.ActiveKeyVersion;
        var counters = new Counters();

        await ReencryptSecretValuesAsync(active, dryRun, batchSize, counters, cancellationToken);
        await ReencryptTwoFactorSecretsAsync(dryRun, batchSize, counters, cancellationToken);
        await ReencryptProxyConfigurationsAsync(dryRun, batchSize, counters, cancellationToken);
        await ReencryptInstanceEmailSecretsAsync(dryRun, counters, cancellationToken);

        var result = new SecretReencryptionResult(
            active,
            dryRun,
            counters.SecretValuesUsingOldKey,
            counters.SecretValuesReencrypted,
            counters.TwoFactorNeedingUpgrade,
            counters.TwoFactorReencrypted,
            counters.Undecryptable,
            counters.EnvelopesNeedingUpgrade,
            counters.EnvelopesReencrypted);

        await auditWriter.RecordAsync(
            null,
            actorUserId,
            AuditActions.SecretsReencrypted,
            new AuditTarget("secret_key_ring", active),
            null,
            new Dictionary<string, object?>
            {
                ["activeKeyVersion"] = active,
                ["dryRun"] = dryRun,
                ["secretValuesUsingOldKey"] = result.SecretValuesUsingOldKey,
                ["secretValuesReencrypted"] = result.SecretValuesReencrypted,
                ["twoFactorNeedingUpgrade"] = result.TwoFactorSecretsNeedingUpgrade,
                ["twoFactorReencrypted"] = result.TwoFactorSecretsReencrypted,
                ["protectedEnvelopesNeedingUpgrade"] = result.ProtectedEnvelopesNeedingUpgrade,
                ["protectedEnvelopesReencrypted"] = result.ProtectedEnvelopesReencrypted,
                ["undecryptable"] = result.Undecryptable
            },
            cancellationToken);

        return result;
    }

    private async Task ReencryptSecretValuesAsync(
        string activeVersion,
        bool dryRun,
        int batchSize,
        Counters counters,
        CancellationToken cancellationToken)
    {
        SecretValueId[] ids = dbContext.SecretValues
            .Where(value => value.KeyVersion != activeVersion)
            .Select(value => value.Id)
            .ToArray();
        counters.SecretValuesUsingOldKey = ids.Length;

        foreach (SecretValueId[] chunk in ids.Chunk(batchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            SecretValue[] values = dbContext.SecretValues.Where(value => chunk.Contains(value.Id)).ToArray();
            DateTimeOffset now = timeProvider.GetUtcNow();
            foreach (SecretValue value in values)
            {
                string associatedData = SecretValueAssociatedData.For(value.SecretReferenceId);
                string plaintext;
                try
                {
                    plaintext = cipher.Decrypt(
                        new SecretCipherText(value.CipherText, value.Nonce, value.Tag, value.KeyVersion),
                        associatedData);
                }
                catch (SecretDecryptionException)
                {
                    counters.Undecryptable++;
                    continue;
                }

                if (dryRun) continue;

                SecretCipherText encrypted = cipher.Encrypt(plaintext, associatedData);
                value.Replace(encrypted.CipherText, encrypted.Nonce, encrypted.Tag, encrypted.KeyVersion, now);
                counters.SecretValuesReencrypted++;
            }

            if (!dryRun) await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task ReencryptTwoFactorSecretsAsync(
        bool dryRun,
        int batchSize,
        Counters counters,
        CancellationToken cancellationToken)
    {
        var protector = new TwoFactorSecretProtector(cipher);
        UserId[] ids = dbContext.Users
            .Where(user => user.TwoFactorSecret != null)
            .Select(user => user.Id)
            .ToArray();

        foreach (UserId[] chunk in ids.Chunk(batchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            User[] users = dbContext.Users.Where(user => chunk.Contains(user.Id)).ToArray();
            DateTimeOffset now = timeProvider.GetUtcNow();
            var changed = false;
            foreach (User user in users)
            {
                if (!protector.NeedsRewrap(user)) continue;
                counters.TwoFactorNeedingUpgrade++;
                try
                {
                    if (dryRun)
                    {
                        _ = protector.Reveal(user);
                        continue;
                    }

                    if (protector.TryRewrap(user, now))
                    {
                        counters.TwoFactorReencrypted++;
                        changed = true;
                    }
                }
                catch (SecretDecryptionException)
                {
                    counters.Undecryptable++;
                }
            }

            if (changed) await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    ///     Stored proxy configuration versions and user dynamic files are protected per server (NET-07). Rows written
    ///     before encryption at rest are plaintext and are encrypted here too.
    /// </summary>
    private async Task ReencryptProxyConfigurationsAsync(
        bool dryRun,
        int batchSize,
        Counters counters,
        CancellationToken cancellationToken)
    {
        var protector = new ProxyConfigurationProtector(cipher);

        ProxyConfigurationVersionId[] versionIds = dbContext.ProxyConfigurationVersions
            .Select(version => version.Id)
            .ToArray();
        foreach (ProxyConfigurationVersionId[] chunk in versionIds.Chunk(batchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var changed = false;
            DateTimeOffset now = timeProvider.GetUtcNow();
            foreach (var version in dbContext.ProxyConfigurationVersions.Where(item => chunk.Contains(item.Id))
                         .ToArray())
                if (TryRewrap(() => protector.RewrapIfStale(version.ServerId, version.Configuration), dryRun,
                        counters, out var rewrapped))
                {
                    version.RewrapConfiguration(rewrapped, now);
                    changed = true;
                }

            if (changed) await dbContext.SaveChangesAsync(cancellationToken);
        }

        ProxyDynamicConfigurationId[] fileIds = dbContext.ProxyDynamicConfigurations
            .Select(file => file.Id)
            .ToArray();
        foreach (ProxyDynamicConfigurationId[] chunk in fileIds.Chunk(batchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var changed = false;
            DateTimeOffset now = timeProvider.GetUtcNow();
            foreach (var file in dbContext.ProxyDynamicConfigurations.Where(item => chunk.Contains(item.Id))
                         .ToArray())
                if (TryRewrap(() => protector.RewrapIfStale(file.ServerId, file.Content), dryRun, counters,
                        out var rewrapped))
                {
                    file.Replace(rewrapped, file.ContentHash, now);
                    changed = true;
                }

            if (changed) await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>Instance SMTP passwords and the Resend API key (NTF-06).</summary>
    private async Task ReencryptInstanceEmailSecretsAsync(
        bool dryRun,
        Counters counters,
        CancellationToken cancellationToken)
    {
        var settings = dbContext.InstanceEmailSettings.FirstOrDefault();
        if (settings is null) return;

        var stale = new List<string>();
        foreach (var server in settings.SmtpServers)
            if (server.ProtectedPassword is not null && IsStale(server.ProtectedPassword))
                stale.Add(server.ProtectedPassword);
        var resendStale = settings.ProtectedResendApiKey is not null && IsStale(settings.ProtectedResendApiKey);
        var needing = stale.Count + (resendStale ? 1 : 0);
        if (needing == 0) return;
        counters.EnvelopesNeedingUpgrade += needing;

        try
        {
            // Decrypt everything first so a failure leaves the row untouched.
            foreach (var value in stale)
                _ = cipher.Unprotect(value, InstanceEmailSender.SmtpPasswordPurpose);
            if (resendStale)
                _ = cipher.Unprotect(settings.ProtectedResendApiKey!, InstanceEmailSender.ResendApiKeyPurpose);
        }
        catch (SecretDecryptionException)
        {
            counters.Undecryptable += needing;
            return;
        }

        if (dryRun) return;

        settings.RewrapSecrets(
            value => IsStale(value)
                ? cipher.Protect(cipher.Unprotect(value, InstanceEmailSender.SmtpPasswordPurpose),
                    InstanceEmailSender.SmtpPasswordPurpose)
                : value,
            value => IsStale(value)
                ? cipher.Protect(cipher.Unprotect(value, InstanceEmailSender.ResendApiKeyPurpose),
                    InstanceEmailSender.ResendApiKeyPurpose)
                : value,
            timeProvider.GetUtcNow());
        counters.EnvelopesReencrypted += needing;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private bool IsStale(string protectedValue)
    {
        return !string.Equals(cipher.GetProtectedKeyVersion(protectedValue), cipher.ActiveKeyVersion,
            StringComparison.Ordinal);
    }

    private static bool TryRewrap(Func<string?> rewrap, bool dryRun, Counters counters, out string rewrapped)
    {
        rewrapped = string.Empty;
        string? result;
        try
        {
            result = rewrap();
        }
        catch (SecretDecryptionException)
        {
            counters.Undecryptable++;
            return false;
        }

        if (result is null) return false;
        counters.EnvelopesNeedingUpgrade++;
        if (dryRun) return false;
        counters.EnvelopesReencrypted++;
        rewrapped = result;
        return true;
    }

    private sealed class Counters
    {
        public int SecretValuesUsingOldKey { get; set; }
        public int SecretValuesReencrypted { get; set; }
        public int TwoFactorNeedingUpgrade { get; set; }
        public int TwoFactorReencrypted { get; set; }
        public int Undecryptable { get; set; }
        public int EnvelopesNeedingUpgrade { get; set; }
        public int EnvelopesReencrypted { get; set; }
    }
}

/// <summary>Thin Hangfire entry point for <see cref="SecretReencryptionService"/>.</summary>
public sealed class SecretReencryptionJob(SecretReencryptionService service)
{
    public Task RunAsync(Guid? requestedByUserId, CancellationToken cancellationToken = default)
    {
        UserId? actor = requestedByUserId.HasValue ? new UserId(requestedByUserId.Value) : null;
        return service.RunAsync(actor, dryRun: false, cancellationToken: cancellationToken);
    }
}

/// <summary>
/// Associated data that binds a <see cref="SecretValue"/> ciphertext to its reference. Shared by the vault
/// and the re-encryption command so both authenticate identically.
/// </summary>
public static class SecretValueAssociatedData
{
    public static string For(SecretReferenceId secretReferenceId)
    {
        return secretReferenceId.Value.ToString("D");
    }
}
