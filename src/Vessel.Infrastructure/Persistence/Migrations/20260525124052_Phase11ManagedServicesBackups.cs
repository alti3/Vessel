using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vessel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _20260525124052_Phase11ManagedServicesBackups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ComposeSnapshotReference",
                schema: "vessel",
                table: "database_resources",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContainerName",
                schema: "vessel",
                table: "database_resources",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LifecycleState",
                schema: "vessel",
                table: "database_resources",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "NotProvisioned");

            migrationBuilder.CreateTable(
                name: "backup_schedules",
                schema: "vessel",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    DatabaseResourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    CronExpression = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    RetentionCount = table.Column<int>(type: "integer", nullable: false),
                    StorageKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    LastRunAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backup_schedules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_backup_schedules_database_resources_DatabaseResourceId",
                        column: x => x.DatabaseResourceId,
                        principalSchema: "vessel",
                        principalTable: "database_resources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_backup_schedules_teams_TeamId",
                        column: x => x.TeamId,
                        principalSchema: "vessel",
                        principalTable: "teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "service_resources",
                schema: "vessel",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    TemplateKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    TemplateVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ConfigurationJson = table.Column<string>(type: "jsonb", nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ComposeSnapshotReference = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_resources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_service_resources_environments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalSchema: "vessel",
                        principalTable: "environments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_service_resources_servers_ServerId",
                        column: x => x.ServerId,
                        principalSchema: "vessel",
                        principalTable: "servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_service_resources_teams_TeamId",
                        column: x => x.TeamId,
                        principalSchema: "vessel",
                        principalTable: "teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "backup_executions",
                schema: "vessel",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    DatabaseResourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScheduleId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StorageKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ArtifactBucket = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    ArtifactKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    ChecksumSha256 = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Protected = table.Column<bool>(type: "boolean", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backup_executions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_backup_executions_backup_schedules_ScheduleId",
                        column: x => x.ScheduleId,
                        principalSchema: "vessel",
                        principalTable: "backup_schedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_backup_executions_database_resources_DatabaseResourceId",
                        column: x => x.DatabaseResourceId,
                        principalSchema: "vessel",
                        principalTable: "database_resources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_backup_executions_teams_TeamId",
                        column: x => x.TeamId,
                        principalSchema: "vessel",
                        principalTable: "teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_backup_executions_DatabaseResourceId",
                schema: "vessel",
                table: "backup_executions",
                column: "DatabaseResourceId");

            migrationBuilder.CreateIndex(
                name: "IX_backup_executions_ScheduleId",
                schema: "vessel",
                table: "backup_executions",
                column: "ScheduleId");

            migrationBuilder.CreateIndex(
                name: "IX_backup_executions_TeamId_DatabaseResourceId_CreatedAt",
                schema: "vessel",
                table: "backup_executions",
                columns: new[] { "TeamId", "DatabaseResourceId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_schedules_DatabaseResourceId",
                schema: "vessel",
                table: "backup_schedules",
                column: "DatabaseResourceId");

            migrationBuilder.CreateIndex(
                name: "IX_backup_schedules_TeamId_DatabaseResourceId_Name",
                schema: "vessel",
                table: "backup_schedules",
                columns: new[] { "TeamId", "DatabaseResourceId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_service_resources_EnvironmentId",
                schema: "vessel",
                table: "service_resources",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_service_resources_ServerId",
                schema: "vessel",
                table: "service_resources",
                column: "ServerId");

            migrationBuilder.CreateIndex(
                name: "IX_service_resources_TeamId_Name",
                schema: "vessel",
                table: "service_resources",
                columns: new[] { "TeamId", "Name" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "backup_executions",
                schema: "vessel");

            migrationBuilder.DropTable(
                name: "service_resources",
                schema: "vessel");

            migrationBuilder.DropTable(
                name: "backup_schedules",
                schema: "vessel");

            migrationBuilder.DropColumn(
                name: "ComposeSnapshotReference",
                schema: "vessel",
                table: "database_resources");

            migrationBuilder.DropColumn(
                name: "ContainerName",
                schema: "vessel",
                table: "database_resources");

            migrationBuilder.DropColumn(
                name: "LifecycleState",
                schema: "vessel",
                table: "database_resources");

        }
    }
}
