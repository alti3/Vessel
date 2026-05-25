using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vessel.Application.Security;
using Vessel.Infrastructure.Extensions;

namespace Vessel.IntegrationTests.Security;

public sealed class Argon2PasswordHasherTests
{
    [Fact]
    public void HashPasswordUsesArgon2idAndVerifiesWithStoredParameters()
    {
        using ServiceProvider provider = new ServiceCollection()
            .AddLogging()
            .AddVesselInfrastructure(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Vessel:Argon2:DegreeOfParallelism"] = "1",
                    ["Vessel:Argon2:Iterations"] = "1",
                    ["Vessel:Argon2:MemorySize"] = "8192"
                })
                .Build())
            .BuildServiceProvider();

        IPasswordHasher hasher = provider.GetRequiredService<IPasswordHasher>();
        var hash = hasher.HashPassword("CorrectHorseBatteryStaple1");

        Assert.StartsWith("$argon2id$v=19$", hash, StringComparison.Ordinal);
        Assert.True(hasher.VerifyPassword(hash, "CorrectHorseBatteryStaple1"));
        Assert.False(hasher.VerifyPassword(hash, "wrong-password"));
    }
}
