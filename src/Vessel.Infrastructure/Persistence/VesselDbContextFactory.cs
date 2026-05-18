using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Vessel.Infrastructure.Persistence;

public sealed class VesselDbContextFactory : IDesignTimeDbContextFactory<VesselDbContext>
{
    public VesselDbContext CreateDbContext(string[] args)
    {
        DbContextOptions<VesselDbContext> options = new DbContextOptionsBuilder<VesselDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=vessel;Username=vessel;Password=vessel")
            .Options;

        return new VesselDbContext(options);
    }
}
