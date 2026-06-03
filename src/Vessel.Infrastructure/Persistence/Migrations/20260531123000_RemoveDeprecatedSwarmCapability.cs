using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vessel.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
[DbContext(typeof(VesselDbContext))]
[Migration("20260531123000_RemoveDeprecatedSwarmCapability")]
public partial class _20260531123000_RemoveDeprecatedSwarmCapability : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            UPDATE vessel.servers
            SET "Capabilities" = "Capabilities" & ~4
            WHERE ("Capabilities" & 4) = 4;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        throw new NotSupportedException(
            "This migration is not reversible: clearing the deprecated swarm capability bit is lossy.");
    }
}
