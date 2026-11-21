using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vessel.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class _20261121113532_Wave2bParity : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_audit_logs_TeamId_CreatedAt",
            schema: "vessel",
            table: "audit_logs");

        migrationBuilder.AddColumn<Guid>(
            name: "CurrentTeamId",
            schema: "vessel",
            table: "users",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "GeneratedSecretsReferenceId",
            schema: "vessel",
            table: "service_resources",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "ConsecutiveFailedHealthChecks",
            schema: "vessel",
            table: "servers",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "ExpirationWarningSentAt",
            schema: "vessel",
            table: "personal_access_tokens",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "PreserveComposeNull",
            schema: "vessel",
            table: "environment_variables",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<int>(
            name: "SortOrder",
            schema: "vessel",
            table: "environment_variables",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<bool>(
            name: "UsesLegacyEscaping",
            schema: "vessel",
            table: "environment_variables",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AlterColumn<Guid>(
            name: "BackupExecutionId",
            schema: "vessel",
            table: "database_restores",
            type: "uuid",
            nullable: true,
            oldClrType: typeof(Guid),
            oldType: "uuid");

        migrationBuilder.AddColumn<string>(
            name: "ImportDatabaseName",
            schema: "vessel",
            table: "database_restores",
            type: "character varying(63)",
            maxLength: 63,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "ImportDestinationId",
            schema: "vessel",
            table: "database_restores",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ImportReference",
            schema: "vessel",
            table: "database_restores",
            type: "character varying(1024)",
            maxLength: 1024,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ImportSource",
            schema: "vessel",
            table: "database_restores",
            type: "character varying(32)",
            maxLength: 32,
            nullable: false,
            defaultValue: "None");

        migrationBuilder.AddColumn<string>(
            name: "MissedRunPolicy",
            schema: "vessel",
            table: "backup_schedules",
            type: "character varying(16)",
            maxLength: 16,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "TimeZone",
            schema: "vessel",
            table: "backup_schedules",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ActorName",
            schema: "vessel",
            table: "audit_logs",
            type: "character varying(120)",
            maxLength: 120,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "ActorTokenId",
            schema: "vessel",
            table: "audit_logs",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ActorTokenName",
            schema: "vessel",
            table: "audit_logs",
            type: "character varying(255)",
            maxLength: 255,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ActorType",
            schema: "vessel",
            table: "audit_logs",
            type: "character varying(32)",
            maxLength: 32,
            nullable: false,
            // Overwritten below for rows with an actor user.
            defaultValue: "system");

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "AnonymizedAt",
            schema: "vessel",
            table: "audit_logs",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ChangesJson",
            schema: "vessel",
            table: "audit_logs",
            type: "jsonb",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Description",
            schema: "vessel",
            table: "audit_logs",
            type: "character varying(500)",
            maxLength: 500,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "IpAddress",
            schema: "vessel",
            table: "audit_logs",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Level",
            schema: "vessel",
            table: "audit_logs",
            type: "character varying(16)",
            maxLength: 16,
            nullable: false,
            // Rows recorded before IAM-08 had no level.
            defaultValue: "Info");

        migrationBuilder.AddColumn<string>(
            name: "ResourceName",
            schema: "vessel",
            table: "audit_logs",
            type: "character varying(255)",
            maxLength: 255,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Source",
            schema: "vessel",
            table: "audit_logs",
            type: "character varying(32)",
            maxLength: 32,
            nullable: false,
            // Rows recorded before IAM-08 had no recorded source.
            defaultValue: "system");

        migrationBuilder.AddColumn<string>(
            name: "UserAgent",
            schema: "vessel",
            table: "audit_logs",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AlterColumn<string>(
            name: "BuildConfiguration",
            schema: "vessel",
            table: "applications",
            type: "character varying(8192)",
            maxLength: 8192,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "character varying(2048)",
            oldMaxLength: 2048);

        migrationBuilder.AddColumn<Guid>(
            name: "SourceId",
            schema: "vessel",
            table: "applications",
            type: "uuid",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "auth_providers",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                ClientId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                ProtectedClientSecret = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                Authority = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                Scopes = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                Enabled = table.Column<bool>(type: "boolean", nullable: false),
                AllowRegistration = table.Column<bool>(type: "boolean", nullable: false),
                AutoJoinTeamId = table.Column<Guid>(type: "uuid", nullable: true),
                AllowedDomains = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                RequireVerifiedEmail = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_auth_providers", x => x.Id);
                table.ForeignKey(
                    name: "FK_auth_providers_teams_AutoJoinTeamId",
                    column: x => x.AutoJoinTeamId,
                    principalSchema: "vessel",
                    principalTable: "teams",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "container_metric_samples",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                CollectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ContainerId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                ContainerName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                ResourceKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                ResourceId = table.Column<Guid>(type: "uuid", nullable: true),
                CpuPercent = table.Column<decimal>(type: "numeric(9,2)", precision: 9, scale: 2, nullable: true),
                MemoryUsedBytes = table.Column<long>(type: "bigint", nullable: true),
                MemoryLimitBytes = table.Column<long>(type: "bigint", nullable: true),
                NetworkRxBytes = table.Column<long>(type: "bigint", nullable: true),
                NetworkTxBytes = table.Column<long>(type: "bigint", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_container_metric_samples", x => x.Id);
                table.ForeignKey(
                    name: "FK_container_metric_samples_servers_ServerId",
                    column: x => x.ServerId,
                    principalSchema: "vessel",
                    principalTable: "servers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "control_plane_backups",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Trigger = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                RelativePath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                ChecksumSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                FailureReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                WorkerHost = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_control_plane_backups", x => x.Id);
                table.ForeignKey(
                    name: "FK_control_plane_backups_users_RequestedByUserId",
                    column: x => x.RequestedByUserId,
                    principalSchema: "vessel",
                    principalTable: "users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "database_import_uploads",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                DatabaseResourceId = table.Column<Guid>(type: "uuid", nullable: false),
                CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                DeclaredSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                ReceivedBytes = table.Column<long>(type: "bigint", nullable: false),
                CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_database_import_uploads", x => x.Id);
                table.ForeignKey(
                    name: "FK_database_import_uploads_database_resources_DatabaseResource~",
                    column: x => x.DatabaseResourceId,
                    principalSchema: "vessel",
                    principalTable: "database_resources",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_database_import_uploads_teams_TeamId",
                    column: x => x.TeamId,
                    principalSchema: "vessel",
                    principalTable: "teams",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_database_import_uploads_users_CreatedByUserId",
                    column: x => x.CreatedByUserId,
                    principalSchema: "vessel",
                    principalTable: "users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "docker_cleanup_executions",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                Trigger = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                ReclaimedBytes = table.Column<long>(type: "bigint", nullable: false),
                RemovedContainers = table.Column<int>(type: "integer", nullable: false),
                RemovedImages = table.Column<int>(type: "integer", nullable: false),
                BuildCachePruned = table.Column<bool>(type: "boolean", nullable: false),
                Log = table.Column<string>(type: "character varying(32768)", maxLength: 32768, nullable: false),
                Message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_docker_cleanup_executions", x => x.Id);
                table.ForeignKey(
                    name: "FK_docker_cleanup_executions_servers_ServerId",
                    column: x => x.ServerId,
                    principalSchema: "vessel",
                    principalTable: "servers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "file_mounts",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                OwnerKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                Path = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                MountPath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                IsDirectory = table.Column<bool>(type: "boolean", nullable: false),
                IsHostFile = table.Column<bool>(type: "boolean", nullable: false),
                ServiceName = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: true),
                ProtectedContent = table.Column<string>(type: "text", nullable: true),
                ContentLength = table.Column<long>(type: "bigint", nullable: false),
                ContentPendingWrite = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_file_mounts", x => x.Id);
                table.ForeignKey(
                    name: "FK_file_mounts_teams_TeamId",
                    column: x => x.TeamId,
                    principalSchema: "vessel",
                    principalTable: "teams",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "git_known_hosts",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                Host = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                Port = table.Column<int>(type: "integer", nullable: false),
                KeyType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Key = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: false),
                Origin = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_git_known_hosts", x => x.Id);
                table.ForeignKey(
                    name: "FK_git_known_hosts_teams_TeamId",
                    column: x => x.TeamId,
                    principalSchema: "vessel",
                    principalTable: "teams",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "git_sources",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                IsSystemWide = table.Column<bool>(type: "boolean", nullable: false),
                HtmlUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                ApiUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                Organization = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                CustomSshUser = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                CustomSshPort = table.Column<int>(type: "integer", nullable: true),
                AppId = table.Column<long>(type: "bigint", nullable: true),
                InstallationId = table.Column<long>(type: "bigint", nullable: true),
                ClientId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                Username = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                SshKeyId = table.Column<Guid>(type: "uuid", nullable: true),
                PrivateKeyReferenceId = table.Column<Guid>(type: "uuid", nullable: true),
                ClientSecretReferenceId = table.Column<Guid>(type: "uuid", nullable: true),
                WebhookSecretReferenceId = table.Column<Guid>(type: "uuid", nullable: true),
                AccessTokenReferenceId = table.Column<Guid>(type: "uuid", nullable: true),
                RefreshTokenReferenceId = table.Column<Guid>(type: "uuid", nullable: true),
                AccessTokenExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_git_sources", x => x.Id);
                table.ForeignKey(
                    name: "FK_git_sources_ssh_keys_SshKeyId",
                    column: x => x.SshKeyId,
                    principalSchema: "vessel",
                    principalTable: "ssh_keys",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_git_sources_teams_TeamId",
                    column: x => x.TeamId,
                    principalSchema: "vessel",
                    principalTable: "teams",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "persistent_volumes",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                OwnerKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                MountPath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                ServiceName = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_persistent_volumes", x => x.Id);
                table.ForeignKey(
                    name: "FK_persistent_volumes_teams_TeamId",
                    column: x => x.TeamId,
                    principalSchema: "vessel",
                    principalTable: "teams",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "resource_runtime_statuses",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                ResourceId = table.Column<Guid>(type: "uuid", nullable: false),
                TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                Health = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                ContainerCount = table.Column<int>(type: "integer", nullable: false),
                RunningContainerCount = table.Column<int>(type: "integer", nullable: false),
                RestartCount = table.Column<int>(type: "integer", nullable: false),
                MaxRestartCount = table.Column<int>(type: "integer", nullable: false),
                RestartLimitReached = table.Column<bool>(type: "boolean", nullable: false),
                LastRestartAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                StateChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                LastCheckedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastSeenRunningAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                ExpectedStopUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_resource_runtime_statuses", x => x.Id);
                table.ForeignKey(
                    name: "FK_resource_runtime_statuses_servers_ServerId",
                    column: x => x.ServerId,
                    principalSchema: "vessel",
                    principalTable: "servers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "scheduled_job_runs",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                ScheduleId = table.Column<Guid>(type: "uuid", nullable: false),
                TeamId = table.Column<Guid>(type: "uuid", nullable: true),
                ScheduledFor = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Trigger = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                TimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                EnqueuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                EnqueueCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                ClaimToken = table.Column<Guid>(type: "uuid", nullable: true),
                WorkerHost = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                ExecutionId = table.Column<Guid>(type: "uuid", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_scheduled_job_runs", x => x.Id);
                table.ForeignKey(
                    name: "FK_scheduled_job_runs_teams_TeamId",
                    column: x => x.TeamId,
                    principalSchema: "vessel",
                    principalTable: "teams",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "scheduled_tasks",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                OwnerKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                Command = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                CronExpression = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                TimeZone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                ContainerName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                TimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                UseShell = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                Enabled = table.Column<bool>(type: "boolean", nullable: false),
                MissedRunPolicy = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                FailingSince = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                DisabledReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                LastRunAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_scheduled_tasks", x => x.Id);
                table.ForeignKey(
                    name: "FK_scheduled_tasks_teams_TeamId",
                    column: x => x.TeamId,
                    principalSchema: "vessel",
                    principalTable: "teams",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "scheduled_volume_backups",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                OwnerKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                TargetKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                TargetId = table.Column<Guid>(type: "uuid", nullable: false),
                CronExpression = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                Enabled = table.Column<bool>(type: "boolean", nullable: false),
                StopDuringBackup = table.Column<bool>(type: "boolean", nullable: false),
                DisableLocalCopy = table.Column<bool>(type: "boolean", nullable: false),
                TimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                LocalRetentionCount = table.Column<int>(type: "integer", nullable: false),
                LocalRetentionDays = table.Column<int>(type: "integer", nullable: false),
                LocalRetentionMaxBytes = table.Column<long>(type: "bigint", nullable: false),
                RemoteRetentionCount = table.Column<int>(type: "integer", nullable: false),
                RemoteRetentionDays = table.Column<int>(type: "integer", nullable: false),
                RemoteRetentionMaxBytes = table.Column<long>(type: "bigint", nullable: false),
                MissingAlertDays = table.Column<int>(type: "integer", nullable: false),
                MissingAlertSentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastRunAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastSucceededAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                FailingSince = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastFailureNotifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_scheduled_volume_backups", x => x.Id);
                table.ForeignKey(
                    name: "FK_scheduled_volume_backups_teams_TeamId",
                    column: x => x.TeamId,
                    principalSchema: "vessel",
                    principalTable: "teams",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "server_metric_samples",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                Resolution = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                BucketStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                SampleCount = table.Column<int>(type: "integer", nullable: false),
                CpuPercent = table.Column<decimal>(type: "numeric(7,2)", precision: 7, scale: 2, nullable: true),
                CpuPercentMax = table.Column<decimal>(type: "numeric(7,2)", precision: 7, scale: 2, nullable: true),
                CpuCount = table.Column<int>(type: "integer", nullable: true),
                Load1 = table.Column<decimal>(type: "numeric(9,2)", precision: 9, scale: 2, nullable: true),
                Load5 = table.Column<decimal>(type: "numeric(9,2)", precision: 9, scale: 2, nullable: true),
                Load15 = table.Column<decimal>(type: "numeric(9,2)", precision: 9, scale: 2, nullable: true),
                MemoryTotalBytes = table.Column<long>(type: "bigint", nullable: true),
                MemoryUsedBytes = table.Column<long>(type: "bigint", nullable: true),
                DiskMount = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                DiskTotalBytes = table.Column<long>(type: "bigint", nullable: true),
                DiskUsedBytes = table.Column<long>(type: "bigint", nullable: true),
                NetworkRxBytesPerSecond = table.Column<long>(type: "bigint", nullable: true),
                NetworkTxBytesPerSecond = table.Column<long>(type: "bigint", nullable: true),
                ContainersRunning = table.Column<int>(type: "integer", nullable: false),
                ContainersTotal = table.Column<int>(type: "integer", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_server_metric_samples", x => x.Id);
                table.ForeignKey(
                    name: "FK_server_metric_samples_servers_ServerId",
                    column: x => x.ServerId,
                    principalSchema: "vessel",
                    principalTable: "servers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "server_monitoring_settings",
            schema: "vessel",
            columns: table => new
            {
                ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                MetricsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                MetricsIntervalSeconds = table.Column<int>(type: "integer", nullable: false),
                DiskAlertThresholdPercent = table.Column<int>(type: "integer", nullable: false),
                DiskAlertIntervalHours = table.Column<int>(type: "integer", nullable: false),
                DiskAlertArmed = table.Column<bool>(type: "boolean", nullable: false),
                LastDiskAlertAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                docker_cleanup_schedule_enabled = table.Column<bool>(type: "boolean", nullable: false),
                docker_cleanup_cron = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                docker_cleanup_threshold_percent = table.Column<int>(type: "integer", nullable: false),
                docker_cleanup_force_scheduled = table.Column<bool>(type: "boolean", nullable: false),
                docker_cleanup_delete_unused_volumes = table.Column<bool>(type: "boolean", nullable: false),
                docker_cleanup_delete_unused_networks = table.Column<bool>(type: "boolean", nullable: false),
                LastDockerCleanupAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastMetricsCollectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastMetricsFailureAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_server_monitoring_settings", x => x.ServerId);
                table.ForeignKey(
                    name: "FK_server_monitoring_settings_servers_ServerId",
                    column: x => x.ServerId,
                    principalSchema: "vessel",
                    principalTable: "servers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "user_sessions",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                ActiveTeamId = table.Column<Guid>(type: "uuid", nullable: false),
                AuthenticationMethod = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                IpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                UserAgent = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                RevocationReason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_user_sessions", x => x.Id);
                table.ForeignKey(
                    name: "FK_user_sessions_teams_ActiveTeamId",
                    column: x => x.ActiveTeamId,
                    principalSchema: "vessel",
                    principalTable: "teams",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_user_sessions_users_UserId",
                    column: x => x.UserId,
                    principalSchema: "vessel",
                    principalTable: "users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "oauth_identities",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                ProviderId = table.Column<Guid>(type: "uuid", nullable: false),
                Issuer = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                Subject = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                LastLoginAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_oauth_identities", x => x.Id);
                table.ForeignKey(
                    name: "FK_oauth_identities_auth_providers_ProviderId",
                    column: x => x.ProviderId,
                    principalSchema: "vessel",
                    principalTable: "auth_providers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_oauth_identities_users_UserId",
                    column: x => x.UserId,
                    principalSchema: "vessel",
                    principalTable: "users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "upgrade_runs",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                FromVersion = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                TargetVersion = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Channel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                StartedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                WorkerHost = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                PreUpdateBackupId = table.Column<Guid>(type: "uuid", nullable: true),
                HeartbeatAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_upgrade_runs", x => x.Id);
                table.ForeignKey(
                    name: "FK_upgrade_runs_control_plane_backups_PreUpdateBackupId",
                    column: x => x.PreUpdateBackupId,
                    principalSchema: "vessel",
                    principalTable: "control_plane_backups",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_upgrade_runs_users_StartedByUserId",
                    column: x => x.StartedByUserId,
                    principalSchema: "vessel",
                    principalTable: "users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "scheduled_task_executions",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ScheduledTaskId = table.Column<Guid>(type: "uuid", nullable: false),
                TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                RunId = table.Column<Guid>(type: "uuid", nullable: true),
                Trigger = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                ContainerName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                DurationSeconds = table.Column<double>(type: "double precision", nullable: true),
                ExitCode = table.Column<int>(type: "integer", nullable: true),
                Output = table.Column<string>(type: "character varying(262171)", maxLength: 262171, nullable: false),
                OutputTruncated = table.Column<bool>(type: "boolean", nullable: false),
                Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                WorkerHost = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_scheduled_task_executions", x => x.Id);
                table.ForeignKey(
                    name: "FK_scheduled_task_executions_scheduled_tasks_ScheduledTaskId",
                    column: x => x.ScheduledTaskId,
                    principalSchema: "vessel",
                    principalTable: "scheduled_tasks",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "scheduled_volume_backup_destinations",
            schema: "vessel",
            columns: table => new
            {
                ScheduledVolumeBackupId = table.Column<Guid>(type: "uuid", nullable: false),
                BackupDestinationId = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_scheduled_volume_backup_destinations", x => new { x.ScheduledVolumeBackupId, x.BackupDestinationId });
                table.ForeignKey(
                    name: "FK_scheduled_volume_backup_destinations_backup_destinations_Ba~",
                    column: x => x.BackupDestinationId,
                    principalSchema: "vessel",
                    principalTable: "backup_destinations",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_scheduled_volume_backup_destinations_scheduled_volume_backu~",
                    column: x => x.ScheduledVolumeBackupId,
                    principalSchema: "vessel",
                    principalTable: "scheduled_volume_backups",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "volume_backup_executions",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                ScheduleId = table.Column<Guid>(type: "uuid", nullable: false),
                Manual = table.Column<bool>(type: "boolean", nullable: false),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                ArtifactBucket = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                ArtifactKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                SizeBytes = table.Column<long>(type: "bigint", nullable: true),
                ChecksumSha256 = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                FailureReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                Warning = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Protected = table.Column<bool>(type: "boolean", nullable: false),
                StoppedContainerIds = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                StopRecoveryPending = table.Column<bool>(type: "boolean", nullable: false),
                RecoveryAttempts = table.Column<int>(type: "integer", nullable: false),
                RecoveryLastAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                RecoveryError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                RecoveryNeedsAttention = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_volume_backup_executions", x => x.Id);
                table.ForeignKey(
                    name: "FK_volume_backup_executions_scheduled_volume_backups_ScheduleId",
                    column: x => x.ScheduleId,
                    principalSchema: "vessel",
                    principalTable: "scheduled_volume_backups",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_volume_backup_executions_teams_TeamId",
                    column: x => x.TeamId,
                    principalSchema: "vessel",
                    principalTable: "teams",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "volume_backup_replicas",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                VolumeBackupExecutionId = table.Column<Guid>(type: "uuid", nullable: false),
                BackupDestinationId = table.Column<Guid>(type: "uuid", nullable: true),
                Bucket = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                Uploaded = table.Column<bool>(type: "boolean", nullable: false),
                Message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_volume_backup_replicas", x => x.Id);
                table.ForeignKey(
                    name: "FK_volume_backup_replicas_backup_destinations_BackupDestinatio~",
                    column: x => x.BackupDestinationId,
                    principalSchema: "vessel",
                    principalTable: "backup_destinations",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_volume_backup_replicas_volume_backup_executions_VolumeBacku~",
                    column: x => x.VolumeBackupExecutionId,
                    principalSchema: "vessel",
                    principalTable: "volume_backup_executions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "volume_backup_restores",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                VolumeBackupExecutionId = table.Column<Guid>(type: "uuid", nullable: false),
                RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                ReplaceExisting = table.Column<bool>(type: "boolean", nullable: false),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Log = table.Column<string>(type: "character varying(65536)", maxLength: 65536, nullable: false),
                FailureReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                StoppedContainerIds = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                StopRecoveryPending = table.Column<bool>(type: "boolean", nullable: false),
                RecoveryAttempts = table.Column<int>(type: "integer", nullable: false),
                RecoveryError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                RecoveryNeedsAttention = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_volume_backup_restores", x => x.Id);
                table.ForeignKey(
                    name: "FK_volume_backup_restores_teams_TeamId",
                    column: x => x.TeamId,
                    principalSchema: "vessel",
                    principalTable: "teams",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_volume_backup_restores_users_RequestedByUserId",
                    column: x => x.RequestedByUserId,
                    principalSchema: "vessel",
                    principalTable: "users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_volume_backup_restores_volume_backup_executions_VolumeBacku~",
                    column: x => x.VolumeBackupExecutionId,
                    principalSchema: "vessel",
                    principalTable: "volume_backup_executions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_users_CurrentTeamId",
            schema: "vessel",
            table: "users",
            column: "CurrentTeamId");

        migrationBuilder.CreateIndex(
            name: "IX_personal_access_tokens_ExpiresAt_ExpirationWarningSentAt",
            schema: "vessel",
            table: "personal_access_tokens",
            columns: new[] { "ExpiresAt", "ExpirationWarningSentAt" });

        migrationBuilder.CreateIndex(
            name: "IX_database_restores_ImportDestinationId",
            schema: "vessel",
            table: "database_restores",
            column: "ImportDestinationId");

        migrationBuilder.CreateIndex(
            name: "IX_audit_logs_CreatedAt",
            schema: "vessel",
            table: "audit_logs",
            column: "CreatedAt");

        migrationBuilder.CreateIndex(
            name: "IX_audit_logs_TeamId_Action_CreatedAt",
            schema: "vessel",
            table: "audit_logs",
            columns: new[] { "TeamId", "Action", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_audit_logs_TeamId_CreatedAt_Id",
            schema: "vessel",
            table: "audit_logs",
            columns: new[] { "TeamId", "CreatedAt", "Id" });

        migrationBuilder.CreateIndex(
            name: "IX_audit_logs_TeamId_Source_CreatedAt",
            schema: "vessel",
            table: "audit_logs",
            columns: new[] { "TeamId", "Source", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_applications_SourceId",
            schema: "vessel",
            table: "applications",
            column: "SourceId");

        migrationBuilder.CreateIndex(
            name: "IX_auth_providers_AutoJoinTeamId",
            schema: "vessel",
            table: "auth_providers",
            column: "AutoJoinTeamId");

        migrationBuilder.CreateIndex(
            name: "IX_container_metric_samples_CollectedAt",
            schema: "vessel",
            table: "container_metric_samples",
            column: "CollectedAt");

        migrationBuilder.CreateIndex(
            name: "IX_container_metric_samples_ServerId_CollectedAt",
            schema: "vessel",
            table: "container_metric_samples",
            columns: new[] { "ServerId", "CollectedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_control_plane_backups_RequestedByUserId",
            schema: "vessel",
            table: "control_plane_backups",
            column: "RequestedByUserId");

        migrationBuilder.CreateIndex(
            name: "IX_control_plane_backups_Trigger_StartedAt",
            schema: "vessel",
            table: "control_plane_backups",
            columns: new[] { "Trigger", "StartedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_database_import_uploads_CreatedByUserId",
            schema: "vessel",
            table: "database_import_uploads",
            column: "CreatedByUserId");

        migrationBuilder.CreateIndex(
            name: "IX_database_import_uploads_DatabaseResourceId_CreatedAt",
            schema: "vessel",
            table: "database_import_uploads",
            columns: new[] { "DatabaseResourceId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_database_import_uploads_ExpiresAt",
            schema: "vessel",
            table: "database_import_uploads",
            column: "ExpiresAt");

        migrationBuilder.CreateIndex(
            name: "IX_database_import_uploads_TeamId",
            schema: "vessel",
            table: "database_import_uploads",
            column: "TeamId");

        migrationBuilder.CreateIndex(
            name: "IX_docker_cleanup_executions_ServerId_StartedAt",
            schema: "vessel",
            table: "docker_cleanup_executions",
            columns: new[] { "ServerId", "StartedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_docker_cleanup_executions_Status",
            schema: "vessel",
            table: "docker_cleanup_executions",
            column: "Status");

        migrationBuilder.CreateIndex(
            name: "IX_file_mounts_OwnerKind_OwnerId_Path",
            schema: "vessel",
            table: "file_mounts",
            columns: new[] { "OwnerKind", "OwnerId", "Path" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_file_mounts_TeamId",
            schema: "vessel",
            table: "file_mounts",
            column: "TeamId");

        migrationBuilder.CreateIndex(
            name: "IX_git_known_hosts_TeamId_Host_Port_KeyType",
            schema: "vessel",
            table: "git_known_hosts",
            columns: new[] { "TeamId", "Host", "Port", "KeyType" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_git_sources_IsSystemWide",
            schema: "vessel",
            table: "git_sources",
            column: "IsSystemWide");

        migrationBuilder.CreateIndex(
            name: "IX_git_sources_SshKeyId",
            schema: "vessel",
            table: "git_sources",
            column: "SshKeyId");

        migrationBuilder.CreateIndex(
            name: "IX_git_sources_TeamId_Name",
            schema: "vessel",
            table: "git_sources",
            columns: new[] { "TeamId", "Name" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_oauth_identities_ProviderId_Subject",
            schema: "vessel",
            table: "oauth_identities",
            columns: new[] { "ProviderId", "Subject" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_oauth_identities_UserId",
            schema: "vessel",
            table: "oauth_identities",
            column: "UserId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_persistent_volumes_OwnerKind_OwnerId_Name",
            schema: "vessel",
            table: "persistent_volumes",
            columns: new[] { "OwnerKind", "OwnerId", "Name" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_persistent_volumes_TeamId",
            schema: "vessel",
            table: "persistent_volumes",
            column: "TeamId");

        migrationBuilder.CreateIndex(
            name: "IX_resource_runtime_statuses_Kind_ResourceId",
            schema: "vessel",
            table: "resource_runtime_statuses",
            columns: new[] { "Kind", "ResourceId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_resource_runtime_statuses_ResourceId",
            schema: "vessel",
            table: "resource_runtime_statuses",
            column: "ResourceId");

        migrationBuilder.CreateIndex(
            name: "IX_resource_runtime_statuses_ServerId",
            schema: "vessel",
            table: "resource_runtime_statuses",
            column: "ServerId");

        migrationBuilder.CreateIndex(
            name: "IX_scheduled_job_runs_Status_EnqueuedAt",
            schema: "vessel",
            table: "scheduled_job_runs",
            columns: new[] { "Status", "EnqueuedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_scheduled_job_runs_Status_WorkerHost",
            schema: "vessel",
            table: "scheduled_job_runs",
            columns: new[] { "Status", "WorkerHost" });

        migrationBuilder.CreateIndex(
            name: "IX_scheduled_job_runs_TeamId",
            schema: "vessel",
            table: "scheduled_job_runs",
            column: "TeamId");

        migrationBuilder.CreateIndex(
            name: "ux_scheduled_job_runs_occurrence",
            schema: "vessel",
            table: "scheduled_job_runs",
            columns: new[] { "Kind", "ScheduleId", "ScheduledFor" },
            unique: true,
            filter: "\"Trigger\" = 'Scheduled'");

        migrationBuilder.CreateIndex(
            name: "IX_scheduled_task_executions_RunId",
            schema: "vessel",
            table: "scheduled_task_executions",
            column: "RunId");

        migrationBuilder.CreateIndex(
            name: "IX_scheduled_task_executions_ScheduledTaskId_StartedAt",
            schema: "vessel",
            table: "scheduled_task_executions",
            columns: new[] { "ScheduledTaskId", "StartedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_scheduled_tasks_OwnerKind_OwnerId",
            schema: "vessel",
            table: "scheduled_tasks",
            columns: new[] { "OwnerKind", "OwnerId" });

        migrationBuilder.CreateIndex(
            name: "IX_scheduled_tasks_TeamId_Enabled",
            schema: "vessel",
            table: "scheduled_tasks",
            columns: new[] { "TeamId", "Enabled" });

        migrationBuilder.CreateIndex(
            name: "IX_scheduled_volume_backup_destinations_BackupDestinationId",
            schema: "vessel",
            table: "scheduled_volume_backup_destinations",
            column: "BackupDestinationId");

        migrationBuilder.CreateIndex(
            name: "IX_scheduled_volume_backups_OwnerKind_OwnerId",
            schema: "vessel",
            table: "scheduled_volume_backups",
            columns: new[] { "OwnerKind", "OwnerId" });

        migrationBuilder.CreateIndex(
            name: "IX_scheduled_volume_backups_TargetKind_TargetId",
            schema: "vessel",
            table: "scheduled_volume_backups",
            columns: new[] { "TargetKind", "TargetId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_scheduled_volume_backups_TeamId",
            schema: "vessel",
            table: "scheduled_volume_backups",
            column: "TeamId");

        migrationBuilder.CreateIndex(
            name: "IX_server_metric_samples_Resolution_BucketStart",
            schema: "vessel",
            table: "server_metric_samples",
            columns: new[] { "Resolution", "BucketStart" });

        migrationBuilder.CreateIndex(
            name: "IX_server_metric_samples_ServerId_Resolution_BucketStart",
            schema: "vessel",
            table: "server_metric_samples",
            columns: new[] { "ServerId", "Resolution", "BucketStart" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_upgrade_runs_PreUpdateBackupId",
            schema: "vessel",
            table: "upgrade_runs",
            column: "PreUpdateBackupId");

        migrationBuilder.CreateIndex(
            name: "IX_upgrade_runs_StartedByUserId",
            schema: "vessel",
            table: "upgrade_runs",
            column: "StartedByUserId");

        migrationBuilder.CreateIndex(
            name: "IX_upgrade_runs_Status_CreatedAt",
            schema: "vessel",
            table: "upgrade_runs",
            columns: new[] { "Status", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_user_sessions_ActiveTeamId",
            schema: "vessel",
            table: "user_sessions",
            column: "ActiveTeamId");

        migrationBuilder.CreateIndex(
            name: "IX_user_sessions_ExpiresAt",
            schema: "vessel",
            table: "user_sessions",
            column: "ExpiresAt");

        migrationBuilder.CreateIndex(
            name: "IX_user_sessions_UserId_RevokedAt",
            schema: "vessel",
            table: "user_sessions",
            columns: new[] { "UserId", "RevokedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_volume_backup_executions_ScheduleId_CreatedAt",
            schema: "vessel",
            table: "volume_backup_executions",
            columns: new[] { "ScheduleId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_volume_backup_executions_Status_StopRecoveryPending",
            schema: "vessel",
            table: "volume_backup_executions",
            columns: new[] { "Status", "StopRecoveryPending" });

        migrationBuilder.CreateIndex(
            name: "IX_volume_backup_executions_TeamId",
            schema: "vessel",
            table: "volume_backup_executions",
            column: "TeamId");

        migrationBuilder.CreateIndex(
            name: "IX_volume_backup_replicas_BackupDestinationId",
            schema: "vessel",
            table: "volume_backup_replicas",
            column: "BackupDestinationId");

        migrationBuilder.CreateIndex(
            name: "IX_volume_backup_replicas_VolumeBackupExecutionId",
            schema: "vessel",
            table: "volume_backup_replicas",
            column: "VolumeBackupExecutionId");

        migrationBuilder.CreateIndex(
            name: "IX_volume_backup_restores_RequestedByUserId",
            schema: "vessel",
            table: "volume_backup_restores",
            column: "RequestedByUserId");

        migrationBuilder.CreateIndex(
            name: "IX_volume_backup_restores_TeamId",
            schema: "vessel",
            table: "volume_backup_restores",
            column: "TeamId");

        migrationBuilder.CreateIndex(
            name: "IX_volume_backup_restores_VolumeBackupExecutionId_Status",
            schema: "vessel",
            table: "volume_backup_restores",
            columns: new[] { "VolumeBackupExecutionId", "Status" });

        migrationBuilder.AddForeignKey(
            name: "FK_applications_git_sources_SourceId",
            schema: "vessel",
            table: "applications",
            column: "SourceId",
            principalSchema: "vessel",
            principalTable: "git_sources",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.AddForeignKey(
            name: "FK_database_restores_backup_destinations_ImportDestinationId",
            schema: "vessel",
            table: "database_restores",
            column: "ImportDestinationId",
            principalSchema: "vessel",
            principalTable: "backup_destinations",
            principalColumn: "Id",
            onDelete: ReferentialAction.SetNull);

        migrationBuilder.AddForeignKey(
            name: "FK_users_teams_CurrentTeamId",
            schema: "vessel",
            table: "users",
            column: "CurrentTeamId",
            principalSchema: "vessel",
            principalTable: "teams",
            principalColumn: "Id",
            onDelete: ReferentialAction.SetNull);

        // IAM-08: earlier rows with an acting user were user actions.
        migrationBuilder.Sql("""UPDATE vessel.audit_logs SET "ActorType" = 'user' WHERE "ActorUserId" IS NOT NULL;""");

        // DEP-10: values saved before exact-value semantics keep resolving exactly as they did (upstream
        // uses_legacy_escaping is true for existing variables and false for new ones).
        migrationBuilder.Sql("""UPDATE vessel.environment_variables SET "UsesLegacyEscaping" = TRUE;""");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_applications_git_sources_SourceId",
            schema: "vessel",
            table: "applications");

        migrationBuilder.DropForeignKey(
            name: "FK_database_restores_backup_destinations_ImportDestinationId",
            schema: "vessel",
            table: "database_restores");

        migrationBuilder.DropForeignKey(
            name: "FK_users_teams_CurrentTeamId",
            schema: "vessel",
            table: "users");

        migrationBuilder.DropTable(
            name: "container_metric_samples",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "database_import_uploads",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "docker_cleanup_executions",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "file_mounts",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "git_known_hosts",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "git_sources",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "oauth_identities",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "persistent_volumes",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "resource_runtime_statuses",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "scheduled_job_runs",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "scheduled_task_executions",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "scheduled_volume_backup_destinations",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "server_metric_samples",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "server_monitoring_settings",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "upgrade_runs",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "user_sessions",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "volume_backup_replicas",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "volume_backup_restores",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "auth_providers",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "scheduled_tasks",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "control_plane_backups",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "volume_backup_executions",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "scheduled_volume_backups",
            schema: "vessel");

        migrationBuilder.DropIndex(
            name: "IX_users_CurrentTeamId",
            schema: "vessel",
            table: "users");

        migrationBuilder.DropIndex(
            name: "IX_personal_access_tokens_ExpiresAt_ExpirationWarningSentAt",
            schema: "vessel",
            table: "personal_access_tokens");

        migrationBuilder.DropIndex(
            name: "IX_database_restores_ImportDestinationId",
            schema: "vessel",
            table: "database_restores");

        migrationBuilder.DropIndex(
            name: "IX_audit_logs_CreatedAt",
            schema: "vessel",
            table: "audit_logs");

        migrationBuilder.DropIndex(
            name: "IX_audit_logs_TeamId_Action_CreatedAt",
            schema: "vessel",
            table: "audit_logs");

        migrationBuilder.DropIndex(
            name: "IX_audit_logs_TeamId_CreatedAt_Id",
            schema: "vessel",
            table: "audit_logs");

        migrationBuilder.DropIndex(
            name: "IX_audit_logs_TeamId_Source_CreatedAt",
            schema: "vessel",
            table: "audit_logs");

        migrationBuilder.DropIndex(
            name: "IX_applications_SourceId",
            schema: "vessel",
            table: "applications");

        migrationBuilder.DropColumn(
            name: "CurrentTeamId",
            schema: "vessel",
            table: "users");

        migrationBuilder.DropColumn(
            name: "GeneratedSecretsReferenceId",
            schema: "vessel",
            table: "service_resources");

        migrationBuilder.DropColumn(
            name: "ConsecutiveFailedHealthChecks",
            schema: "vessel",
            table: "servers");

        migrationBuilder.DropColumn(
            name: "ExpirationWarningSentAt",
            schema: "vessel",
            table: "personal_access_tokens");

        migrationBuilder.DropColumn(
            name: "PreserveComposeNull",
            schema: "vessel",
            table: "environment_variables");

        migrationBuilder.DropColumn(
            name: "SortOrder",
            schema: "vessel",
            table: "environment_variables");

        migrationBuilder.DropColumn(
            name: "UsesLegacyEscaping",
            schema: "vessel",
            table: "environment_variables");

        migrationBuilder.DropColumn(
            name: "ImportDatabaseName",
            schema: "vessel",
            table: "database_restores");

        migrationBuilder.DropColumn(
            name: "ImportDestinationId",
            schema: "vessel",
            table: "database_restores");

        migrationBuilder.DropColumn(
            name: "ImportReference",
            schema: "vessel",
            table: "database_restores");

        migrationBuilder.DropColumn(
            name: "ImportSource",
            schema: "vessel",
            table: "database_restores");

        migrationBuilder.DropColumn(
            name: "MissedRunPolicy",
            schema: "vessel",
            table: "backup_schedules");

        migrationBuilder.DropColumn(
            name: "TimeZone",
            schema: "vessel",
            table: "backup_schedules");

        migrationBuilder.DropColumn(
            name: "ActorName",
            schema: "vessel",
            table: "audit_logs");

        migrationBuilder.DropColumn(
            name: "ActorTokenId",
            schema: "vessel",
            table: "audit_logs");

        migrationBuilder.DropColumn(
            name: "ActorTokenName",
            schema: "vessel",
            table: "audit_logs");

        migrationBuilder.DropColumn(
            name: "ActorType",
            schema: "vessel",
            table: "audit_logs");

        migrationBuilder.DropColumn(
            name: "AnonymizedAt",
            schema: "vessel",
            table: "audit_logs");

        migrationBuilder.DropColumn(
            name: "ChangesJson",
            schema: "vessel",
            table: "audit_logs");

        migrationBuilder.DropColumn(
            name: "Description",
            schema: "vessel",
            table: "audit_logs");

        migrationBuilder.DropColumn(
            name: "IpAddress",
            schema: "vessel",
            table: "audit_logs");

        migrationBuilder.DropColumn(
            name: "Level",
            schema: "vessel",
            table: "audit_logs");

        migrationBuilder.DropColumn(
            name: "ResourceName",
            schema: "vessel",
            table: "audit_logs");

        migrationBuilder.DropColumn(
            name: "Source",
            schema: "vessel",
            table: "audit_logs");

        migrationBuilder.DropColumn(
            name: "UserAgent",
            schema: "vessel",
            table: "audit_logs");

        migrationBuilder.DropColumn(
            name: "SourceId",
            schema: "vessel",
            table: "applications");

        // Imports (DATA-10) have no backup execution and cannot exist in the older schema.
        migrationBuilder.Sql("""DELETE FROM vessel.database_restores WHERE "BackupExecutionId" IS NULL;""");

        migrationBuilder.AlterColumn<Guid>(
            name: "BackupExecutionId",
            schema: "vessel",
            table: "database_restores",
            type: "uuid",
            nullable: false,
            defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
            oldClrType: typeof(Guid),
            oldType: "uuid",
            oldNullable: true);

        migrationBuilder.AlterColumn<string>(
            name: "BuildConfiguration",
            schema: "vessel",
            table: "applications",
            type: "character varying(2048)",
            maxLength: 2048,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "character varying(8192)",
            oldMaxLength: 8192);

        migrationBuilder.CreateIndex(
            name: "IX_audit_logs_TeamId_CreatedAt",
            schema: "vessel",
            table: "audit_logs",
            columns: new[] { "TeamId", "CreatedAt" });
    }
}
