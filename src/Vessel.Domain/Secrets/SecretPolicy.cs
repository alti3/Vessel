namespace Vessel.Domain.Secrets;

public readonly record struct SecretPolicy(bool ShowOnce, bool AvailableAtBuild, bool AvailableAtRuntime)
{
    public static SecretPolicy Default => new(false, true, true);
}
