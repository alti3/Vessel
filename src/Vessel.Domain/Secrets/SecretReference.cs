using Vessel.Domain.Common;

namespace Vessel.Domain.Secrets;

public sealed class SecretReference : Entity<SecretReferenceId>
{
    private SecretReference()
    {
    }

    private SecretReference(
        SecretReferenceId id,
        TeamId teamId,
        SecretScope scope,
        string key,
        SecretProvider provider,
        string providerReference,
        SecretPolicy policy,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        TeamId = teamId;
        Scope = scope;
        Key = key;
        Provider = provider;
        ProviderReference = providerReference;
        Policy = policy;
    }

    public TeamId TeamId { get; private set; }

    public ProjectId? ProjectId { get; private set; }

    public EnvironmentId? EnvironmentId { get; private set; }

    public ServerId? ServerId { get; private set; }

    public ApplicationId? ApplicationId { get; private set; }

    public DatabaseResourceId? DatabaseResourceId { get; private set; }

    public SecretScope Scope { get; private set; }

    public string Key { get; private set; } = string.Empty;

    public SecretProvider Provider { get; private set; }

    public string ProviderReference { get; private set; } = string.Empty;

    public SecretPolicy Policy { get; private set; }

    public static SecretReference Create(
        TeamId teamId,
        SecretScope scope,
        string key,
        SecretProvider provider,
        string providerReference,
        SecretPolicy policy,
        DateTimeOffset now)
    {
        var normalizedKey = DomainValidation.Required(key, nameof(Key), 160);
        var normalizedReference = DomainValidation.Required(providerReference, nameof(ProviderReference), 512);

        return new SecretReference(SecretReferenceId.New(), teamId, scope, normalizedKey, provider, normalizedReference,
            policy, now);
    }
}
