using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace atos.AIFarm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PreventConcurrentFarmJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "UX_farm_jobs_account_active",
                schema: "aifarm",
                table: "farm_jobs",
                column: "AccountId",
                unique: true,
                filter: "\"Status\" IN ('Queued', 'Running', 'Verifying')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_farm_jobs_account_active",
                schema: "aifarm",
                table: "farm_jobs");
        }
    }
}
