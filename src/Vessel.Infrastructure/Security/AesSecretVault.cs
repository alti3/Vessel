using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Persistence;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Secrets;
using Vessel.Infrastructure.Configuration;

namespace Vessel.Infrastructure.Security;

public sealed class AesSecretVault(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    IAuditWriter auditWriter,
    TimeProvider timeProvider,
    IOptions<SecretStorageOptions> options) : ISecretVault
{
    public async Task<SecretReference> StoreAsync(
        TeamId teamId,
        SecretScope scope,
        string key,
        string plaintext,
        SecretPolicy policy,
        SecretTarget target,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        var reference = SecretReference.Create(teamId, scope, key, SecretProvider.Vessel,
            $"vessel://secrets/{Guid.NewGuid():D}", policy, now);
        ApplyTarget(reference, target);
        EncryptedSecret encrypted = Encrypt(plaintext, reference.Id);
        var secretValue = SecretValue.Create(reference.Id, encrypted.CipherText, encrypted.Nonce, encrypted.Tag,
            options.Value.KeyVersion, now);

        await dbContext.SecretReferenceRepository.AddAsync(reference, cancellationToken);
        await dbContext.SecretValueRepository.AddAsync(secretValue, cancellationToken);
        await auditWriter.RecordAsync(teamId, null, AuditActions.SecretStored,
            new AuditTarget("secret", reference.Id.Value.ToString("D")), null,
            new Dictionary<string, object?> { ["scope"] = scope.ToString(), ["key"] = key }, cancellationToken);

        return reference;
    }

    public async Task ReplaceAsync(
        SecretReferenceId secretReferenceId,
        string plaintext,
        CancellationToken cancellationToken = default)
    {
        SecretValue secretValue = dbContext.SecretValues.Single(value => value.SecretReferenceId == secretReferenceId);
        EncryptedSecret encrypted = Encrypt(plaintext, secretReferenceId);
        secretValue.Replace(encrypted.CipherText, encrypted.Nonce, encrypted.Tag, options.Value.KeyVersion,
            timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<string> RevealAsync(
        UserId actorUserId,
        TeamId teamId,
        SecretReferenceId secretReferenceId,
        CancellationToken cancellationToken = default)
    {
        if (!authorization.HasPermission(actorUserId, teamId, VesselPermissions.SecretsRead))
            throw new UnauthorizedAccessException("Secret reveal requires secrets.read.");

        SecretReference reference = dbContext.SecretReferences.Single(reference => reference.Id == secretReferenceId);
        if (reference.TeamId != teamId) throw new UnauthorizedAccessException("Secret is outside the active team.");
        SecretValue secretValue = dbContext.SecretValues.Single(value => value.SecretReferenceId == secretReferenceId);
        var plaintext = Decrypt(secretValue);
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.SecretRevealed,
            new AuditTarget("secret", secretReferenceId.Value.ToString("D")), null,
            new Dictionary<string, object?> { ["scope"] = reference.Scope.ToString(), ["key"] = reference.Key },
            cancellationToken);

        return plaintext;
    }

    public async Task<string> RevealForDeploymentAsync(
        TeamId teamId,
        SecretReferenceId secretReferenceId,
        CancellationToken cancellationToken = default)
    {
        SecretReference reference = dbContext.SecretReferences.Single(reference => reference.Id == secretReferenceId);
        if (reference.TeamId != teamId) throw new UnauthorizedAccessException("Secret is outside the active team.");
        SecretValue secretValue = dbContext.SecretValues.Single(value => value.SecretReferenceId == secretReferenceId);
        await auditWriter.RecordAsync(teamId, null, AuditActions.SecretRevealed,
            new AuditTarget("secret", secretReferenceId.Value.ToString("D")), null,
            new Dictionary<string, object?>
            { ["scope"] = reference.Scope.ToString(), ["key"] = reference.Key, ["purpose"] = "deployment" },
            cancellationToken);

        return Decrypt(secretValue);
    }

    private EncryptedSecret Encrypt(string plaintext, SecretReferenceId secretReferenceId)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        if (string.IsNullOrWhiteSpace(plaintext) || plaintext.Length > 12000)
            throw new InvalidOperationException("Secret value is required and cannot exceed 12000 characters.");

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherText = new byte[plaintextBytes.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(GetKey(), 16);
        aes.Encrypt(nonce, plaintextBytes, cipherText, tag, AssociatedData(secretReferenceId));

        return new EncryptedSecret(Convert.ToBase64String(cipherText), Convert.ToBase64String(nonce),
            Convert.ToBase64String(tag));
    }

    private string Decrypt(SecretValue secretValue)
    {
        var cipherText = Convert.FromBase64String(secretValue.CipherText);
        var nonce = Convert.FromBase64String(secretValue.Nonce);
        var tag = Convert.FromBase64String(secretValue.Tag);
        var plaintextBytes = new byte[cipherText.Length];
        using var aes = new AesGcm(GetKey(), 16);
        aes.Decrypt(nonce, cipherText, tag, plaintextBytes, AssociatedData(secretValue.SecretReferenceId));

        return Encoding.UTF8.GetString(plaintextBytes);
    }

    private byte[] GetKey()
    {
        if (!string.IsNullOrWhiteSpace(options.Value.MasterKey))
        {
            var configured = Convert.FromBase64String(options.Value.MasterKey);
            if (configured.Length == 32) return configured;
        }

        return SHA256.HashData(Encoding.UTF8.GetBytes("vessel-alpha-development-secret-storage-key"));
    }

    private static byte[] AssociatedData(SecretReferenceId secretReferenceId)
    {
        return Encoding.UTF8.GetBytes(secretReferenceId.Value.ToString("D"));
    }

    private static void ApplyTarget(SecretReference reference, SecretTarget target)
    {
        if (target.ApplicationId.HasValue)
        {
            reference.TargetApplication(target.ProjectId!.Value, target.EnvironmentId!.Value,
                target.ApplicationId.Value);
            return;
        }

        if (target.DatabaseResourceId.HasValue)
        {
            reference.TargetDatabase(target.ProjectId!.Value, target.EnvironmentId!.Value,
                target.DatabaseResourceId.Value);
            return;
        }

        if (target.EnvironmentId.HasValue)
        {
            reference.TargetEnvironment(target.ProjectId!.Value, target.EnvironmentId.Value);
            return;
        }

        if (target.ProjectId.HasValue)
            reference.TargetProject(target.ProjectId.Value);
        if (target.ServerId.HasValue)
            reference.TargetServer(target.ServerId.Value);
    }

    private sealed record EncryptedSecret(string CipherText, string Nonce, string Tag);
}
