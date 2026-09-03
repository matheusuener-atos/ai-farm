using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace atos.AIFarm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFarmDemandShardFailover : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_farm_jobs_DemandId_DemandShardIndex",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_farm_jobs_demand_shard_complete",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.AddColumn<int>(
                name: "DemandShardAttempt",
                schema: "aifarm",
                table: "farm_jobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxAttemptsPerShard",
                schema: "aifarm",
                table: "farm_demands",
                type: "integer",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.Sql(
                "UPDATE aifarm.farm_jobs SET \"DemandShardAttempt\" = 1 " +
                "WHERE \"DemandId\" IS NOT NULL AND \"DemandShardAttempt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_farm_jobs_DemandId_DemandShardIndex_DemandShardAttempt",
                schema: "aifarm",
                table: "farm_jobs",
                columns: new[] { "DemandId", "DemandShardIndex", "DemandShardAttempt" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_farm_jobs_demand_shard_complete",
                schema: "aifarm",
                table: "farm_jobs",
                sql: "(\"DemandId\" IS NULL AND \"DemandShardIndex\" IS NULL AND \"DemandShardCount\" IS NULL AND \"DemandShardAttempt\" IS NULL) OR (\"DemandId\" IS NOT NULL AND \"DemandShardIndex\" > 0 AND \"DemandShardCount\" >= \"DemandShardIndex\" AND \"DemandShardAttempt\" > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_farm_demands_shard_attempts",
                schema: "aifarm",
                table: "farm_demands",
                sql: "\"MaxAttemptsPerShard\" >= 1 AND \"MaxAttemptsPerShard\" <= 5");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_farm_jobs_DemandId_DemandShardIndex_DemandShardAttempt",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_farm_jobs_demand_shard_complete",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_farm_demands_shard_attempts",
                schema: "aifarm",
                table: "farm_demands");

            migrationBuilder.DropColumn(
                name: "DemandShardAttempt",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropColumn(
                name: "MaxAttemptsPerShard",
                schema: "aifarm",
                table: "farm_demands");

            migrationBuilder.CreateIndex(
                name: "IX_farm_jobs_DemandId_DemandShardIndex",
                schema: "aifarm",
                table: "farm_jobs",
                columns: new[] { "DemandId", "DemandShardIndex" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_farm_jobs_demand_shard_complete",
                schema: "aifarm",
                table: "farm_jobs",
                sql: "(\"DemandId\" IS NULL AND \"DemandShardIndex\" IS NULL AND \"DemandShardCount\" IS NULL) OR (\"DemandId\" IS NOT NULL AND \"DemandShardIndex\" > 0 AND \"DemandShardCount\" >= \"DemandShardIndex\")");
        }
    }
}
