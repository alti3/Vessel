using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vessel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase7ResourceManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Labels",
                schema: "vessel",
                table: "servers",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsArchived",
                schema: "vessel",
                table: "projects",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "environment_variables",
                schema: "vessel",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    EnvironmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    ServerId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApplicationId = table.Column<Guid>(type: "uuid", nullable: true),
                    DatabaseResourceId = table.Column<Guid>(type: "uuid", nullable: true),
                    TargetType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Key = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    ValueKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PlainValue = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    SecretReferenceId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsBuildTime = table.Column<bool>(type: "boolean", nullable: false),
                    IsRuntime = table.Column<bool>(type: "boolean", nullable: false),
                    IsPreview = table.Column<bool>(type: "boolean", nullable: false),
                    IsLiteral = table.Column<bool>(type: "boolean", nullable: false),
                    IsMultiline = table.Column<bool>(type: "boolean", nullable: false),
                    Comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_environment_variables", x => x.Id);
                    table.ForeignKey(
                        name: "FK_environment_variables_secret_references_SecretReferenceId",
                        column: x => x.SecretReferenceId,
                        principalSchema: "vessel",
                        principalTable: "secret_references",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_environment_variables_teams_TeamId",
                        column: x => x.TeamId,
                        principalSchema: "vessel",
                        principalTable: "teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "registry_credentials",
                schema: "vessel",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Registry = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Username = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    PasswordReferenceId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_registry_credentials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_registry_credentials_secret_references_PasswordReferenceId",
                        column: x => x.PasswordReferenceId,
                        principalSchema: "vessel",
                        principalTable: "secret_references",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_registry_credentials_teams_TeamId",
                        column: x => x.TeamId,
                        principalSchema: "vessel",
                        principalTable: "teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "secret_values",
                schema: "vessel",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SecretReferenceId = table.Column<Guid>(type: "uuid", nullable: false),
                    CipherText = table.Column<string>(type: "character varying(12000)", maxLength: 12000, nullable: false),
                    Nonce = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Tag = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    KeyVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_secret_values", x => x.Id);
                    table.ForeignKey(
                        name: "FK_secret_values_secret_references_SecretReferenceId",
                        column: x => x.SecretReferenceId,
                        principalSchema: "vessel",
                        principalTable: "secret_references",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "server_status_snapshots",
                schema: "vessel",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CpuLoadPercent = table.Column<decimal>(type: "numeric(7,2)", precision: 7, scale: 2, nullable: true),
                    MemoryUsedBytes = table.Column<long>(type: "bigint", nullable: true),
                    DiskUsedBytes = table.Column<long>(type: "bigint", nullable: true),
                    RunningContainers = table.Column<int>(type: "integer", nullable: false),
                    ProxyHealthy = table.Column<bool>(type: "boolean", nullable: false),
                    CertificatesHealthy = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_server_status_snapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_server_status_snapshots_servers_ServerId",
                        column: x => x.ServerId,
                        principalSchema: "vessel",
                        principalTable: "servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_environment_variables_SecretReferenceId",
                schema: "vessel",
                table: "environment_variables",
                column: "SecretReferenceId");

            migrationBuilder.CreateIndex(
                name: "IX_environment_variables_TeamId_TargetType_ProjectId_Environme~",
                schema: "vessel",
                table: "environment_variables",
                columns: new[] { "TeamId", "TargetType", "ProjectId", "EnvironmentId", "ServerId", "ApplicationId", "DatabaseResourceId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_registry_credentials_PasswordReferenceId",
                schema: "vessel",
                table: "registry_credentials",
                column: "PasswordReferenceId");

            migrationBuilder.CreateIndex(
                name: "IX_registry_credentials_TeamId_Registry_Username",
                schema: "vessel",
                table: "registry_credentials",
                columns: new[] { "TeamId", "Registry", "Username" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_secret_values_SecretReferenceId",
                schema: "vessel",
                table: "secret_values",
                column: "SecretReferenceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_server_status_snapshots_ServerId_CreatedAt",
                schema: "vessel",
                table: "server_status_snapshots",
                columns: new[] { "ServerId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "environment_variables",
                schema: "vessel");

            migrationBuilder.DropTable(
                name: "registry_credentials",
                schema: "vessel");

            migrationBuilder.DropTable(
                name: "secret_values",
                schema: "vessel");

            migrationBuilder.DropTable(
                name: "server_status_snapshots",
                schema: "vessel");

            migrationBuilder.DropColumn(
                name: "Labels",
                schema: "vessel",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "IsArchived",
                schema: "vessel",
                table: "projects");
        }
    }
}
