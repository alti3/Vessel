namespace Vessel.Domain.Access;

/// <summary>Where an <see cref="AccessGrant" /> applies. A grant on a parent applies to all of its children.</summary>
public enum AccessScope
{
    Team = 0,
    Project = 1,
    Environment = 2,
    Resource = 3
}
