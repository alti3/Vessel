using Vessel.Domain;
using Vessel.Domain.Secrets;
using ApplicationId = Vessel.Domain.ApplicationId;

namespace Vessel.Application.Security;

public interface ISecretVault
{
    Task<SecretReference> StoreAsync(
        TeamId teamId,
        SecretScope scope,
        string key,
        string plaintext,
        SecretPolicy policy,
        SecretTarget target,
        CancellationToken cancellationToken = default);

    Task ReplaceAsync(
        SecretReferenceId secretReferenceId,
        string plaintext,
        CancellationToken cancellationToken = default);

    Task<string> RevealAsync(
        UserId actorUserId,
        TeamId teamId,
        SecretReferenceId secretReferenceId,
        CancellationToken cancellationToken = default);

    Task<string> RevealForDeploymentAsync(
        TeamId teamId,
        SecretReferenceId secretReferenceId,
        CancellationToken cancellationToken = default);
}

public sealed record SecretTarget(
    ProjectId? ProjectId = null,
    EnvironmentId? EnvironmentId = null,
    ServerId? ServerId = null,
    ApplicationId? ApplicationId = null,
    DatabaseResourceId? DatabaseResourceId = null);
