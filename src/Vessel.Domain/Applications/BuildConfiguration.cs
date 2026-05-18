using Vessel.Domain.ValueObjects;

namespace Vessel.Domain.Applications;

public readonly record struct BuildConfiguration(
    ApplicationBuildPack BuildPack,
    string BaseDirectory,
    string? DockerfilePath,
    string? InstallCommand,
    string? BuildCommand,
    string? StartCommand,
    ImageTag? ImageTag)
{
    public static BuildConfiguration Default(ApplicationBuildPack buildPack)
    {
        return new BuildConfiguration(buildPack, "/", null, null, null, null, null);
    }
}
