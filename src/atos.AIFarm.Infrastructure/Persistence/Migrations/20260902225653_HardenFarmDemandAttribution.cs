using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace atos.AIFarm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HardenFarmDemandAttribution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_farm_jobs_single_automation_origin",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.AddCheckConstraint(
                name: "CK_farm_jobs_single_automation_origin",
                schema: "aifarm",
                table: "farm_jobs",
                sql: "(\"ApiTokenId\" IS NULL OR (\"ScheduleId\" IS NULL AND \"DemandId\" IS NULL)) AND (\"ScheduleId\" IS NULL OR \"DemandId\" IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_farm_jobs_single_automation_origin",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.AddCheckConstraint(
                name: "CK_farm_jobs_single_automation_origin",
                schema: "aifarm",
                table: "farm_jobs",
                sql: "\"ApiTokenId\" IS NULL OR \"ScheduleId\" IS NULL");
        }
    }
}
