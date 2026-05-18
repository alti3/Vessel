using Microsoft.EntityFrameworkCore;
using Vessel.Domain.Teams;
using Vessel.Infrastructure.Persistence;
using AppEntity = Vessel.Domain.Applications.Application;

namespace Vessel.IntegrationTests.Persistence;

public sealed class VesselDbContextModelTests
{
    [Fact]
    public void Model_MapsCoreTablesToVesselSchema()
    {
        using VesselDbContext context = CreateContext();

        Assert.Equal("vessel", context.Model.FindEntityType(typeof(Team))?.GetSchema());
        Assert.Equal("teams", context.Model.FindEntityType(typeof(Team))?.GetTableName());
        Assert.Equal("applications", context.Model.FindEntityType(typeof(AppEntity))?.GetTableName());
    }

    [Fact]
    public void Model_KeepsDescriptionsNullable()
    {
        using VesselDbContext context = CreateContext();

        var isNullable = context.Model
            .FindEntityType(typeof(Team))
            ?.FindProperty(nameof(Team.Description))
            ?.IsNullable;

        Assert.True(isNullable);
    }

    [Fact]
    public void Model_ContainsInitialMigration()
    {
        using VesselDbContext context = CreateContext();

        Assert.Contains(context.Database.GetMigrations(),
            migration => migration.EndsWith("InitialDomainModel", StringComparison.Ordinal));
    }

    [Fact]
    public void CreateScript_ContainsCoreForeignKeysAndIndexes()
    {
        using VesselDbContext context = CreateContext();

        var script = context.Database.GenerateCreateScript();

        Assert.Contains("CREATE SCHEMA", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FK_applications_environments_EnvironmentId", script, StringComparison.Ordinal);
        Assert.Contains("IX_team_memberships_UserId", script, StringComparison.Ordinal);
    }

    private static VesselDbContext CreateContext()
    {
        DbContextOptions<VesselDbContext> options = new DbContextOptionsBuilder<VesselDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=vessel_tests;Username=vessel;Password=vessel")
            .Options;

        return new VesselDbContext(options);
    }
}
