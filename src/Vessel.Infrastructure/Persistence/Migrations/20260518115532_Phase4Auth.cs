using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vessel.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class Phase4Auth : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "FailedLoginCount",
            schema: "vessel",
            table: "users",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "LockoutEndAt",
            schema: "vessel",
            table: "users",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PasswordHash",
            schema: "vessel",
            table: "users",
            type: "character varying(512)",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "PasswordResetTokenExpiresAt",
            schema: "vessel",
            table: "users",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PasswordResetTokenHash",
            schema: "vessel",
            table: "users",
            type: "character varying(128)",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "TwoFactorConfirmedAt",
            schema: "vessel",
            table: "users",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "TwoFactorRecoveryCodeHashes",
            schema: "vessel",
            table: "users",
            type: "character varying(4000)",
            maxLength: 4000,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "TwoFactorSecret",
            schema: "vessel",
            table: "users",
            type: "character varying(128)",
            maxLength: 128,
            nullable: true);

        migrationBuilder.CreateTable(
            name: "personal_access_tokens",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                TokenHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Scopes = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_personal_access_tokens", x => x.Id);
                table.ForeignKey(
                    name: "FK_personal_access_tokens_teams_TeamId",
                    column: x => x.TeamId,
                    principalSchema: "vessel",
                    principalTable: "teams",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_personal_access_tokens_users_UserId",
                    column: x => x.UserId,
                    principalSchema: "vessel",
                    principalTable: "users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "team_invitations",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                Role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                TokenHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                AcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_team_invitations", x => x.Id);
                table.ForeignKey(
                    name: "FK_team_invitations_teams_TeamId",
                    column: x => x.TeamId,
                    principalSchema: "vessel",
                    principalTable: "teams",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_personal_access_tokens_TeamId",
            schema: "vessel",
            table: "personal_access_tokens",
            column: "TeamId");

        migrationBuilder.CreateIndex(
            name: "IX_personal_access_tokens_TokenHash",
            schema: "vessel",
            table: "personal_access_tokens",
            column: "TokenHash",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_personal_access_tokens_UserId_CreatedAt",
            schema: "vessel",
            table: "personal_access_tokens",
            columns: new[] { "UserId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_team_invitations_TeamId_Email",
            schema: "vessel",
            table: "team_invitations",
            columns: new[] { "TeamId", "Email" });

        migrationBuilder.CreateIndex(
            name: "IX_team_invitations_TokenHash",
            schema: "vessel",
            table: "team_invitations",
            column: "TokenHash",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "personal_access_tokens",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "team_invitations",
            schema: "vessel");

        migrationBuilder.DropColumn(
            name: "FailedLoginCount",
            schema: "vessel",
            table: "users");

        migrationBuilder.DropColumn(
            name: "LockoutEndAt",
            schema: "vessel",
            table: "users");

        migrationBuilder.DropColumn(
            name: "PasswordHash",
            schema: "vessel",
            table: "users");

        migrationBuilder.DropColumn(
            name: "PasswordResetTokenExpiresAt",
            schema: "vessel",
            table: "users");

        migrationBuilder.DropColumn(
            name: "PasswordResetTokenHash",
            schema: "vessel",
            table: "users");

        migrationBuilder.DropColumn(
            name: "TwoFactorConfirmedAt",
            schema: "vessel",
            table: "users");

        migrationBuilder.DropColumn(
            name: "TwoFactorRecoveryCodeHashes",
            schema: "vessel",
            table: "users");

        migrationBuilder.DropColumn(
            name: "TwoFactorSecret",
            schema: "vessel",
            table: "users");
    }
}
