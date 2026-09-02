using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace atos.AIFarm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFarmJobEvidenceBundle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EvidenceCreatedAtUtc",
                schema: "aifarm",
                table: "farm_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EvidenceDigest",
                schema: "aifarm",
                table: "farm_jobs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EvidenceKeyId",
                schema: "aifarm",
                table: "farm_jobs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EvidencePayload",
                schema: "aifarm",
                table: "farm_jobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EvidenceSignature",
                schema: "aifarm",
                table: "farm_jobs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EvidenceVersion",
                schema: "aifarm",
                table: "farm_jobs",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_farm_jobs_evidence_complete",
                schema: "aifarm",
                table: "farm_jobs",
                sql: "(\"EvidenceVersion\" IS NULL AND \"EvidencePayload\" IS NULL AND \"EvidenceDigest\" IS NULL AND \"EvidenceSignature\" IS NULL AND \"EvidenceKeyId\" IS NULL AND \"EvidenceCreatedAtUtc\" IS NULL) OR (\"EvidenceVersion\" IS NOT NULL AND \"EvidencePayload\" IS NOT NULL AND \"EvidenceDigest\" IS NOT NULL AND \"EvidenceSignature\" IS NOT NULL AND \"EvidenceKeyId\" IS NOT NULL AND \"EvidenceCreatedAtUtc\" IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_farm_jobs_evidence_complete",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropColumn(
                name: "EvidenceCreatedAtUtc",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropColumn(
                name: "EvidenceDigest",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropColumn(
                name: "EvidenceKeyId",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropColumn(
                name: "EvidencePayload",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropColumn(
                name: "EvidenceSignature",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropColumn(
                name: "EvidenceVersion",
                schema: "aifarm",
                table: "farm_jobs");
        }
    }
}
