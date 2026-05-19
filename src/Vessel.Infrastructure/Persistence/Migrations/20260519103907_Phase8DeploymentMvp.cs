using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vessel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase8DeploymentMvp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CancellationRequestedAt",
                schema: "vessel",
                table: "deployments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CommitBranch",
                schema: "vessel",
                table: "deployments",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CommitMessage",
                schema: "vessel",
                table: "deployments",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConfigurationSnapshotReference",
                schema: "vessel",
                table: "deployments",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RepositoryUrl",
                schema: "vessel",
                table: "deployments",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CancellationRequestedAt",
                schema: "vessel",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "CommitBranch",
                schema: "vessel",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "CommitMessage",
                schema: "vessel",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "ConfigurationSnapshotReference",
                schema: "vessel",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "RepositoryUrl",
                schema: "vessel",
                table: "deployments");
        }
    }
}
