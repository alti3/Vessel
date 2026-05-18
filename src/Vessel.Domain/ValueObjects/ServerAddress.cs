using Vessel.Domain.Common;

namespace Vessel.Domain.ValueObjects;

public readonly record struct ServerAddress
{
    public const int MaxLength = 255;

    public ServerAddress(string host, PortNumber port, string? user = null)
    {
        Host = DomainValidation.Required(host, nameof(Host), MaxLength).ToLowerInvariant();
        Port = port;
        User = DomainValidation.Optional(user, nameof(User), 120);
    }

    public string Host { get; }

    public PortNumber Port { get; }

    public string? User { get; }

    public override string ToString()
    {
        return User is null ? $"{Host}:{Port.Value}" : $"{User}@{Host}:{Port.Value}";
    }
}
