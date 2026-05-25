using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vessel.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class Phase9WebhooksAndPreviews : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "PreviewId",
            schema: "vessel",
            table: "deployments",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "WebhookEventId",
            schema: "vessel",
            table: "deployments",
            type: "uuid",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "application_previews",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ApplicationId = table.Column<Guid>(type: "uuid", nullable: false),
                Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                PullRequestNumber = table.Column<int>(type: "integer", nullable: false),
                SourceBranch = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                TargetBranch = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                CommitSha = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                PullRequestUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                Title = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                PreviewUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_application_previews", x => x.Id);
                table.ForeignKey(
                    name: "FK_application_previews_applications_ApplicationId",
                    column: x => x.ApplicationId,
                    principalSchema: "vessel",
                    principalTable: "applications",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "application_webhook_configurations",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ApplicationId = table.Column<Guid>(type: "uuid", nullable: false),
                Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                SecretReferenceId = table.Column<Guid>(type: "uuid", nullable: false),
                IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                LastRotatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_application_webhook_configurations", x => x.Id);
                table.ForeignKey(
                    name: "FK_application_webhook_configurations_applications_Application~",
                    column: x => x.ApplicationId,
                    principalSchema: "vessel",
                    principalTable: "applications",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_application_webhook_configurations_secret_references_Secret~",
                    column: x => x.SecretReferenceId,
                    principalSchema: "vessel",
                    principalTable: "secret_references",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "webhook_events",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                EventType = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                ProviderEventId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                DedupeKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                PayloadReference = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                PayloadJson = table.Column<string>(type: "text", nullable: false),
                SignatureStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                FailureReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                ApplicationId = table.Column<Guid>(type: "uuid", nullable: true),
                DeploymentId = table.Column<Guid>(type: "uuid", nullable: true),
                PreviewId = table.Column<Guid>(type: "uuid", nullable: true),
                ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_webhook_events", x => x.Id);
                table.ForeignKey(
                    name: "FK_webhook_events_applications_ApplicationId",
                    column: x => x.ApplicationId,
                    principalSchema: "vessel",
                    principalTable: "applications",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateIndex(
            name: "IX_deployments_PreviewId",
            schema: "vessel",
            table: "deployments",
            column: "PreviewId");

        migrationBuilder.CreateIndex(
            name: "IX_deployments_WebhookEventId",
            schema: "vessel",
            table: "deployments",
            column: "WebhookEventId");

        migrationBuilder.CreateIndex(
            name: "IX_application_previews_ApplicationId_Provider_PullRequestNumb~",
            schema: "vessel",
            table: "application_previews",
            columns: new[] { "ApplicationId", "Provider", "PullRequestNumber" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_application_webhook_configurations_ApplicationId_Provider",
            schema: "vessel",
            table: "application_webhook_configurations",
            columns: new[] { "ApplicationId", "Provider" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_application_webhook_configurations_SecretReferenceId",
            schema: "vessel",
            table: "application_webhook_configurations",
            column: "SecretReferenceId");

        migrationBuilder.CreateIndex(
            name: "IX_webhook_events_ApplicationId",
            schema: "vessel",
            table: "webhook_events",
            column: "ApplicationId");

        migrationBuilder.CreateIndex(
            name: "IX_webhook_events_DedupeKey",
            schema: "vessel",
            table: "webhook_events",
            column: "DedupeKey",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_webhook_events_Provider_EventType_CreatedAt",
            schema: "vessel",
            table: "webhook_events",
            columns: new[] { "Provider", "EventType", "CreatedAt" });

        migrationBuilder.AddForeignKey(
            name: "FK_deployments_application_previews_PreviewId",
            schema: "vessel",
            table: "deployments",
            column: "PreviewId",
            principalSchema: "vessel",
            principalTable: "application_previews",
            principalColumn: "Id",
            onDelete: ReferentialAction.SetNull);

        migrationBuilder.AddForeignKey(
            name: "FK_deployments_webhook_events_WebhookEventId",
            schema: "vessel",
            table: "deployments",
            column: "WebhookEventId",
            principalSchema: "vessel",
            principalTable: "webhook_events",
            principalColumn: "Id",
            onDelete: ReferentialAction.SetNull);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_deployments_application_previews_PreviewId",
            schema: "vessel",
            table: "deployments");

        migrationBuilder.DropForeignKey(
            name: "FK_deployments_webhook_events_WebhookEventId",
            schema: "vessel",
            table: "deployments");

        migrationBuilder.DropTable(
            name: "application_previews",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "application_webhook_configurations",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "webhook_events",
            schema: "vessel");

        migrationBuilder.DropIndex(
            name: "IX_deployments_PreviewId",
            schema: "vessel",
            table: "deployments");

        migrationBuilder.DropIndex(
            name: "IX_deployments_WebhookEventId",
            schema: "vessel",
            table: "deployments");

        migrationBuilder.DropColumn(
            name: "PreviewId",
            schema: "vessel",
            table: "deployments");

        migrationBuilder.DropColumn(
            name: "WebhookEventId",
            schema: "vessel",
            table: "deployments");
    }
}
