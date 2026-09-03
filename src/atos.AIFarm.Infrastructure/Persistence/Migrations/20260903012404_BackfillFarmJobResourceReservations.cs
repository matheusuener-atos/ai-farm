using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace atos.AIFarm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BackfillFarmJobResourceReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE aifarm.farm_jobs
                SET "ReservedLogicalProcessors" = COALESCE(
                        NULLIF(("ManifestPayload"::jsonb ->> 'max_logical_processors')::integer, 0), 1),
                    "ReservedMemoryBytes" = COALESCE(
                        NULLIF(("ManifestPayload"::jsonb ->> 'max_memory_bytes')::bigint, 0), 67108864)
                WHERE "ManifestPayload" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
