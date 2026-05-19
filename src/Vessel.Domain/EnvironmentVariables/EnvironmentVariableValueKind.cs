namespace Vessel.Domain.EnvironmentVariables;

public enum EnvironmentVariableValueKind
{
    Plain = 0,
    Secret = 1,
    SharedReference = 2
}
