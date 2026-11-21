using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Persistence;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Secrets;

namespace Vessel.Infrastructure.Security;

public sealed class AesSecretVault(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    IAuditWriter auditWriter,
    TimeProvider timeProvider,
    ISecretCipher cipher) : ISecretVault
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
        SecretCipherText encrypted = Encrypt(plaintext, reference.Id);
        var secretValue = SecretValue.Create(reference.Id, encrypted.CipherText, encrypted.Nonce, encrypted.Tag,
            encrypted.KeyVersion, now);

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
        SecretCipherText encrypted = Encrypt(plaintext, secretReferenceId);
        secretValue.Replace(encrypted.CipherText, encrypted.Nonce, encrypted.Tag, encrypted.KeyVersion,
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

    private SecretCipherText Encrypt(string plaintext, SecretReferenceId secretReferenceId)
    {
        return cipher.Encrypt(plaintext, AssociatedData(secretReferenceId));
    }

    private string Decrypt(SecretValue secretValue)
    {
        return cipher.Decrypt(
            new SecretCipherText(secretValue.CipherText, secretValue.Nonce, secretValue.Tag, secretValue.KeyVersion),
            AssociatedData(secretValue.SecretReferenceId));
    }

    private static string AssociatedData(SecretReferenceId secretReferenceId)
    {
        return SecretValueAssociatedData.For(secretReferenceId);
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

}
