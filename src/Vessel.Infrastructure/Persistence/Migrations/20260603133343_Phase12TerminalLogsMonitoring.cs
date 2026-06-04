using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vessel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _20260603133343_Phase12TerminalLogsMonitoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "terminal_sessions",
                schema: "vessel",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ContainerName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Command = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Columns = table.Column<int>(type: "integer", nullable: false),
                    Rows = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastActivityAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FailureReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_terminal_sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_terminal_sessions_servers_ServerId",
                        column: x => x.ServerId,
                        principalSchema: "vessel",
                        principalTable: "servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_terminal_sessions_teams_TeamId",
                        column: x => x.TeamId,
                        principalSchema: "vessel",
                        principalTable: "teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_terminal_sessions_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalSchema: "vessel",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_terminal_sessions_OwnerUserId_StartedAt",
                schema: "vessel",
                table: "terminal_sessions",
                columns: new[] { "OwnerUserId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_terminal_sessions_ServerId",
                schema: "vessel",
                table: "terminal_sessions",
                column: "ServerId");

            migrationBuilder.CreateIndex(
                name: "IX_terminal_sessions_TeamId_Status_StartedAt",
                schema: "vessel",
                table: "terminal_sessions",
                columns: new[] { "TeamId", "Status", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "terminal_sessions",
                schema: "vessel");
        }
    }
}
