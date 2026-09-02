using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace atos.AIFarm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFarmUsefulJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DemandLabel",
                schema: "aifarm",
                table: "farm_jobs",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InputDigest",
                schema: "aifarm",
                table: "farm_jobs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InputPayload",
                schema: "aifarm",
                table: "farm_jobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "InputSizeBytes",
                schema: "aifarm",
                table: "farm_jobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InputVersion",
                schema: "aifarm",
                table: "farm_jobs",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OutputDigest",
                schema: "aifarm",
                table: "farm_jobs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OutputPayload",
                schema: "aifarm",
                table: "farm_jobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OutputSizeBytes",
                schema: "aifarm",
                table: "farm_jobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OutputVersion",
                schema: "aifarm",
                table: "farm_jobs",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_farm_jobs_useful_input_complete",
                schema: "aifarm",
                table: "farm_jobs",
                sql: "(\"InputVersion\" IS NULL AND \"InputPayload\" IS NULL AND \"InputSizeBytes\" IS NULL AND \"InputDigest\" IS NULL AND \"DemandLabel\" IS NULL) OR (\"InputVersion\" IS NOT NULL AND \"InputPayload\" IS NOT NULL AND \"InputSizeBytes\" > 0 AND \"InputDigest\" IS NOT NULL AND \"DemandLabel\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_farm_jobs_useful_output_complete",
                schema: "aifarm",
                table: "farm_jobs",
                sql: "(\"OutputVersion\" IS NULL AND \"OutputPayload\" IS NULL AND \"OutputSizeBytes\" IS NULL AND \"OutputDigest\" IS NULL) OR (\"OutputVersion\" IS NOT NULL AND \"OutputPayload\" IS NOT NULL AND \"OutputSizeBytes\" > 0 AND \"OutputDigest\" IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_farm_jobs_useful_input_complete",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_farm_jobs_useful_output_complete",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropColumn(
                name: "DemandLabel",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropColumn(
                name: "InputDigest",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropColumn(
                name: "InputPayload",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropColumn(
                name: "InputSizeBytes",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropColumn(
                name: "InputVersion",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropColumn(
                name: "OutputDigest",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropColumn(
                name: "OutputPayload",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropColumn(
                name: "OutputSizeBytes",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropColumn(
                name: "OutputVersion",
                schema: "aifarm",
                table: "farm_jobs");
        }
    }
}
