namespace Vessel.Infrastructure.Configuration;

public sealed class SecretStorageOptions
{
    public const string SectionName = "Secrets";

    public string? MasterKey { get; set; }

    public string KeyVersion { get; set; } = "v1";
}
