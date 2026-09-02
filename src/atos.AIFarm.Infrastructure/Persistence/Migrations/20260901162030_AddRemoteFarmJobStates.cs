using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace atos.AIFarm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRemoteFarmJobStates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_farm_jobs_account_active",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_farm_jobs_status",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.CreateIndex(
                name: "UX_farm_jobs_account_active",
                schema: "aifarm",
                table: "farm_jobs",
                column: "AccountId",
                unique: true,
                filter: "\"Status\" IN ('Queued', 'Offered', 'Accepted', 'Running', 'Uploading', 'Verifying')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_farm_jobs_status",
                schema: "aifarm",
                table: "farm_jobs",
                sql: "\"Status\" IN ('Queued', 'Offered', 'Accepted', 'Running', 'Uploading', 'Verifying', 'Completed', 'Failed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_farm_jobs_account_active",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_farm_jobs_status",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.CreateIndex(
                name: "UX_farm_jobs_account_active",
                schema: "aifarm",
                table: "farm_jobs",
                column: "AccountId",
                unique: true,
                filter: "\"Status\" IN ('Queued', 'Running', 'Verifying')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_farm_jobs_status",
                schema: "aifarm",
                table: "farm_jobs",
                sql: "\"Status\" IN ('Queued', 'Running', 'Verifying', 'Completed', 'Failed')");
        }
    }
}
