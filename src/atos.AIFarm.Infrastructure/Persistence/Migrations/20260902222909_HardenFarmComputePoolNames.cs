using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace atos.AIFarm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HardenFarmComputePoolNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_farm_nodes_AccountId_Name",
                schema: "aifarm",
                table: "farm_nodes");

            migrationBuilder.CreateIndex(
                name: "IX_farm_nodes_AccountId_Name",
                schema: "aifarm",
                table: "farm_nodes",
                columns: new[] { "AccountId", "Name" },
                unique: true,
                filter: "\"ConnectionKind\" = 'Agent'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_farm_nodes_AccountId_Name",
                schema: "aifarm",
                table: "farm_nodes");

            migrationBuilder.CreateIndex(
                name: "IX_farm_nodes_AccountId_Name",
                schema: "aifarm",
                table: "farm_nodes",
                columns: new[] { "AccountId", "Name" },
                unique: true);
        }
    }
}
