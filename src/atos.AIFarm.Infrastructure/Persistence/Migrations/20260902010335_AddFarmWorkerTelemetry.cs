using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace atos.AIFarm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFarmWorkerTelemetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WorkerEnforcement",
                schema: "aifarm",
                table: "farm_jobs",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "WorkerPeakMemoryBytes",
                schema: "aifarm",
                table: "farm_jobs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "WorkerProcessorTimeMilliseconds",
                schema: "aifarm",
                table: "farm_jobs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_farm_jobs_worker_telemetry",
                schema: "aifarm",
                table: "farm_jobs",
                sql: "(\"WorkerPeakMemoryBytes\" IS NULL AND \"WorkerProcessorTimeMilliseconds\" IS NULL AND \"WorkerEnforcement\" IS NULL) OR (\"WorkerPeakMemoryBytes\" > 0 AND \"WorkerProcessorTimeMilliseconds\" > 0 AND \"WorkerEnforcement\" IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_farm_jobs_worker_telemetry",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropColumn(
                name: "WorkerEnforcement",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropColumn(
                name: "WorkerPeakMemoryBytes",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropColumn(
                name: "WorkerProcessorTimeMilliseconds",
                schema: "aifarm",
                table: "farm_jobs");
        }
    }
}
