using Vessel.Domain.Common;
using Vessel.Domain.ValueObjects;

namespace Vessel.Domain.Registries;

public sealed class RegistryCredential : Entity<RegistryCredentialId>
{
    private RegistryCredential()
    {
    }

    private RegistryCredential(
        RegistryCredentialId id,
        TeamId teamId,
        ResourceName name,
        string registry,
        string username,
        SecretReferenceId passwordReferenceId,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        TeamId = teamId;
        Name = name;
        Registry = DomainValidation.Required(registry, nameof(Registry), 255).ToLowerInvariant();
        Username = DomainValidation.Required(username, nameof(Username), 255);
        PasswordReferenceId = passwordReferenceId;
    }

    public TeamId TeamId { get; private set; }

    public ResourceName Name { get; private set; }

    public string Registry { get; private set; } = string.Empty;

    public string Username { get; private set; } = string.Empty;

    public SecretReferenceId PasswordReferenceId { get; private set; }

    public static RegistryCredential Create(
        TeamId teamId,
        ResourceName name,
        string registry,
        string username,
        SecretReferenceId passwordReferenceId,
        DateTimeOffset now)
    {
        return new RegistryCredential(RegistryCredentialId.New(), teamId, name, registry, username,
            passwordReferenceId, now);
    }
}
