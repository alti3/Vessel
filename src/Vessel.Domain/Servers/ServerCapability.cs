namespace Vessel.Domain.Servers;

[Flags]
public enum ServerCapability
{
    None = 0,
    Containers = 1,
    Compose = 2,
    Swarm = 4,
    Volumes = 8,
    Networks = 16,
    BuildKit = 32
}
