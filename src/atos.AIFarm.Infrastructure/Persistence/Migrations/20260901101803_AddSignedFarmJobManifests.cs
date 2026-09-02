using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace atos.AIFarm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSignedFarmJobManifests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AttemptId",
                schema: "aifarm",
                table: "farm_jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ManifestDigest",
                schema: "aifarm",
                table: "farm_jobs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ManifestExpiresAtUtc",
                schema: "aifarm",
                table: "farm_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ManifestIssuedAtUtc",
                schema: "aifarm",
                table: "farm_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ManifestPayload",
                schema: "aifarm",
                table: "farm_jobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ManifestSignature",
                schema: "aifarm",
                table: "farm_jobs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ManifestVersion",
                schema: "aifarm",
                table: "farm_jobs",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_farm_jobs_AttemptId",
                schema: "aifarm",
                table: "farm_jobs",
                column: "AttemptId",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_farm_jobs_manifest_complete",
                schema: "aifarm",
                table: "farm_jobs",
                sql: "(\"AttemptId\" IS NULL AND \"ManifestVersion\" IS NULL AND \"ManifestPayload\" IS NULL AND \"ManifestDigest\" IS NULL AND \"ManifestSignature\" IS NULL AND \"ManifestIssuedAtUtc\" IS NULL AND \"ManifestExpiresAtUtc\" IS NULL) OR (\"AttemptId\" IS NOT NULL AND \"ManifestVersion\" IS NOT NULL AND \"ManifestPayload\" IS NOT NULL AND \"ManifestDigest\" IS NOT NULL AND \"ManifestSignature\" IS NOT NULL AND \"ManifestIssuedAtUtc\" IS NOT NULL AND \"ManifestExpiresAtUtc\" IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_farm_jobs_AttemptId",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_farm_jobs_manifest_complete",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropColumn(
                name: "AttemptId",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropColumn(
                name: "ManifestDigest",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropColumn(
                name: "ManifestExpiresAtUtc",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropColumn(
                name: "ManifestIssuedAtUtc",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropColumn(
                name: "ManifestPayload",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropColumn(
                name: "ManifestSignature",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropColumn(
                name: "ManifestVersion",
                schema: "aifarm",
                table: "farm_jobs");
        }
    }
}
