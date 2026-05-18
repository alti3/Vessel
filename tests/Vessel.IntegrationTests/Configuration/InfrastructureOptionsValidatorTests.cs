using Vessel.Infrastructure.Configuration;

namespace Vessel.IntegrationTests.Configuration;

public sealed class InfrastructureOptionsValidatorTests
{
    [Fact]
    public void DatabaseOptionsRequireConnectionStringWhenEnabled()
    {
        var validator = new DatabaseOptionsValidator();

        var result = validator.Validate(null, new DatabaseOptions
        {
            Enabled = true
        });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("ConnectionString", StringComparison.Ordinal));
    }

    [Fact]
    public void RedisOptionsAllowDisabledConnectionString()
    {
        var validator = new RedisOptionsValidator();

        var result = validator.Validate(null, new RedisOptions
        {
            Enabled = false
        });

        Assert.False(result.Failed);
    }

    [Fact]
    public void HangfireOptionsRejectUnsupportedStorageProvider()
    {
        var validator = new HangfireStorageOptionsValidator();

        var result = validator.Validate(null, new HangfireStorageOptions
        {
            Enabled = true,
            StorageProvider = "Redis",
            ConnectionString = "Host=localhost;Database=vessel"
        });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("StorageProvider", StringComparison.Ordinal));
    }

    [Fact]
    public void ObjectStorageOptionsRequireAbsoluteEndpointWhenEnabled()
    {
        var validator = new ObjectStorageOptionsValidator();

        var result = validator.Validate(null, new ObjectStorageOptions
        {
            Enabled = true,
            Endpoint = "localhost:9000",
            BucketName = "vessel"
        });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("Endpoint", StringComparison.Ordinal));
    }
}
