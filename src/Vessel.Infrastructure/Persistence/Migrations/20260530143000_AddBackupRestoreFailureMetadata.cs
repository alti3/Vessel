using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Vessel.Infrastructure.Persistence;

#nullable disable

namespace Vessel.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
[DbContext(typeof(VesselDbContext))]
[Migration("20260530143000_AddBackupRestoreFailureMetadata")]
public partial class _20260530143000_AddBackupRestoreFailureMetadata : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "LastRestoreFailedAt",
            schema: "vessel",
            table: "backup_executions",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "LastRestoreFailureReason",
            schema: "vessel",
            table: "backup_executions",
            type: "character varying(1000)",
            maxLength: 1000,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "LastRestoreFailedAt",
            schema: "vessel",
            table: "backup_executions");

        migrationBuilder.DropColumn(
            name: "LastRestoreFailureReason",
            schema: "vessel",
            table: "backup_executions");
    }
}
