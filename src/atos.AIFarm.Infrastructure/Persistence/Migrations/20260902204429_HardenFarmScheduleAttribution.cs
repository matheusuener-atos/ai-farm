using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace atos.AIFarm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HardenFarmScheduleAttribution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_farm_jobs_schedule_attribution_complete",
                schema: "aifarm",
                table: "farm_jobs",
                sql: "(\"ScheduleId\" IS NULL AND \"ScheduledForUtc\" IS NULL) OR (\"ScheduleId\" IS NOT NULL AND \"ScheduledForUtc\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_farm_jobs_single_automation_origin",
                schema: "aifarm",
                table: "farm_jobs",
                sql: "\"ApiTokenId\" IS NULL OR \"ScheduleId\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_farm_jobs_schedule_attribution_complete",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_farm_jobs_single_automation_origin",
                schema: "aifarm",
                table: "farm_jobs");
        }
    }
}
