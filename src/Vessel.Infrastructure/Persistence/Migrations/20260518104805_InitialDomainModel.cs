using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vessel.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class InitialDomainModel : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "vessel");

        migrationBuilder.CreateTable(
            name: "teams",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                IsPersonal = table.Column<bool>(type: "boolean", nullable: false),
                ShowOnboarding = table.Column<bool>(type: "boolean", nullable: false),
                CustomServerLimit = table.Column<int>(type: "integer", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_teams", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "users",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                EmailVerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                ExternalSubject = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                ForcePasswordReset = table.Column<bool>(type: "boolean", nullable: false),
                MarketingEmailsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_users", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "projects",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_projects", x => x.Id);
                table.ForeignKey(
                    name: "FK_projects_teams_TeamId",
                    column: x => x.TeamId,
                    principalSchema: "vessel",
                    principalTable: "teams",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "secret_references",
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
                Scope = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Key = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                ProviderReference = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                Policy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_secret_references", x => x.Id);
                table.ForeignKey(
                    name: "FK_secret_references_teams_TeamId",
                    column: x => x.TeamId,
                    principalSchema: "vessel",
                    principalTable: "teams",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "servers",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                Address = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                ConnectionType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Runtime = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Capabilities = table.Column<int>(type: "integer", nullable: false),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                LastReachableAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastUnreachableAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_servers", x => x.Id);
                table.ForeignKey(
                    name: "FK_servers_teams_TeamId",
                    column: x => x.TeamId,
                    principalSchema: "vessel",
                    principalTable: "teams",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "audit_logs",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TeamId = table.Column<Guid>(type: "uuid", nullable: true),
                ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                Action = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                Target = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                CorrelationId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                RedactedMetadataJson = table.Column<string>(type: "jsonb", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_audit_logs", x => x.Id);
                table.ForeignKey(
                    name: "FK_audit_logs_teams_TeamId",
                    column: x => x.TeamId,
                    principalSchema: "vessel",
                    principalTable: "teams",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_audit_logs_users_ActorUserId",
                    column: x => x.ActorUserId,
                    principalSchema: "vessel",
                    principalTable: "users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "team_memberships",
            schema: "vessel",
            columns: table => new
            {
                TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                Role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_team_memberships", x => new { x.TeamId, x.UserId });
                table.ForeignKey(
                    name: "FK_team_memberships_teams_TeamId",
                    column: x => x.TeamId,
                    principalSchema: "vessel",
                    principalTable: "teams",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_team_memberships_users_UserId",
                    column: x => x.UserId,
                    principalSchema: "vessel",
                    principalTable: "users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "environments",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_environments", x => x.Id);
                table.ForeignKey(
                    name: "FK_environments_projects_ProjectId",
                    column: x => x.ProjectId,
                    principalSchema: "vessel",
                    principalTable: "projects",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "settings",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Scope = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                TeamId = table.Column<Guid>(type: "uuid", nullable: true),
                ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                ResourceType = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                ResourceId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                Key = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                Value = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_settings", x => x.Id);
                table.ForeignKey(
                    name: "FK_settings_projects_ProjectId",
                    column: x => x.ProjectId,
                    principalSchema: "vessel",
                    principalTable: "projects",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_settings_teams_TeamId",
                    column: x => x.TeamId,
                    principalSchema: "vessel",
                    principalTable: "teams",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "notification_targets",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                Channel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                CredentialsReferenceId = table.Column<Guid>(type: "uuid", nullable: true),
                Policy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_notification_targets", x => x.Id);
                table.ForeignKey(
                    name: "FK_notification_targets_secret_references_CredentialsReference~",
                    column: x => x.CredentialsReferenceId,
                    principalSchema: "vessel",
                    principalTable: "secret_references",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_notification_targets_teams_TeamId",
                    column: x => x.TeamId,
                    principalSchema: "vessel",
                    principalTable: "teams",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "applications",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                EnvironmentId = table.Column<Guid>(type: "uuid", nullable: false),
                ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                GitSource = table.Column<string>(type: "character varying(2600)", maxLength: 2600, nullable: false),
                BuildConfiguration = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                RuntimeConfiguration = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                DeploymentSettings = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_applications", x => x.Id);
                table.ForeignKey(
                    name: "FK_applications_environments_EnvironmentId",
                    column: x => x.EnvironmentId,
                    principalSchema: "vessel",
                    principalTable: "environments",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_applications_servers_ServerId",
                    column: x => x.ServerId,
                    principalSchema: "vessel",
                    principalTable: "servers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "database_resources",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                EnvironmentId = table.Column<Guid>(type: "uuid", nullable: false),
                ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                Engine = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Version = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                Storage = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                CredentialsReferenceId = table.Column<Guid>(type: "uuid", nullable: false),
                HealthState = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_database_resources", x => x.Id);
                table.ForeignKey(
                    name: "FK_database_resources_environments_EnvironmentId",
                    column: x => x.EnvironmentId,
                    principalSchema: "vessel",
                    principalTable: "environments",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_database_resources_secret_references_CredentialsReferenceId",
                    column: x => x.CredentialsReferenceId,
                    principalSchema: "vessel",
                    principalTable: "secret_references",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_database_resources_servers_ServerId",
                    column: x => x.ServerId,
                    principalSchema: "vessel",
                    principalTable: "servers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "application_domains",
            schema: "vessel",
            columns: table => new
            {
                ApplicationId = table.Column<Guid>(type: "uuid", nullable: false),
                DomainName = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_application_domains", x => new { x.ApplicationId, x.DomainName });
                table.ForeignKey(
                    name: "FK_application_domains_applications_ApplicationId",
                    column: x => x.ApplicationId,
                    principalSchema: "vessel",
                    principalTable: "applications",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "deployments",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ApplicationId = table.Column<Guid>(type: "uuid", nullable: false),
                ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                CommitSha = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                ArtifactReference = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                RollbackDeploymentId = table.Column<Guid>(type: "uuid", nullable: true),
                IsRollback = table.Column<bool>(type: "boolean", nullable: false),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_deployments", x => x.Id);
                table.ForeignKey(
                    name: "FK_deployments_applications_ApplicationId",
                    column: x => x.ApplicationId,
                    principalSchema: "vessel",
                    principalTable: "applications",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_deployments_servers_ServerId",
                    column: x => x.ServerId,
                    principalSchema: "vessel",
                    principalTable: "servers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_deployments_users_ActorUserId",
                    column: x => x.ActorUserId,
                    principalSchema: "vessel",
                    principalTable: "users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

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

        migrationBuilder.CreateTable(
            name: "deployment_log_lines",
            schema: "vessel",
            columns: table => new
            {
                DeploymentId = table.Column<Guid>(type: "uuid", nullable: false),
                Sequence = table.Column<int>(type: "integer", nullable: false),
                Stream = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                Message = table.Column<string>(type: "text", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_deployment_log_lines", x => new { x.DeploymentId, x.Sequence });
                table.ForeignKey(
                    name: "FK_deployment_log_lines_deployments_DeploymentId",
                    column: x => x.DeploymentId,
                    principalSchema: "vessel",
                    principalTable: "deployments",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_applications_EnvironmentId_Name",
            schema: "vessel",
            table: "applications",
            columns: new[] { "EnvironmentId", "Name" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_applications_ServerId",
            schema: "vessel",
            table: "applications",
            column: "ServerId");

        migrationBuilder.CreateIndex(
            name: "IX_audit_logs_ActorUserId",
            schema: "vessel",
            table: "audit_logs",
            column: "ActorUserId");

        migrationBuilder.CreateIndex(
            name: "IX_audit_logs_TeamId_CreatedAt",
            schema: "vessel",
            table: "audit_logs",
            columns: new[] { "TeamId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_database_resources_CredentialsReferenceId",
            schema: "vessel",
            table: "database_resources",
            column: "CredentialsReferenceId");

        migrationBuilder.CreateIndex(
            name: "IX_database_resources_EnvironmentId",
            schema: "vessel",
            table: "database_resources",
            column: "EnvironmentId");

        migrationBuilder.CreateIndex(
            name: "IX_database_resources_ServerId",
            schema: "vessel",
            table: "database_resources",
            column: "ServerId");

        migrationBuilder.CreateIndex(
            name: "IX_deployments_ActorUserId",
            schema: "vessel",
            table: "deployments",
            column: "ActorUserId");

        migrationBuilder.CreateIndex(
            name: "IX_deployments_ApplicationId_CreatedAt",
            schema: "vessel",
            table: "deployments",
            columns: new[] { "ApplicationId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_deployments_ServerId",
            schema: "vessel",
            table: "deployments",
            column: "ServerId");

        migrationBuilder.CreateIndex(
            name: "IX_environments_ProjectId_Name",
            schema: "vessel",
            table: "environments",
            columns: new[] { "ProjectId", "Name" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_notification_targets_CredentialsReferenceId",
            schema: "vessel",
            table: "notification_targets",
            column: "CredentialsReferenceId");

        migrationBuilder.CreateIndex(
            name: "IX_notification_targets_TeamId_Name",
            schema: "vessel",
            table: "notification_targets",
            columns: new[] { "TeamId", "Name" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_projects_TeamId_Name",
            schema: "vessel",
            table: "projects",
            columns: new[] { "TeamId", "Name" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_secret_references_TeamId_Scope_Key",
            schema: "vessel",
            table: "secret_references",
            columns: new[] { "TeamId", "Scope", "Key" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_servers_TeamId_Name",
            schema: "vessel",
            table: "servers",
            columns: new[] { "TeamId", "Name" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_settings_ProjectId",
            schema: "vessel",
            table: "settings",
            column: "ProjectId");

        migrationBuilder.CreateIndex(
            name: "IX_settings_Scope_TeamId_ProjectId_ResourceType_ResourceId_Key",
            schema: "vessel",
            table: "settings",
            columns: new[] { "Scope", "TeamId", "ProjectId", "ResourceType", "ResourceId", "Key" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_settings_TeamId",
            schema: "vessel",
            table: "settings",
            column: "TeamId");

        migrationBuilder.CreateIndex(
            name: "IX_team_memberships_UserId",
            schema: "vessel",
            table: "team_memberships",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_users_Email",
            schema: "vessel",
            table: "users",
            column: "Email",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "application_domains",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "audit_logs",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "database_backup_policies",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "deployment_log_lines",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "notification_targets",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "settings",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "team_memberships",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "database_resources",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "deployments",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "secret_references",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "applications",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "users",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "environments",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "servers",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "projects",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "teams",
            schema: "vessel");
    }
}
