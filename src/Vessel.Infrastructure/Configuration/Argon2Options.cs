namespace Vessel.Infrastructure.Configuration;

public sealed class Argon2Options
{
    public const string SectionName = "Vessel:Argon2";

    public int DegreeOfParallelism { get; set; } = 2;

    public int Iterations { get; set; } = 3;

    public int MemorySize { get; set; } = 65536;
}
