using Vessel.Domain;
using Vessel.Domain.Secrets;

namespace Vessel.Application.Security;

public sealed class UnavailableSecretVault : ISecretVault
{
    public Task<SecretReference> StoreAsync(
        TeamId teamId,
        SecretScope scope,
        string key,
        string plaintext,
        SecretPolicy policy,
        SecretTarget target,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("Secret storage is unavailable because database persistence is disabled.");
    }

    public Task ReplaceAsync(
        SecretReferenceId secretReferenceId,
        string plaintext,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("Secret storage is unavailable because database persistence is disabled.");
    }

    public Task<string> RevealAsync(
        UserId actorUserId,
        TeamId teamId,
        SecretReferenceId secretReferenceId,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("Secret storage is unavailable because database persistence is disabled.");
    }
}
