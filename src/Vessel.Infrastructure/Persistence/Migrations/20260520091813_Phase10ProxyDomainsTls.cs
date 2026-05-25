using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vessel.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class Phase10ProxyDomainsTls : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "Canonical",
            schema: "vessel",
            table: "application_domains",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.Sql("""
            UPDATE vessel.application_domains AS domain
            SET "Canonical" = TRUE
            FROM (
                SELECT "ApplicationId", "DomainName",
                       ROW_NUMBER() OVER (PARTITION BY "ApplicationId" ORDER BY "DomainName") AS row_number
                FROM vessel.application_domains
            ) AS ranked
            WHERE domain."ApplicationId" = ranked."ApplicationId"
              AND domain."DomainName" = ranked."DomainName"
              AND ranked.row_number = 1;
            """);

        migrationBuilder.AddColumn<bool>(
            name: "RedirectToCanonical",
            schema: "vessel",
            table: "application_domains",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<int>(
            name: "TargetPort",
            schema: "vessel",
            table: "application_domains",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "TlsEnabled",
            schema: "vessel",
            table: "application_domains",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.CreateTable(
            name: "certificates",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                ApplicationId = table.Column<Guid>(type: "uuid", nullable: false),
                Host = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                IssuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                RenewalDueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastAttemptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                CertificateSecretReferenceId = table.Column<Guid>(type: "uuid", nullable: true),
                PrivateKeySecretReferenceId = table.Column<Guid>(type: "uuid", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_certificates", x => x.Id);
                table.ForeignKey(
                    name: "FK_certificates_applications_ApplicationId",
                    column: x => x.ApplicationId,
                    principalSchema: "vessel",
                    principalTable: "applications",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_certificates_secret_references_CertificateSecretReferenceId",
                    column: x => x.CertificateSecretReferenceId,
                    principalSchema: "vessel",
                    principalTable: "secret_references",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_certificates_secret_references_PrivateKeySecretReferenceId",
                    column: x => x.PrivateKeySecretReferenceId,
                    principalSchema: "vessel",
                    principalTable: "secret_references",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "proxy_configuration_versions",
            schema: "vessel",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Version = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                ConfigurationHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Configuration = table.Column<string>(type: "text", nullable: false),
                PreviousVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                ValidationError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                ApplyError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                ValidatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                AppliedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                RolledBackAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_proxy_configuration_versions", x => x.Id);
                table.ForeignKey(
                    name: "FK_proxy_configuration_versions_previous_version",
                    column: x => x.PreviousVersionId,
                    principalSchema: "vessel",
                    principalTable: "proxy_configuration_versions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_proxy_configuration_versions_servers_ServerId",
                    column: x => x.ServerId,
                    principalSchema: "vessel",
                    principalTable: "servers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

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

        migrationBuilder.CreateIndex(
            name: "IX_proxy_configuration_versions_ServerId_ConfigurationHash",
            schema: "vessel",
            table: "proxy_configuration_versions",
            columns: new[] { "ServerId", "ConfigurationHash" });

        migrationBuilder.CreateIndex(
            name: "IX_proxy_configuration_versions_PreviousVersionId",
            schema: "vessel",
            table: "proxy_configuration_versions",
            column: "PreviousVersionId");

        migrationBuilder.CreateIndex(
            name: "IX_proxy_configuration_versions_ServerId_CreatedAt",
            schema: "vessel",
            table: "proxy_configuration_versions",
            columns: new[] { "ServerId", "CreatedAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "certificates",
            schema: "vessel");

        migrationBuilder.DropTable(
            name: "proxy_configuration_versions",
            schema: "vessel");

        migrationBuilder.DropColumn(
            name: "Canonical",
            schema: "vessel",
            table: "application_domains");

        migrationBuilder.DropColumn(
            name: "RedirectToCanonical",
            schema: "vessel",
            table: "application_domains");

        migrationBuilder.DropColumn(
            name: "TargetPort",
            schema: "vessel",
            table: "application_domains");

        migrationBuilder.DropColumn(
            name: "TlsEnabled",
            schema: "vessel",
            table: "application_domains");
    }
}
