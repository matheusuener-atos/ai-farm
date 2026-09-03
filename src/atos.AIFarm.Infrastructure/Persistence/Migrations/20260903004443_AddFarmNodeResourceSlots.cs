using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace atos.AIFarm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFarmNodeResourceSlots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_farm_jobs_node_active",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.AddColumn<int>(
                name: "ReservedLogicalProcessors",
                schema: "aifarm",
                table: "farm_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<long>(
                name: "ReservedMemoryBytes",
                schema: "aifarm",
                table: "farm_jobs",
                type: "bigint",
                nullable: false,
                defaultValue: 67108864L);

            migrationBuilder.Sql(
                """
                UPDATE aifarm.farm_jobs
                SET "ReservedLogicalProcessors" = COALESCE(
                        NULLIF(("ManifestPayload"::jsonb ->> 'max_logical_processors')::integer, 0), 1),
                    "ReservedMemoryBytes" = COALESCE(
                        NULLIF(("ManifestPayload"::jsonb ->> 'max_memory_bytes')::bigint, 0), 67108864)
                WHERE "ManifestPayload" IS NOT NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_farm_jobs_node",
                schema: "aifarm",
                table: "farm_jobs",
                column: "NodeId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_farm_jobs_resource_reservation",
                schema: "aifarm",
                table: "farm_jobs",
                sql: "\"ReservedLogicalProcessors\" >= 1 AND \"ReservedMemoryBytes\" >= 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_farm_jobs_node",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_farm_jobs_resource_reservation",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropColumn(
                name: "ReservedLogicalProcessors",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropColumn(
                name: "ReservedMemoryBytes",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.CreateIndex(
                name: "UX_farm_jobs_node_active",
                schema: "aifarm",
                table: "farm_jobs",
                column: "NodeId",
                unique: true,
                filter: "\"Status\" IN ('Queued', 'Offered', 'Accepted', 'Running', 'Uploading', 'Verifying')");
        }
    }
}
