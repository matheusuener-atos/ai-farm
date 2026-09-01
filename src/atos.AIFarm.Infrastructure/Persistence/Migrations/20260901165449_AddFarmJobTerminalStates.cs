using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace atos.AIFarm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFarmJobTerminalStates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_farm_jobs_status",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.AddCheckConstraint(
                name: "CK_farm_jobs_status",
                schema: "aifarm",
                table: "farm_jobs",
                sql: "\"Status\" IN ('Queued', 'Offered', 'Accepted', 'Running', 'Uploading', 'Verifying', 'Completed', 'Failed', 'Cancelled', 'TimedOut', 'Rejected')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_farm_jobs_status",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.AddCheckConstraint(
                name: "CK_farm_jobs_status",
                schema: "aifarm",
                table: "farm_jobs",
                sql: "\"Status\" IN ('Queued', 'Offered', 'Accepted', 'Running', 'Uploading', 'Verifying', 'Completed', 'Failed')");
        }
    }
}
