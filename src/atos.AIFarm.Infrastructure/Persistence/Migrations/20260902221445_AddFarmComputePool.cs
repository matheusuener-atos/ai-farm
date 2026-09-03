using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace atos.AIFarm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFarmComputePool : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_farm_nodes_AccountId",
                schema: "aifarm",
                table: "farm_nodes");

            migrationBuilder.DropIndex(
                name: "IX_farm_jobs_NodeId",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropIndex(
                name: "UX_farm_jobs_account_active",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.CreateIndex(
                name: "IX_farm_nodes_AccountId_Name",
                schema: "aifarm",
                table: "farm_nodes",
                columns: new[] { "AccountId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_farm_nodes_account_embedded",
                schema: "aifarm",
                table: "farm_nodes",
                column: "AccountId",
                unique: true,
                filter: "\"ConnectionKind\" = 'Embedded'");

            migrationBuilder.CreateIndex(
                name: "UX_farm_jobs_node_active",
                schema: "aifarm",
                table: "farm_jobs",
                column: "NodeId",
                unique: true,
                filter: "\"Status\" IN ('Queued', 'Offered', 'Accepted', 'Running', 'Uploading', 'Verifying')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_farm_nodes_AccountId_Name",
                schema: "aifarm",
                table: "farm_nodes");

            migrationBuilder.DropIndex(
                name: "UX_farm_nodes_account_embedded",
                schema: "aifarm",
                table: "farm_nodes");

            migrationBuilder.DropIndex(
                name: "UX_farm_jobs_node_active",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.CreateIndex(
                name: "IX_farm_nodes_AccountId",
                schema: "aifarm",
                table: "farm_nodes",
                column: "AccountId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_farm_jobs_NodeId",
                schema: "aifarm",
                table: "farm_jobs",
                column: "NodeId");

            migrationBuilder.CreateIndex(
                name: "UX_farm_jobs_account_active",
                schema: "aifarm",
                table: "farm_jobs",
                column: "AccountId",
                unique: true,
                filter: "\"Status\" IN ('Queued', 'Offered', 'Accepted', 'Running', 'Uploading', 'Verifying')");
        }
    }
}
