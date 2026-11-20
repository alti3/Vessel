using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vessel.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class _20261120173645_Wave1SecurityAndCorrectness : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_environment_variables_TeamId_TargetType_ProjectId_Environme~",
            schema: "vessel",
            table: "environment_variables");

        migrationBuilder.AddColumn<Guid[]>(
            name: "VerifiedApplicationIds",
            schema: "vessel",
            table: "webhook_events",
            type: "uuid[]",
            nullable: false,
            defaultValue: new Guid[0]);

        migrationBuilder.AlterColumn<string>(
            name: "TwoFactorSecret",
            schema: "vessel",
            table: "users",
            type: "character varying(512)",
            maxLength: 512,
            nullable: true,
            oldClrType: typeof(string),
            oldType: "character varying(128)",
            oldMaxLength: 128,
            oldNullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "IsInstanceAdmin",
            schema: "vessel",
            table: "users",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        // SEC-03: host-local execution requires an instance administrator. Existing installs promote the
        // earliest registered user, matching the "first user becomes instance admin" rule for new installs.
        migrationBuilder.Sql("""
            UPDATE vessel.users SET "IsInstanceAdmin" = TRUE
            WHERE "Id" = (SELECT "Id" FROM vessel.users ORDER BY "CreatedAt", "Id" LIMIT 1);
            """);

        migrationBuilder.AddColumn<int>(
            name: "TimeoutSeconds",
            schema: "vessel",
            table: "backup_schedules",
            type: "integer",
            nullable: false,
            defaultValue: 3600);

        migrationBuilder.AddColumn<bool>(
            name: "IsSameRepository",
            schema: "vessel",
            table: "application_previews",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "SourceRepository",
            schema: "vessel",
            table: "application_previews",
            type: "character varying(512)",
            maxLength: 512,
            nullable: true);

        migrationBuilder.CreateTable(
            name: "database_restores",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                BackupExecutionId = table.Column<Guid>(type: "uuid", nullable: false),
                TargetDatabaseResourceId = table.Column<Guid>(type: "uuid", nullable: false),
                RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                DryRun = table.Column<bool>(type: "boolean", nullable: false),
                ReplaceExisting = table.Column<bool>(type: "boolean", nullable: false),
                KeepOwners = table.Column<bool>(type: "boolean", nullable: false),
                DetectedFormat = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                Log = table.Column<string>(type: "character varying(65536)", maxLength: 65536, nullable: false),
                FailureReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_database_restores", x => x.Id);
                table.ForeignKey(
                    name: "FK_database_restores_backup_executions_BackupExecutionId",
                    column: x => x.BackupExecutionId,
                    principalSchema: "vessel",
                    principalTable: "backup_executions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_database_restores_database_resources_TargetDatabaseResource~",
                    column: x => x.TargetDatabaseResourceId,
                    principalSchema: "vessel",
                    principalTable: "database_resources",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_database_restores_teams_TeamId",
                    column: x => x.TeamId,
                    principalSchema: "vessel",
                    principalTable: "teams",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_database_restores_users_RequestedByUserId",
                    column: x => x.RequestedByUserId,
                    principalSchema: "vessel",
                    principalTable: "users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "server_proxy_settings",
            schema: "vessel",
            columns: table => new
            {
                ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                ProxyType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Image = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                HttpPort = table.Column<int>(type: "integer", nullable: false),
                HttpsPort = table.Column<int>(type: "integer", nullable: false),
                DesiredState = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                LastAppliedStaticHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                LastAppliedDynamicHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                LastStartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastStoppedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastDynamicAppliedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_server_proxy_settings", x => x.ServerId);
                table.ForeignKey(
                    name: "FK_server_proxy_settings_servers_ServerId",
                    column: x => x.ServerId,
                    principalSchema: "vessel",
                    principalTable: "servers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        // The unique index now treats NULL scope columns as equal (NULLS NOT DISTINCT) and includes IsPreview.
        // Earlier rows could duplicate (TeamId, scope, Key) through NULL columns; keep the most recently updated
        // row of each duplicate group so the index can be created.
        migrationBuilder.Sql("""
            DELETE FROM vessel.environment_variables AS ev
            USING (
                SELECT "Id",
                       row_number() OVER (
                           PARTITION BY "TeamId", "TargetType", "ProjectId", "EnvironmentId", "ServerId",
                                        "ApplicationId", "DatabaseResourceId", "Key", "IsPreview"
                           ORDER BY "UpdatedAt" DESC, "CreatedAt" DESC, "Id") AS rn
                FROM vessel.environment_variables
            ) AS ranked
            WHERE ev."Id" = ranked."Id" AND ranked.rn > 1;
            """);

        migrationBuilder.CreateIndex(
            name: "IX_environment_variables_TeamId_TargetType_ProjectId_Environme~",
            schema: "vessel",
            table: "environment_variables",
            columns: new[] { "TeamId", "TargetType", "ProjectId", "EnvironmentId", "ServerId", "ApplicationId", "DatabaseResourceId", "Key", "IsPreview" },
            unique: true)
            .Annotation("Npgsql:NullsDistinct", false);

        migrationBuilder.CreateIndex(
            name: "IX_database_restores_BackupExecutionId",
            schema: "vessel",
            table: "database_restores",
            column: "BackupExecutionId");

        migrationBuilder.CreateIndex(
            name: "IX_database_restores_RequestedByUserId",
            schema: "vessel",
            table: "database_restores",
            column: "RequestedByUserId");

        migrationBuilder.CreateIndex(
            name: "IX_database_restores_TargetDatabaseResourceId_Status",
            schema: "vessel",
            table: "database_restores",
            columns: new[] { "TargetDatabaseResourceId", "Status" });

        migrationBuilder.CreateIndex(
            name: "IX_database_restores_TeamId_TargetDatabaseResourceId_CreatedAt",
            schema: "vessel",
            table: "database_restores",
            columns: new[] { "TeamId", "TargetDatabaseResourceId", "CreatedAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "database_restores",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "server_proxy_settings",
            schema: "vessel");

        migrationBuilder.DropIndex(
            name: "IX_environment_variables_TeamId_TargetType_ProjectId_Environme~",
            schema: "vessel",
            table: "environment_variables");

        migrationBuilder.DropColumn(
            name: "VerifiedApplicationIds",
            schema: "vessel",
            table: "webhook_events");

        migrationBuilder.DropColumn(
            name: "IsInstanceAdmin",
            schema: "vessel",
            table: "users");

        migrationBuilder.DropColumn(
            name: "TimeoutSeconds",
            schema: "vessel",
            table: "backup_schedules");

        migrationBuilder.DropColumn(
            name: "IsSameRepository",
            schema: "vessel",
            table: "application_previews");

        migrationBuilder.DropColumn(
            name: "SourceRepository",
            schema: "vessel",
            table: "application_previews");

        migrationBuilder.AlterColumn<string>(
            name: "TwoFactorSecret",
            schema: "vessel",
            table: "users",
            type: "character varying(128)",
            maxLength: 128,
            nullable: true,
            oldClrType: typeof(string),
            oldType: "character varying(512)",
            oldMaxLength: 512,
            oldNullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_environment_variables_TeamId_TargetType_ProjectId_Environme~",
            schema: "vessel",
            table: "environment_variables",
            columns: new[] { "TeamId", "TargetType", "ProjectId", "EnvironmentId", "ServerId", "ApplicationId", "DatabaseResourceId", "Key" },
            unique: true);
    }
}
