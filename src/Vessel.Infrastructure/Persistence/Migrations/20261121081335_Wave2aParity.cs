using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vessel.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class _20261121081335_Wave2aParity : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_backup_executions_backup_schedules_ScheduleId",
            schema: "vessel",
            table: "backup_executions");

        migrationBuilder.DropForeignKey(
            name: "FK_certificates_applications_ApplicationId",
            schema: "vessel",
            table: "certificates");

        migrationBuilder.DropForeignKey(
            name: "FK_certificates_secret_references_CertificateSecretReferenceId",
            schema: "vessel",
            table: "certificates");

        migrationBuilder.DropForeignKey(
            name: "FK_certificates_secret_references_PrivateKeySecretReferenceId",
            schema: "vessel",
            table: "certificates");

        migrationBuilder.DropTable(
            name: "database_backup_policies",
            schema: "vessel");

        migrationBuilder.DropIndex(
            name: "IX_deployments_ServerId",
            schema: "vessel",
            table: "deployments");

        migrationBuilder.DropIndex(
            name: "IX_certificates_ApplicationId_Host",
            schema: "vessel",
            table: "certificates");

        migrationBuilder.DropIndex(
            name: "IX_certificates_CertificateSecretReferenceId",
            schema: "vessel",
            table: "certificates");

        migrationBuilder.DropIndex(
            name: "IX_certificates_PrivateKeySecretReferenceId",
            schema: "vessel",
            table: "certificates");

        migrationBuilder.DropIndex(
            name: "IX_certificates_TeamId_RenewalDueAt",
            schema: "vessel",
            table: "certificates");

        migrationBuilder.DropPrimaryKey(
            name: "PK_application_domains",
            schema: "vessel",
            table: "application_domains");

        migrationBuilder.DropColumn(
            name: "CertificateSecretReferenceId",
            schema: "vessel",
            table: "certificates");

        migrationBuilder.DropColumn(
            name: "PrivateKeySecretReferenceId",
            schema: "vessel",
            table: "certificates");

        migrationBuilder.RenameColumn(
            name: "RenewalDueAt",
            schema: "vessel",
            table: "certificates",
            newName: "LastSyncedAt");

        // EF scaffolds this as a rename because the types match, but RenewalDueAt and LastSyncedAt are unrelated:
        // no certificate has been synced from acme.json yet (NET-04).
        migrationBuilder.Sql("""UPDATE vessel.certificates SET "LastSyncedAt" = NULL;""");

        migrationBuilder.AddColumn<string>(
            name: "ApplicationOutcomes",
            schema: "vessel",
            table: "webhook_events",
            type: "jsonb",
            nullable: false,
            defaultValueSql: "'[]'::jsonb");

        migrationBuilder.AddColumn<string>(
            name: "Mode",
            schema: "vessel",
            table: "terminal_sessions",
            type: "character varying(16)",
            maxLength: 16,
            nullable: false,
            defaultValue: "Shell");

        migrationBuilder.AddColumn<Guid>(
            name: "ResourceId",
            schema: "vessel",
            table: "terminal_sessions",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ResourceKind",
            schema: "vessel",
            table: "terminal_sessions",
            type: "character varying(32)",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "AccessGrants",
            schema: "vessel",
            table: "team_memberships",
            type: "jsonb",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "AccessGrants",
            schema: "vessel",
            table: "team_invitations",
            type: "jsonb",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "ConcurrentBuilds",
            schema: "vessel",
            table: "servers",
            type: "integer",
            nullable: false,
            defaultValue: 2);

        migrationBuilder.AddColumn<int>(
            name: "DeploymentQueueLimit",
            schema: "vessel",
            table: "servers",
            type: "integer",
            nullable: false,
            defaultValue: 25);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "HostKeyChangedAt",
            schema: "vessel",
            table: "servers",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "HostKeyChecking",
            schema: "vessel",
            table: "servers",
            type: "character varying(16)",
            maxLength: 16,
            nullable: false,
            defaultValue: "Enforce");

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "HostKeyTrustedAt",
            schema: "vessel",
            table: "servers",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PendingHostKey",
            schema: "vessel",
            table: "servers",
            type: "character varying(16384)",
            maxLength: 16384,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PinnedHostKey",
            schema: "vessel",
            table: "servers",
            type: "character varying(16384)",
            maxLength: 16384,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "SshKeyId",
            schema: "vessel",
            table: "servers",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "TerminalAccessEnabled",
            schema: "vessel",
            table: "servers",
            type: "boolean",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<string>(
            name: "validation_compose_version",
            schema: "vessel",
            table: "servers",
            type: "character varying(128)",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "validation_has_passwordless_sudo",
            schema: "vessel",
            table: "servers",
            type: "boolean",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "validation_is_root_user",
            schema: "vessel",
            table: "servers",
            type: "boolean",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "validation_message",
            schema: "vessel",
            table: "servers",
            type: "character varying(1000)",
            maxLength: 1000,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "validation_os_id",
            schema: "vessel",
            table: "servers",
            type: "character varying(128)",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "validation_os_id_like",
            schema: "vessel",
            table: "servers",
            type: "character varying(128)",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "validation_os_version",
            schema: "vessel",
            table: "servers",
            type: "character varying(128)",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "validation_package_manager",
            schema: "vessel",
            table: "servers",
            type: "character varying(128)",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "validation_runtime_version",
            schema: "vessel",
            table: "servers",
            type: "character varying(128)",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "validation_succeeded",
            schema: "vessel",
            table: "servers",
            type: "boolean",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "validation_validated_at",
            schema: "vessel",
            table: "servers",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "FileName",
            schema: "vessel",
            table: "proxy_configuration_versions",
            type: "character varying(128)",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "RestoredFromId",
            schema: "vessel",
            table: "proxy_configuration_versions",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Source",
            schema: "vessel",
            table: "proxy_configuration_versions",
            type: "character varying(32)",
            maxLength: 32,
            nullable: false,
            // Every version stored before NET-07 was generated by Vessel.
            defaultValue: "Generated");

        migrationBuilder.AddColumn<string>(
            name: "AccessGrants",
            schema: "vessel",
            table: "personal_access_tokens",
            type: "jsonb",
            nullable: true);

        // personal_access_tokens.ExpiresAt already exists (created by Phase4Auth). Earlier models left the property
        // unmapped because it had no setter, so expiry was never persisted; the model now maps the existing column.

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "PausedAt",
            schema: "vessel",
            table: "personal_access_tokens",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "AutoDisabledAt",
            schema: "vessel",
            table: "notification_targets",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "ConsecutiveFailureCount",
            schema: "vessel",
            table: "notification_targets",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<string>(
            name: "DisabledReason",
            schema: "vessel",
            table: "notification_targets",
            type: "character varying(512)",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "EventRoutesJson",
            schema: "vessel",
            table: "notification_targets",
            type: "jsonb",
            nullable: false,
            defaultValue: "{}");

        migrationBuilder.AddColumn<string[]>(
            name: "SubscribedEvents",
            schema: "vessel",
            table: "notification_targets",
            type: "text[]",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "CancellationReason",
            schema: "vessel",
            table: "deployments",
            type: "character varying(512)",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "DeployPhaseStartedAt",
            schema: "vessel",
            table: "deployments",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "DispatchedAt",
            schema: "vessel",
            table: "deployments",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "ParentDeploymentId",
            schema: "vessel",
            table: "deployments",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "RollbackImage",
            schema: "vessel",
            table: "deployments",
            type: "character varying(512)",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Trigger",
            schema: "vessel",
            table: "deployments",
            type: "character varying(32)",
            maxLength: 32,
            nullable: false,
            defaultValue: "Manual");

        migrationBuilder.AddColumn<bool>(
            name: "HealthCheckEnabled",
            schema: "vessel",
            table: "database_resources",
            type: "boolean",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<int>(
            name: "HealthCheckIntervalSeconds",
            schema: "vessel",
            table: "database_resources",
            type: "integer",
            nullable: false,
            defaultValue: 15);

        migrationBuilder.AddColumn<int>(
            name: "HealthCheckRetries",
            schema: "vessel",
            table: "database_resources",
            type: "integer",
            nullable: false,
            defaultValue: 5);

        migrationBuilder.AddColumn<int>(
            name: "HealthCheckStartPeriodSeconds",
            schema: "vessel",
            table: "database_resources",
            type: "integer",
            nullable: false,
            defaultValue: 5);

        migrationBuilder.AddColumn<int>(
            name: "HealthCheckTimeoutSeconds",
            schema: "vessel",
            table: "database_resources",
            type: "integer",
            nullable: false,
            defaultValue: 5);

        migrationBuilder.AddColumn<string>(
            name: "Image",
            schema: "vessel",
            table: "database_resources",
            type: "character varying(255)",
            maxLength: 255,
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "LastHealthCheckAt",
            schema: "vessel",
            table: "database_resources",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AlterColumn<Guid>(
            name: "ApplicationId",
            schema: "vessel",
            table: "certificates",
            type: "uuid",
            nullable: true,
            oldClrType: typeof(Guid),
            oldType: "uuid");

        migrationBuilder.AddColumn<string>(
            name: "AcmeKey",
            schema: "vessel",
            table: "certificates",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "ExpiryAlertSentFor",
            schema: "vessel",
            table: "certificates",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "InUse",
            schema: "vessel",
            table: "certificates",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "Resolver",
            schema: "vessel",
            table: "certificates",
            type: "character varying(120)",
            maxLength: 120,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "ServerId",
            schema: "vessel",
            table: "certificates",
            type: "uuid",
            nullable: false,
            defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

        migrationBuilder.AddColumn<string>(
            name: "SubjectAlternativeNames",
            schema: "vessel",
            table: "certificates",
            type: "character varying(8000)",
            maxLength: 8000,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "DatabaseSelection",
            schema: "vessel",
            table: "backup_schedules",
            type: "character varying(2000)",
            maxLength: 2000,
            nullable: false,
            defaultValue: "*");

        migrationBuilder.AddColumn<bool>(
            name: "DisableLocalCopy",
            schema: "vessel",
            table: "backup_schedules",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "DumpFormat",
            schema: "vessel",
            table: "backup_schedules",
            type: "character varying(32)",
            maxLength: 32,
            nullable: false,
            defaultValue: "Native");

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "FailingSince",
            schema: "vessel",
            table: "backup_schedules",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "LastFailureNotifiedAt",
            schema: "vessel",
            table: "backup_schedules",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "LastSucceededAt",
            schema: "vessel",
            table: "backup_schedules",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "LocalRetentionDays",
            schema: "vessel",
            table: "backup_schedules",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<long>(
            name: "LocalRetentionMaxBytes",
            schema: "vessel",
            table: "backup_schedules",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<int>(
            name: "MissingAlertDays",
            schema: "vessel",
            table: "backup_schedules",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "MissingAlertSentAt",
            schema: "vessel",
            table: "backup_schedules",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "RemoteRetentionCount",
            schema: "vessel",
            table: "backup_schedules",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "RemoteRetentionDays",
            schema: "vessel",
            table: "backup_schedules",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<long>(
            name: "RemoteRetentionMaxBytes",
            schema: "vessel",
            table: "backup_schedules",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<string>(
            name: "ArtifactFormat",
            schema: "vessel",
            table: "backup_executions",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "DatabaseName",
            schema: "vessel",
            table: "backup_executions",
            type: "character varying(63)",
            maxLength: 63,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PathPrefix",
            schema: "vessel",
            table: "application_domains",
            type: "character varying(1024)",
            maxLength: 1024,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "HostKind",
            schema: "vessel",
            table: "application_domains",
            type: "character varying(16)",
            maxLength: 16,
            nullable: false,
            // Domains saved before NET-03 were validated as DNS names.
            defaultValue: "Dns");

        migrationBuilder.AddColumn<string>(
            name: "Policy",
            schema: "vessel",
            table: "application_domains",
            type: "jsonb",
            nullable: false,
            defaultValueSql: "'{}'::jsonb");

        migrationBuilder.AddPrimaryKey(
            name: "PK_application_domains",
            schema: "vessel",
            table: "application_domains",
            columns: new[] { "ApplicationId", "DomainName", "PathPrefix" });

        migrationBuilder.CreateTable(
            name: "backup_destinations",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Endpoint = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                Region = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Bucket = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                UsePathStyle = table.Column<bool>(type: "boolean", nullable: false),
                SkipTlsVerification = table.Column<bool>(type: "boolean", nullable: false),
                CredentialsReferenceId = table.Column<Guid>(type: "uuid", nullable: false),
                IsUsable = table.Column<bool>(type: "boolean", nullable: false),
                LastValidatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastValidationError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                UnusableNotifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_backup_destinations", x => x.Id);
                table.ForeignKey(
                    name: "FK_backup_destinations_secret_references_CredentialsReferenceId",
                    column: x => x.CredentialsReferenceId,
                    principalSchema: "vessel",
                    principalTable: "secret_references",
                    principalColumn: "Id");
                table.ForeignKey(
                    name: "FK_backup_destinations_teams_TeamId",
                    column: x => x.TeamId,
                    principalSchema: "vessel",
                    principalTable: "teams",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "instance_email_settings",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Provider = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                ActiveSmtpServerName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                SmtpServers = table.Column<string>(type: "jsonb", nullable: false),
                ProtectedResendApiKey = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                ResendFromAddress = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                ResendFromName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                NotificationFromAddress = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                NotificationFromName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_instance_email_settings", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "notification_throttles",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                EventType = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                SubjectKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_notification_throttles", x => x.Id);
                table.ForeignKey(
                    name: "FK_notification_throttles_teams_TeamId",
                    column: x => x.TeamId,
                    principalSchema: "vessel",
                    principalTable: "teams",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "proxy_dynamic_configurations",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                FileName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Content = table.Column<string>(type: "text", nullable: false),
                ContentHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_proxy_dynamic_configurations", x => x.Id);
                table.ForeignKey(
                    name: "FK_proxy_dynamic_configurations_servers_ServerId",
                    column: x => x.ServerId,
                    principalSchema: "vessel",
                    principalTable: "servers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "service_domains",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ServiceResourceId = table.Column<Guid>(type: "uuid", nullable: false),
                ComposeService = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                Host = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                HostKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                PathPrefix = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                TargetPort = table.Column<int>(type: "integer", nullable: false),
                TlsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                Policy = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_service_domains", x => x.Id);
                table.ForeignKey(
                    name: "FK_service_domains_service_resources_ServiceResourceId",
                    column: x => x.ServiceResourceId,
                    principalSchema: "vessel",
                    principalTable: "service_resources",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "ssh_keys",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                PrivateKeyReferenceId = table.Column<Guid>(type: "uuid", nullable: false),
                PublicKey = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: false),
                Fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Algorithm = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Bits = table.Column<int>(type: "integer", nullable: true),
                IsGitRelated = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ssh_keys", x => x.Id);
                table.ForeignKey(
                    name: "FK_ssh_keys_secret_references_PrivateKeyReferenceId",
                    column: x => x.PrivateKeyReferenceId,
                    principalSchema: "vessel",
                    principalTable: "secret_references",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_ssh_keys_teams_TeamId",
                    column: x => x.TeamId,
                    principalSchema: "vessel",
                    principalTable: "teams",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "backup_artifact_replicas",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                BackupExecutionId = table.Column<Guid>(type: "uuid", nullable: false),
                BackupDestinationId = table.Column<Guid>(type: "uuid", nullable: true),
                Bucket = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                Uploaded = table.Column<bool>(type: "boolean", nullable: false),
                Message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_backup_artifact_replicas", x => x.Id);
                table.ForeignKey(
                    name: "FK_backup_artifact_replicas_backup_destinations_BackupDestinat~",
                    column: x => x.BackupDestinationId,
                    principalSchema: "vessel",
                    principalTable: "backup_destinations",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_backup_artifact_replicas_backup_executions_BackupExecutionId",
                    column: x => x.BackupExecutionId,
                    principalSchema: "vessel",
                    principalTable: "backup_executions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "backup_schedule_destinations",
            schema: "vessel",
            columns: table => new
            {
                BackupScheduleId = table.Column<Guid>(type: "uuid", nullable: false),
                BackupDestinationId = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_backup_schedule_destinations", x => new { x.BackupScheduleId, x.BackupDestinationId });
                table.ForeignKey(
                    name: "FK_backup_schedule_destinations_backup_destinations_BackupDest~",
                    column: x => x.BackupDestinationId,
                    principalSchema: "vessel",
                    principalTable: "backup_destinations",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_backup_schedule_destinations_backup_schedules_BackupSchedul~",
                    column: x => x.BackupScheduleId,
                    principalSchema: "vessel",
                    principalTable: "backup_schedules",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_servers_SshKeyId",
            schema: "vessel",
            table: "servers",
            column: "SshKeyId");

        migrationBuilder.CreateIndex(
            name: "IX_proxy_configuration_versions_RestoredFromId",
            schema: "vessel",
            table: "proxy_configuration_versions",
            column: "RestoredFromId");

        migrationBuilder.CreateIndex(
            name: "IX_proxy_configuration_versions_ServerId_FileName_Status",
            schema: "vessel",
            table: "proxy_configuration_versions",
            columns: new[] { "ServerId", "FileName", "Status" });

        migrationBuilder.CreateIndex(
            name: "IX_deployments_ServerId_Status_CreatedAt",
            schema: "vessel",
            table: "deployments",
            columns: new[] { "ServerId", "Status", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_certificates_ApplicationId",
            schema: "vessel",
            table: "certificates",
            column: "ApplicationId");

        migrationBuilder.CreateIndex(
            name: "IX_certificates_ServerId_AcmeKey",
            schema: "vessel",
            table: "certificates",
            columns: new[] { "ServerId", "AcmeKey" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_certificates_ServerId_Host",
            schema: "vessel",
            table: "certificates",
            columns: new[] { "ServerId", "Host" });

        migrationBuilder.CreateIndex(
            name: "IX_certificates_TeamId_ExpiresAt",
            schema: "vessel",
            table: "certificates",
            columns: new[] { "TeamId", "ExpiresAt" });

        migrationBuilder.CreateIndex(
            name: "IX_backup_schedules_Enabled_MissingAlertDays",
            schema: "vessel",
            table: "backup_schedules",
            columns: new[] { "Enabled", "MissingAlertDays" });

        migrationBuilder.CreateIndex(
            name: "IX_application_domains_DomainName_PathPrefix",
            schema: "vessel",
            table: "application_domains",
            columns: new[] { "DomainName", "PathPrefix" });

        migrationBuilder.CreateIndex(
            name: "IX_backup_artifact_replicas_BackupDestinationId",
            schema: "vessel",
            table: "backup_artifact_replicas",
            column: "BackupDestinationId");

        migrationBuilder.CreateIndex(
            name: "IX_backup_artifact_replicas_BackupExecutionId",
            schema: "vessel",
            table: "backup_artifact_replicas",
            column: "BackupExecutionId");

        migrationBuilder.CreateIndex(
            name: "IX_backup_destinations_CredentialsReferenceId",
            schema: "vessel",
            table: "backup_destinations",
            column: "CredentialsReferenceId");

        migrationBuilder.CreateIndex(
            name: "IX_backup_destinations_IsUsable",
            schema: "vessel",
            table: "backup_destinations",
            column: "IsUsable");

        migrationBuilder.CreateIndex(
            name: "IX_backup_destinations_TeamId_Name",
            schema: "vessel",
            table: "backup_destinations",
            columns: new[] { "TeamId", "Name" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_backup_schedule_destinations_BackupDestinationId",
            schema: "vessel",
            table: "backup_schedule_destinations",
            column: "BackupDestinationId");

        migrationBuilder.CreateIndex(
            name: "IX_notification_throttles_SentAt",
            schema: "vessel",
            table: "notification_throttles",
            column: "SentAt");

        migrationBuilder.CreateIndex(
            name: "IX_notification_throttles_TeamId_EventType_SubjectKey",
            schema: "vessel",
            table: "notification_throttles",
            columns: new[] { "TeamId", "EventType", "SubjectKey" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_proxy_dynamic_configurations_ServerId_FileName",
            schema: "vessel",
            table: "proxy_dynamic_configurations",
            columns: new[] { "ServerId", "FileName" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_service_domains_Host_PathPrefix",
            schema: "vessel",
            table: "service_domains",
            columns: new[] { "Host", "PathPrefix" });

        migrationBuilder.CreateIndex(
            name: "IX_service_domains_ServiceResourceId_Host_PathPrefix",
            schema: "vessel",
            table: "service_domains",
            columns: new[] { "ServiceResourceId", "Host", "PathPrefix" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ssh_keys_PrivateKeyReferenceId",
            schema: "vessel",
            table: "ssh_keys",
            column: "PrivateKeyReferenceId");

        migrationBuilder.CreateIndex(
            name: "IX_ssh_keys_TeamId_Fingerprint",
            schema: "vessel",
            table: "ssh_keys",
            columns: new[] { "TeamId", "Fingerprint" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ssh_keys_TeamId_Name",
            schema: "vessel",
            table: "ssh_keys",
            columns: new[] { "TeamId", "Name" },
            unique: true);

        migrationBuilder.AddForeignKey(
            name: "FK_backup_executions_backup_schedules_ScheduleId",
            schema: "vessel",
            table: "backup_executions",
            column: "ScheduleId",
            principalSchema: "vessel",
            principalTable: "backup_schedules",
            principalColumn: "Id",
            onDelete: ReferentialAction.Cascade);

        migrationBuilder.AddForeignKey(
            name: "FK_certificates_applications_ApplicationId",
            schema: "vessel",
            table: "certificates",
            column: "ApplicationId",
            principalSchema: "vessel",
            principalTable: "applications",
            principalColumn: "Id",
            onDelete: ReferentialAction.SetNull);

        // NET-04: certificates now belong to the server whose acme.json holds them. Existing rows were created per
        // application domain, so they take the application's server. Rows whose application no longer exists cannot
        // be placed on a server and are removed; the acme.json sync recreates rows for certificates that exist.
        migrationBuilder.Sql("""
            UPDATE vessel.certificates AS c
            SET "ServerId" = a."ServerId"
            FROM vessel.applications AS a
            WHERE c."ApplicationId" = a."Id";
            DELETE FROM vessel.certificates WHERE "ServerId" = '00000000-0000-0000-0000-000000000000';
            """);

        // IAM-04 (Coolify v4.6.0): `delete` became its own ability. Tokens that held `write` keep the ability to
        // delete by receiving `delete` (same rule as TokenScopeMapper.MigrateLegacyScopes; scopes stay sorted).
        migrationBuilder.Sql("""
            UPDATE vessel.personal_access_tokens
            SET "Scopes" = array_to_string(ARRAY(
                SELECT distinct_scopes.scope
                FROM (SELECT DISTINCT scope
                      FROM unnest(string_to_array("Scopes", ' ') || ARRAY['delete']) AS scope
                      WHERE scope <> '') AS distinct_scopes
                ORDER BY distinct_scopes.scope COLLATE "C"), ' ')
            WHERE 'write' = ANY(string_to_array("Scopes", ' '))
              AND NOT 'delete' = ANY(string_to_array("Scopes", ' '));
            """);

        migrationBuilder.AddForeignKey(
            name: "FK_certificates_servers_ServerId",
            schema: "vessel",
            table: "certificates",
            column: "ServerId",
            principalSchema: "vessel",
            principalTable: "servers",
            principalColumn: "Id",
            onDelete: ReferentialAction.Cascade);

        migrationBuilder.AddForeignKey(
            name: "FK_proxy_configuration_versions_proxy_configuration_versions_R~",
            schema: "vessel",
            table: "proxy_configuration_versions",
            column: "RestoredFromId",
            principalSchema: "vessel",
            principalTable: "proxy_configuration_versions",
            principalColumn: "Id",
            onDelete: ReferentialAction.SetNull);

        migrationBuilder.AddForeignKey(
            name: "FK_servers_ssh_keys_SshKeyId",
            schema: "vessel",
            table: "servers",
            column: "SshKeyId",
            principalSchema: "vessel",
            principalTable: "ssh_keys",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_backup_executions_backup_schedules_ScheduleId",
            schema: "vessel",
            table: "backup_executions");

        migrationBuilder.DropForeignKey(
            name: "FK_certificates_applications_ApplicationId",
            schema: "vessel",
            table: "certificates");

        migrationBuilder.DropForeignKey(
            name: "FK_certificates_servers_ServerId",
            schema: "vessel",
            table: "certificates");

        migrationBuilder.DropForeignKey(
            name: "FK_proxy_configuration_versions_proxy_configuration_versions_R~",
            schema: "vessel",
            table: "proxy_configuration_versions");

        migrationBuilder.DropForeignKey(
            name: "FK_servers_ssh_keys_SshKeyId",
            schema: "vessel",
            table: "servers");

        migrationBuilder.DropTable(
            name: "backup_artifact_replicas",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "backup_schedule_destinations",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "instance_email_settings",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "notification_throttles",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "proxy_dynamic_configurations",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "service_domains",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "ssh_keys",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "backup_destinations",
            schema: "vessel");

        migrationBuilder.DropIndex(
            name: "IX_servers_SshKeyId",
            schema: "vessel",
            table: "servers");

        migrationBuilder.DropIndex(
            name: "IX_proxy_configuration_versions_RestoredFromId",
            schema: "vessel",
            table: "proxy_configuration_versions");

        migrationBuilder.DropIndex(
            name: "IX_proxy_configuration_versions_ServerId_FileName_Status",
            schema: "vessel",
            table: "proxy_configuration_versions");

        migrationBuilder.DropIndex(
            name: "IX_deployments_ServerId_Status_CreatedAt",
            schema: "vessel",
            table: "deployments");

        migrationBuilder.DropIndex(
            name: "IX_certificates_ApplicationId",
            schema: "vessel",
            table: "certificates");

        migrationBuilder.DropIndex(
            name: "IX_certificates_ServerId_AcmeKey",
            schema: "vessel",
            table: "certificates");

        migrationBuilder.DropIndex(
            name: "IX_certificates_ServerId_Host",
            schema: "vessel",
            table: "certificates");

        migrationBuilder.DropIndex(
            name: "IX_certificates_TeamId_ExpiresAt",
            schema: "vessel",
            table: "certificates");

        migrationBuilder.DropIndex(
            name: "IX_backup_schedules_Enabled_MissingAlertDays",
            schema: "vessel",
            table: "backup_schedules");

        migrationBuilder.DropPrimaryKey(
            name: "PK_application_domains",
            schema: "vessel",
            table: "application_domains");

        migrationBuilder.DropIndex(
            name: "IX_application_domains_DomainName_PathPrefix",
            schema: "vessel",
            table: "application_domains");

        migrationBuilder.DropColumn(
            name: "ApplicationOutcomes",
            schema: "vessel",
            table: "webhook_events");

        migrationBuilder.DropColumn(
            name: "Mode",
            schema: "vessel",
            table: "terminal_sessions");

        migrationBuilder.DropColumn(
            name: "ResourceId",
            schema: "vessel",
            table: "terminal_sessions");

        migrationBuilder.DropColumn(
            name: "ResourceKind",
            schema: "vessel",
            table: "terminal_sessions");

        migrationBuilder.DropColumn(
            name: "AccessGrants",
            schema: "vessel",
            table: "team_memberships");

        migrationBuilder.DropColumn(
            name: "AccessGrants",
            schema: "vessel",
            table: "team_invitations");

        migrationBuilder.DropColumn(
            name: "ConcurrentBuilds",
            schema: "vessel",
            table: "servers");

        migrationBuilder.DropColumn(
            name: "DeploymentQueueLimit",
            schema: "vessel",
            table: "servers");

        migrationBuilder.DropColumn(
            name: "HostKeyChangedAt",
            schema: "vessel",
            table: "servers");

        migrationBuilder.DropColumn(
            name: "HostKeyChecking",
            schema: "vessel",
            table: "servers");

        migrationBuilder.DropColumn(
            name: "HostKeyTrustedAt",
            schema: "vessel",
            table: "servers");

        migrationBuilder.DropColumn(
            name: "PendingHostKey",
            schema: "vessel",
            table: "servers");

        migrationBuilder.DropColumn(
            name: "PinnedHostKey",
            schema: "vessel",
            table: "servers");

        migrationBuilder.DropColumn(
            name: "SshKeyId",
            schema: "vessel",
            table: "servers");

        migrationBuilder.DropColumn(
            name: "TerminalAccessEnabled",
            schema: "vessel",
            table: "servers");

        migrationBuilder.DropColumn(
            name: "validation_compose_version",
            schema: "vessel",
            table: "servers");

        migrationBuilder.DropColumn(
            name: "validation_has_passwordless_sudo",
            schema: "vessel",
            table: "servers");

        migrationBuilder.DropColumn(
            name: "validation_is_root_user",
            schema: "vessel",
            table: "servers");

        migrationBuilder.DropColumn(
            name: "validation_message",
            schema: "vessel",
            table: "servers");

        migrationBuilder.DropColumn(
            name: "validation_os_id",
            schema: "vessel",
            table: "servers");

        migrationBuilder.DropColumn(
            name: "validation_os_id_like",
            schema: "vessel",
            table: "servers");

        migrationBuilder.DropColumn(
            name: "validation_os_version",
            schema: "vessel",
            table: "servers");

        migrationBuilder.DropColumn(
            name: "validation_package_manager",
            schema: "vessel",
            table: "servers");

        migrationBuilder.DropColumn(
            name: "validation_runtime_version",
            schema: "vessel",
            table: "servers");

        migrationBuilder.DropColumn(
            name: "validation_succeeded",
            schema: "vessel",
            table: "servers");

        migrationBuilder.DropColumn(
            name: "validation_validated_at",
            schema: "vessel",
            table: "servers");

        migrationBuilder.DropColumn(
            name: "FileName",
            schema: "vessel",
            table: "proxy_configuration_versions");

        migrationBuilder.DropColumn(
            name: "RestoredFromId",
            schema: "vessel",
            table: "proxy_configuration_versions");

        migrationBuilder.DropColumn(
            name: "Source",
            schema: "vessel",
            table: "proxy_configuration_versions");

        migrationBuilder.DropColumn(
            name: "AccessGrants",
            schema: "vessel",
            table: "personal_access_tokens");

        migrationBuilder.DropColumn(
            name: "PausedAt",
            schema: "vessel",
            table: "personal_access_tokens");

        migrationBuilder.DropColumn(
            name: "AutoDisabledAt",
            schema: "vessel",
            table: "notification_targets");

        migrationBuilder.DropColumn(
            name: "ConsecutiveFailureCount",
            schema: "vessel",
            table: "notification_targets");

        migrationBuilder.DropColumn(
            name: "DisabledReason",
            schema: "vessel",
            table: "notification_targets");

        migrationBuilder.DropColumn(
            name: "EventRoutesJson",
            schema: "vessel",
            table: "notification_targets");

        migrationBuilder.DropColumn(
            name: "SubscribedEvents",
            schema: "vessel",
            table: "notification_targets");

        migrationBuilder.DropColumn(
            name: "CancellationReason",
            schema: "vessel",
            table: "deployments");

        migrationBuilder.DropColumn(
            name: "DeployPhaseStartedAt",
            schema: "vessel",
            table: "deployments");

        migrationBuilder.DropColumn(
            name: "DispatchedAt",
            schema: "vessel",
            table: "deployments");

        migrationBuilder.DropColumn(
            name: "ParentDeploymentId",
            schema: "vessel",
            table: "deployments");

        migrationBuilder.DropColumn(
            name: "RollbackImage",
            schema: "vessel",
            table: "deployments");

        migrationBuilder.DropColumn(
            name: "Trigger",
            schema: "vessel",
            table: "deployments");

        migrationBuilder.DropColumn(
            name: "HealthCheckEnabled",
            schema: "vessel",
            table: "database_resources");

        migrationBuilder.DropColumn(
            name: "HealthCheckIntervalSeconds",
            schema: "vessel",
            table: "database_resources");

        migrationBuilder.DropColumn(
            name: "HealthCheckRetries",
            schema: "vessel",
            table: "database_resources");

        migrationBuilder.DropColumn(
            name: "HealthCheckStartPeriodSeconds",
            schema: "vessel",
            table: "database_resources");

        migrationBuilder.DropColumn(
            name: "HealthCheckTimeoutSeconds",
            schema: "vessel",
            table: "database_resources");

        migrationBuilder.DropColumn(
            name: "Image",
            schema: "vessel",
            table: "database_resources");

        migrationBuilder.DropColumn(
            name: "LastHealthCheckAt",
            schema: "vessel",
            table: "database_resources");

        migrationBuilder.DropColumn(
            name: "AcmeKey",
            schema: "vessel",
            table: "certificates");

        migrationBuilder.DropColumn(
            name: "ExpiryAlertSentFor",
            schema: "vessel",
            table: "certificates");

        migrationBuilder.DropColumn(
            name: "InUse",
            schema: "vessel",
            table: "certificates");

        migrationBuilder.DropColumn(
            name: "Resolver",
            schema: "vessel",
            table: "certificates");

        migrationBuilder.DropColumn(
            name: "ServerId",
            schema: "vessel",
            table: "certificates");

        migrationBuilder.DropColumn(
            name: "SubjectAlternativeNames",
            schema: "vessel",
            table: "certificates");

        migrationBuilder.DropColumn(
            name: "DatabaseSelection",
            schema: "vessel",
            table: "backup_schedules");

        migrationBuilder.DropColumn(
            name: "DisableLocalCopy",
            schema: "vessel",
            table: "backup_schedules");

        migrationBuilder.DropColumn(
            name: "DumpFormat",
            schema: "vessel",
            table: "backup_schedules");

        migrationBuilder.DropColumn(
            name: "FailingSince",
            schema: "vessel",
            table: "backup_schedules");

        migrationBuilder.DropColumn(
            name: "LastFailureNotifiedAt",
            schema: "vessel",
            table: "backup_schedules");

        migrationBuilder.DropColumn(
            name: "LastSucceededAt",
            schema: "vessel",
            table: "backup_schedules");

        migrationBuilder.DropColumn(
            name: "LocalRetentionDays",
            schema: "vessel",
            table: "backup_schedules");

        migrationBuilder.DropColumn(
            name: "LocalRetentionMaxBytes",
            schema: "vessel",
            table: "backup_schedules");

        migrationBuilder.DropColumn(
            name: "MissingAlertDays",
            schema: "vessel",
            table: "backup_schedules");

        migrationBuilder.DropColumn(
            name: "MissingAlertSentAt",
            schema: "vessel",
            table: "backup_schedules");

        migrationBuilder.DropColumn(
            name: "RemoteRetentionCount",
            schema: "vessel",
            table: "backup_schedules");

        migrationBuilder.DropColumn(
            name: "RemoteRetentionDays",
            schema: "vessel",
            table: "backup_schedules");

        migrationBuilder.DropColumn(
            name: "RemoteRetentionMaxBytes",
            schema: "vessel",
            table: "backup_schedules");

        migrationBuilder.DropColumn(
            name: "ArtifactFormat",
            schema: "vessel",
            table: "backup_executions");

        migrationBuilder.DropColumn(
            name: "DatabaseName",
            schema: "vessel",
            table: "backup_executions");

        migrationBuilder.DropColumn(
            name: "PathPrefix",
            schema: "vessel",
            table: "application_domains");

        migrationBuilder.DropColumn(
            name: "HostKind",
            schema: "vessel",
            table: "application_domains");

        migrationBuilder.DropColumn(
            name: "Policy",
            schema: "vessel",
            table: "application_domains");

        migrationBuilder.RenameColumn(
            name: "LastSyncedAt",
            schema: "vessel",
            table: "certificates",
            newName: "RenewalDueAt");

        migrationBuilder.AlterColumn<Guid>(
            name: "ApplicationId",
            schema: "vessel",
            table: "certificates",
            type: "uuid",
            nullable: false,
            defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
            oldClrType: typeof(Guid),
            oldType: "uuid",
            oldNullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "CertificateSecretReferenceId",
            schema: "vessel",
            table: "certificates",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "PrivateKeySecretReferenceId",
            schema: "vessel",
            table: "certificates",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddPrimaryKey(
            name: "PK_application_domains",
            schema: "vessel",
            table: "application_domains",
            columns: new[] { "ApplicationId", "DomainName" });

        migrationBuilder.CreateTable(
            name: "database_backup_policies",
            schema: "vessel",
            columns: table => new
            {
                DatabaseResourceId = table.Column<Guid>(type: "uuid", nullable: false),
                CronExpression = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                RetentionCount = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_database_backup_policies", x => new { x.DatabaseResourceId, x.CronExpression });
                table.ForeignKey(
                    name: "FK_database_backup_policies_database_resources_DatabaseResourc~",
                    column: x => x.DatabaseResourceId,
                    principalSchema: "vessel",
                    principalTable: "database_resources",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_deployments_ServerId",
            schema: "vessel",
            table: "deployments",
            column: "ServerId");

        migrationBuilder.CreateIndex(
            name: "IX_certificates_ApplicationId_Host",
            schema: "vessel",
            table: "certificates",
            columns: new[] { "ApplicationId", "Host" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_certificates_CertificateSecretReferenceId",
            schema: "vessel",
            table: "certificates",
            column: "CertificateSecretReferenceId");

        migrationBuilder.CreateIndex(
            name: "IX_certificates_PrivateKeySecretReferenceId",
            schema: "vessel",
            table: "certificates",
            column: "PrivateKeySecretReferenceId");

        migrationBuilder.CreateIndex(
            name: "IX_certificates_TeamId_RenewalDueAt",
            schema: "vessel",
            table: "certificates",
            columns: new[] { "TeamId", "RenewalDueAt" });

        migrationBuilder.AddForeignKey(
            name: "FK_backup_executions_backup_schedules_ScheduleId",
            schema: "vessel",
            table: "backup_executions",
            column: "ScheduleId",
            principalSchema: "vessel",
            principalTable: "backup_schedules",
            principalColumn: "Id",
            onDelete: ReferentialAction.SetNull);

        migrationBuilder.AddForeignKey(
            name: "FK_certificates_applications_ApplicationId",
            schema: "vessel",
            table: "certificates",
            column: "ApplicationId",
            principalSchema: "vessel",
            principalTable: "applications",
            principalColumn: "Id",
            onDelete: ReferentialAction.Cascade);

        migrationBuilder.AddForeignKey(
            name: "FK_certificates_secret_references_CertificateSecretReferenceId",
            schema: "vessel",
            table: "certificates",
            column: "CertificateSecretReferenceId",
            principalSchema: "vessel",
            principalTable: "secret_references",
            principalColumn: "Id",
            onDelete: ReferentialAction.SetNull);

        migrationBuilder.AddForeignKey(
            name: "FK_certificates_secret_references_PrivateKeySecretReferenceId",
            schema: "vessel",
            table: "certificates",
            column: "PrivateKeySecretReferenceId",
            principalSchema: "vessel",
            principalTable: "secret_references",
            principalColumn: "Id",
            onDelete: ReferentialAction.SetNull);
    }
}
