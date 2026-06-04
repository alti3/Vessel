using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vessel.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class _20260604133232_Phase13Notifications : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ConfigurationJson",
            schema: "vessel",
            table: "notification_targets",
            type: "jsonb",
            nullable: false,
            defaultValue: "{}");

        migrationBuilder.CreateTable(
            name: "notification_events",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: true),
                EventType = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                Severity = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                TargetType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                TargetId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                Title = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                ResourceUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                AttemptCount = table.Column<int>(type: "integer", nullable: false),
                LastAttemptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                FailureReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_notification_events", x => x.Id);
                table.ForeignKey(
                    name: "FK_notification_events_teams_TeamId",
                    column: x => x.TeamId,
                    principalSchema: "vessel",
                    principalTable: "teams",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_notification_events_users_UserId",
                    column: x => x.UserId,
                    principalSchema: "vessel",
                    principalTable: "users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "in_app_notifications",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                EventId = table.Column<Guid>(type: "uuid", nullable: false),
                TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: true),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                ReadAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_in_app_notifications", x => x.Id);
                table.ForeignKey(
                    name: "FK_in_app_notifications_notification_events_EventId",
                    column: x => x.EventId,
                    principalSchema: "vessel",
                    principalTable: "notification_events",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_in_app_notifications_teams_TeamId",
                    column: x => x.TeamId,
                    principalSchema: "vessel",
                    principalTable: "teams",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_in_app_notifications_users_UserId",
                    column: x => x.UserId,
                    principalSchema: "vessel",
                    principalTable: "users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "notification_delivery_attempts",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                EventId = table.Column<Guid>(type: "uuid", nullable: false),
                TargetId = table.Column<Guid>(type: "uuid", nullable: false),
                Channel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                RetryAfter = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                FailureReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                ProviderMessageId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_notification_delivery_attempts", x => x.Id);
                table.ForeignKey(
                    name: "FK_notification_delivery_attempts_notification_events_EventId",
                    column: x => x.EventId,
                    principalSchema: "vessel",
                    principalTable: "notification_events",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_notification_delivery_attempts_notification_targets_TargetId",
                    column: x => x.TargetId,
                    principalSchema: "vessel",
                    principalTable: "notification_targets",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_in_app_notifications_EventId",
            schema: "vessel",
            table: "in_app_notifications",
            column: "EventId");

        migrationBuilder.CreateIndex(
            name: "IX_in_app_notifications_TeamId_Status_CreatedAt",
            schema: "vessel",
            table: "in_app_notifications",
            columns: new[] { "TeamId", "Status", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_in_app_notifications_UserId_Status_CreatedAt",
            schema: "vessel",
            table: "in_app_notifications",
            columns: new[] { "UserId", "Status", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_notification_delivery_attempts_EventId_TargetId_AttemptNumb~",
            schema: "vessel",
            table: "notification_delivery_attempts",
            columns: new[] { "EventId", "TargetId", "AttemptNumber" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_notification_delivery_attempts_Status_RetryAfter",
            schema: "vessel",
            table: "notification_delivery_attempts",
            columns: new[] { "Status", "RetryAfter" });

        migrationBuilder.CreateIndex(
            name: "IX_notification_delivery_attempts_TargetId",
            schema: "vessel",
            table: "notification_delivery_attempts",
            column: "TargetId");

        migrationBuilder.CreateIndex(
            name: "IX_notification_events_EventType_CreatedAt",
            schema: "vessel",
            table: "notification_events",
            columns: new[] { "EventType", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_notification_events_TeamId_CreatedAt",
            schema: "vessel",
            table: "notification_events",
            columns: new[] { "TeamId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_notification_events_TeamId_Status_CreatedAt",
            schema: "vessel",
            table: "notification_events",
            columns: new[] { "TeamId", "Status", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_notification_events_UserId",
            schema: "vessel",
            table: "notification_events",
            column: "UserId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "in_app_notifications",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "notification_delivery_attempts",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "notification_events",
            schema: "vessel");

        migrationBuilder.DropColumn(
            name: "ConfigurationJson",
            schema: "vessel",
            table: "notification_targets");
    }
}
