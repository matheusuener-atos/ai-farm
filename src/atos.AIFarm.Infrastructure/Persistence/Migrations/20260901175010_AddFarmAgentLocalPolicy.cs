using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace atos.AIFarm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFarmAgentLocalPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AgentAcceptJobs",
                schema: "aifarm",
                table: "farm_nodes",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "AgentMaxLogicalProcessors",
                schema: "aifarm",
                table: "farm_nodes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AgentMaxMemoryBytes",
                schema: "aifarm",
                table: "farm_nodes",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_farm_nodes_agent_cpu_limit",
                schema: "aifarm",
                table: "farm_nodes",
                sql: "\"AgentMaxLogicalProcessors\" IS NULL OR (\"AgentMaxLogicalProcessors\" >= 1 AND \"AgentMaxLogicalProcessors\" <= \"LogicalProcessors\")");

            migrationBuilder.AddCheckConstraint(
                name: "CK_farm_nodes_agent_memory_limit",
                schema: "aifarm",
                table: "farm_nodes",
                sql: "\"AgentMaxMemoryBytes\" IS NULL OR (\"AgentMaxMemoryBytes\" >= 1 AND \"AgentMaxMemoryBytes\" <= \"MemoryBytes\")");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_farm_nodes_agent_cpu_limit",
                schema: "aifarm",
                table: "farm_nodes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_farm_nodes_agent_memory_limit",
                schema: "aifarm",
                table: "farm_nodes");

            migrationBuilder.DropColumn(
                name: "AgentAcceptJobs",
                schema: "aifarm",
                table: "farm_nodes");

            migrationBuilder.DropColumn(
                name: "AgentMaxLogicalProcessors",
                schema: "aifarm",
                table: "farm_nodes");

            migrationBuilder.DropColumn(
                name: "AgentMaxMemoryBytes",
                schema: "aifarm",
                table: "farm_nodes");
        }
    }
}
