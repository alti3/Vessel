using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Jobs;
using Vessel.Application.Persistence;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Common;
using Vessel.Domain.Secrets;
using Vessel.Domain.Servers;
using Vessel.Domain.Ssh;
using Vessel.Domain.ValueObjects;

namespace Vessel.Application.Ssh.Keys;

/// <summary>
///     Team-owned SSH private keys (SRV-02/SEC-12). Private material is stored only in the secret vault and is never
///     returned; clients see the public key and SHA-256 fingerprint. Imported keys are validated strictly (OpenSSH/PEM
///     armor, unencrypted, no DSA, RSA at least 2048 bits) and generated keys are Ed25519. A key that servers still use
///     cannot be deleted.
/// </summary>
public sealed class SshKeyService(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    ISecretVault secretVault,
    ISshKeyTool keyTool,
    IBackgroundJobDispatcher jobs,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
{
    public IReadOnlyList<SshKeySummary> List(UserId actorUserId, TeamId teamId)
    {
        Require(actorUserId, teamId, VesselPermissions.ServersRead);
        SshKey[] keys = dbContext.SshKeys.Where(key => key.TeamId == teamId).ToArray();
        return keys.OrderBy(key => key.Name.Value, StringComparer.OrdinalIgnoreCase).Select(ToSummary).ToArray();
    }

    public SshKeySummary Get(UserId actorUserId, TeamId teamId, SshKeyId keyId)
    {
        Require(actorUserId, teamId, VesselPermissions.ServersRead);
        return ToSummary(Load(teamId, keyId));
    }

    public async Task<SshKeySummary> CreateAsync(
        UserId actorUserId,
        TeamId teamId,
        CreateSshKeyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Require(actorUserId, teamId, VesselPermissions.ServersWrite);
        var formatError = SshKeyFormats.ValidatePrivateKeyText(request.PrivateKey);
        if (formatError is not null) throw new DomainException(formatError);

        var privateKey = SshKeyFormats.NormalizePrivateKey(request.PrivateKey);
        var publicKeyLine = await keyTool.DerivePublicKeyAsync(privateKey, cancellationToken)
                            ?? throw new DomainException(
                                "The private key is invalid. Paste an unencrypted private key, not a public key or a certificate.");
        return await StoreAsync(actorUserId, teamId, request.Name, request.Description, privateKey, publicKeyLine,
            request.IsGitRelated, "imported", cancellationToken);
    }

    public async Task<SshKeySummary> GenerateAsync(
        UserId actorUserId,
        TeamId teamId,
        GenerateSshKeyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Require(actorUserId, teamId, VesselPermissions.ServersWrite);
        GeneratedSshKey generated = await keyTool.GenerateEd25519Async($"vessel-{teamId.Value:N}"[..20],
            cancellationToken);
        return await StoreAsync(actorUserId, teamId, request.Name, request.Description, generated.PrivateKey,
            generated.PublicKey, request.IsGitRelated, "generated", cancellationToken);
    }

    public async Task<SshKeySummary> UpdateAsync(
        UserId actorUserId,
        TeamId teamId,
        SshKeyId keyId,
        UpdateSshKeyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Require(actorUserId, teamId, VesselPermissions.ServersWrite);
        SshKey key = Load(teamId, keyId);
        EnsureUniqueName(teamId, request.Name, key.Id);
        key.Update(new ResourceName(request.Name), Describe(request.Description), request.IsGitRelated,
            timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        await AuditAsync(teamId, actorUserId, AuditActions.SshKeyUpdated, key, null, cancellationToken);
        return ToSummary(key);
    }

    public async Task DeleteAsync(
        UserId actorUserId,
        TeamId teamId,
        SshKeyId keyId,
        CancellationToken cancellationToken = default)
    {
        Require(actorUserId, teamId, VesselPermissions.ServersWrite);
        SshKey key = Load(teamId, keyId);
        var servers = dbContext.Servers.Count(server => server.SshKeyId == keyId);
        if (servers > 0)
            throw new SshKeyInUseException(
                $"The SSH key is used by {servers} server(s). Assign another key to those servers first.");

        dbContext.SshKeyRepository.Remove(key);
        // The encrypted private key goes with the key; nothing else references it.
        foreach (SecretValue value in dbContext.SecretValues
                     .Where(value => value.SecretReferenceId == key.PrivateKeyReferenceId).ToArray())
            dbContext.SecretValueRepository.Remove(value);
        SecretReference? reference =
            dbContext.SecretReferences.SingleOrDefault(candidate => candidate.Id == key.PrivateKeyReferenceId);
        if (reference is not null) dbContext.SecretReferenceRepository.Remove(reference);
        await dbContext.SaveChangesAsync(cancellationToken);
        await AuditAsync(teamId, actorUserId, AuditActions.SshKeyDeleted, key, null, cancellationToken);
    }

    /// <summary>
    ///     Starts a rotation of every server using <paramref name="oldKeyId" /> to a new key (generated when not given).
    ///     The work runs as a background job; servers that fail stay on the old key so the rotation can run again.
    /// </summary>
    public async Task<SshKeyRotationAccepted> RotateAsync(
        UserId actorUserId,
        TeamId teamId,
        SshKeyId oldKeyId,
        RotateSshKeyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Require(actorUserId, teamId, VesselPermissions.ServersWrite);
        SshKey oldKey = Load(teamId, oldKeyId);
        SshKey newKey;
        if (request.NewKeyId.HasValue)
        {
            newKey = Load(teamId, new SshKeyId(request.NewKeyId.Value));
            if (newKey.Id == oldKey.Id) throw new DomainException("Choose a different key to rotate to.");
        }
        else
        {
            GeneratedSshKey generated = await keyTool.GenerateEd25519Async($"vessel-{teamId.Value:N}"[..20],
                cancellationToken);
            var name = Truncate($"{oldKey.Name.Value} rotated {timeProvider.GetUtcNow():yyyy-MM-dd HHmmss}", 120);
            SshKeySummary created = await StoreAsync(actorUserId, teamId, name, oldKey.Description?.Value,
                generated.PrivateKey, generated.PublicKey, oldKey.IsGitRelated, "rotation", cancellationToken);
            newKey = Load(teamId, new SshKeyId(created.Id));
        }

        var serverCount = dbContext.Servers.Count(server =>
            server.TeamId == teamId && server.SshKeyId == oldKey.Id && server.ConnectionType == ServerConnectionType.Ssh);
        var jobId = jobs.Enqueue<RotateSshKeyJob>(job =>
            job.RunAsync(teamId.Value, oldKey.Id.Value, newKey.Id.Value, actorUserId.Value, CancellationToken.None));
        await AuditAsync(teamId, actorUserId, AuditActions.SshKeyRotationRequested, oldKey,
            new Dictionary<string, object?> { ["newKeyId"] = newKey.Id.Value, ["servers"] = serverCount },
            cancellationToken);
        return new SshKeyRotationAccepted(oldKey.Id.Value, newKey.Id.Value, serverCount, jobId);
    }

    private async Task<SshKeySummary> StoreAsync(
        UserId actorUserId,
        TeamId teamId,
        string name,
        string? description,
        string privateKey,
        string publicKeyLine,
        bool isGitRelated,
        string source,
        CancellationToken cancellationToken)
    {
        SshPublicKeyInfo publicKey;
        try
        {
            publicKey = SshKeyFormats.ParsePublicKey(publicKeyLine);
        }
        catch (FormatException)
        {
            throw new DomainException("The private key is invalid.");
        }

        var policyError = SshKeyFormats.ValidatePublicKeyPolicy(publicKey);
        if (policyError is not null) throw new DomainException(policyError);
        var resourceName = new ResourceName(name);
        EnsureUniqueName(teamId, resourceName.Value, null);
        var fingerprint = publicKey.Fingerprint;
        if (dbContext.SshKeys.Any(key => key.TeamId == teamId && key.Fingerprint == fingerprint))
            throw new DomainException("This key already exists in the team.");

        SecretReference secret = await secretVault.StoreAsync(teamId, SecretScope.Team,
            $"ssh-key:{fingerprint}", SshKeyFormats.NormalizePrivateKey(privateKey),
            new SecretPolicy(false, false, false), new SecretTarget(), cancellationToken);
        var key = SshKey.Create(teamId, resourceName, Describe(description), secret.Id,
            publicKey.Comment is null ? publicKey.KeyLine : $"{publicKey.KeyLine} {publicKey.Comment}",
            fingerprint, publicKey.Algorithm, publicKey.Bits, isGitRelated, timeProvider.GetUtcNow());
        await dbContext.SshKeyRepository.AddAsync(key, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await AuditAsync(teamId, actorUserId, AuditActions.SshKeyCreated, key,
            new Dictionary<string, object?> { ["source"] = source }, cancellationToken);
        return ToSummary(key);
    }

    private SshKey Load(TeamId teamId, SshKeyId keyId)
    {
        SshKey? key = dbContext.SshKeys.SingleOrDefault(candidate => candidate.Id == keyId);
        // Keys of another team look exactly like missing keys.
        if (key is null || key.TeamId != teamId) throw new KeyNotFoundException("SSH key was not found.");
        return key;
    }

    private void EnsureUniqueName(TeamId teamId, string name, SshKeyId? except)
    {
        var resourceName = new ResourceName(name);
        if (dbContext.SshKeys.Any(key => key.TeamId == teamId && key.Name == resourceName &&
                                         (except == null || key.Id != except.Value)))
            throw new DomainException("An SSH key with this name already exists.");
    }

    private void Require(UserId actorUserId, TeamId teamId, string permission)
    {
        if (!authorization.HasPermission(actorUserId, teamId, permission))
            throw new UnauthorizedAccessException($"Missing required permission '{permission}'.");
    }

    private SshKeySummary ToSummary(SshKey key)
    {
        var servers = dbContext.Servers.Count(server => server.SshKeyId == key.Id);
        return new SshKeySummary(key.Id.Value, key.Name.Value, key.Description?.Value, key.Algorithm, key.Bits,
            key.Fingerprint, key.PublicKey, key.IsGitRelated, servers, key.CreatedAt, key.UpdatedAt);
    }

    private Task AuditAsync(TeamId teamId, UserId actorUserId, string action, SshKey key,
        Dictionary<string, object?>? metadata, CancellationToken cancellationToken)
    {
        metadata ??= new Dictionary<string, object?>();
        metadata["fingerprint"] = key.Fingerprint;
        metadata["algorithm"] = key.Algorithm;
        return auditWriter.RecordAsync(teamId, actorUserId, action, new AuditTarget("ssh_key", key.Id.Value.ToString("D")),
            null, metadata, cancellationToken);
    }

    private static Description? Describe(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : new Description(value);
    }

    private static string Truncate(string value, int length)
    {
        return value.Length <= length ? value : value[..length];
    }
}
