using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace atos.AIFarm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDistributedFarmDemands : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_farm_jobs_DemandId",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_farm_demands_status",
                schema: "aifarm",
                table: "farm_demands");

            migrationBuilder.AddColumn<int>(
                name: "DemandShardCount",
                schema: "aifarm",
                table: "farm_jobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DemandShardIndex",
                schema: "aifarm",
                table: "farm_jobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FinishedAtUtc",
                schema: "aifarm",
                table: "farm_demands",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OutputDigest",
                schema: "aifarm",
                table: "farm_demands",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OutputPayload",
                schema: "aifarm",
                table: "farm_demands",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OutputSizeBytes",
                schema: "aifarm",
                table: "farm_demands",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OutputVersion",
                schema: "aifarm",
                table: "farm_demands",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TargetShards",
                schema: "aifarm",
                table: "farm_demands",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "farm_demand_shards",
                schema: "aifarm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DemandId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    InputPayload = table.Column<string>(type: "text", nullable: false),
                    InputSizeBytes = table.Column<int>(type: "integer", nullable: false),
                    InputDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_farm_demand_shards", x => x.Id);
                    table.CheckConstraint("CK_farm_demand_shards_input", "\"InputSizeBytes\" > 0 AND length(\"InputDigest\") = 64");
                    table.CheckConstraint("CK_farm_demand_shards_sequence", "\"Sequence\" > 0");
                    table.ForeignKey(
                        name: "FK_farm_demand_shards_farm_demands_DemandId",
                        column: x => x.DemandId,
                        principalSchema: "aifarm",
                        principalTable: "farm_demands",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql(
                "UPDATE aifarm.farm_demands SET \"TargetShards\" = 1");

            migrationBuilder.Sql(
                "UPDATE aifarm.farm_jobs SET \"DemandShardIndex\" = 1, \"DemandShardCount\" = 1 " +
                "WHERE \"DemandId\" IS NOT NULL");

            migrationBuilder.Sql(
                "INSERT INTO aifarm.farm_demand_shards " +
                "(\"Id\", \"DemandId\", \"Sequence\", \"InputPayload\", \"InputSizeBytes\", \"InputDigest\", \"JobId\") " +
                "SELECT \"Id\", \"Id\", 1, \"InputPayload\", \"InputSizeBytes\", \"InputDigest\", \"JobId\" " +
                "FROM aifarm.farm_demands");

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

            migrationBuilder.AddCheckConstraint(
                name: "CK_farm_demands_completed",
                schema: "aifarm",
                table: "farm_demands",
                sql: "\"Status\" <> 'Completed' OR (\"OutputDigest\" IS NOT NULL AND \"FinishedAtUtc\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_farm_demands_output",
                schema: "aifarm",
                table: "farm_demands",
                sql: "(\"OutputVersion\" IS NULL AND \"OutputPayload\" IS NULL AND \"OutputSizeBytes\" IS NULL AND \"OutputDigest\" IS NULL) OR (\"OutputVersion\" IS NOT NULL AND \"OutputPayload\" IS NOT NULL AND \"OutputSizeBytes\" > 0 AND \"OutputDigest\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_farm_demands_shards",
                schema: "aifarm",
                table: "farm_demands",
                sql: "\"TargetShards\" >= 1 AND \"TargetShards\" <= 8");

            migrationBuilder.AddCheckConstraint(
                name: "CK_farm_demands_status",
                schema: "aifarm",
                table: "farm_demands",
                sql: "\"Status\" IN ('Queued', 'Dispatching', 'Processing', 'Submitted', 'Completed', 'Cancelled', 'Expired', 'Failed')");

            migrationBuilder.CreateIndex(
                name: "IX_farm_demand_shards_DemandId_Sequence",
                schema: "aifarm",
                table: "farm_demand_shards",
                columns: new[] { "DemandId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_farm_demand_shards_JobId",
                schema: "aifarm",
                table: "farm_demand_shards",
                column: "JobId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "farm_demand_shards",
                schema: "aifarm");

            migrationBuilder.DropIndex(
                name: "IX_farm_jobs_DemandId_DemandShardIndex",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_farm_jobs_demand_shard_complete",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_farm_demands_completed",
                schema: "aifarm",
                table: "farm_demands");

            migrationBuilder.DropCheckConstraint(
                name: "CK_farm_demands_output",
                schema: "aifarm",
                table: "farm_demands");

            migrationBuilder.DropCheckConstraint(
                name: "CK_farm_demands_shards",
                schema: "aifarm",
                table: "farm_demands");

            migrationBuilder.DropCheckConstraint(
                name: "CK_farm_demands_status",
                schema: "aifarm",
                table: "farm_demands");

            migrationBuilder.DropColumn(
                name: "DemandShardCount",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropColumn(
                name: "DemandShardIndex",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropColumn(
                name: "FinishedAtUtc",
                schema: "aifarm",
                table: "farm_demands");

            migrationBuilder.DropColumn(
                name: "OutputDigest",
                schema: "aifarm",
                table: "farm_demands");

            migrationBuilder.DropColumn(
                name: "OutputPayload",
                schema: "aifarm",
                table: "farm_demands");

            migrationBuilder.DropColumn(
                name: "OutputSizeBytes",
                schema: "aifarm",
                table: "farm_demands");

            migrationBuilder.DropColumn(
                name: "OutputVersion",
                schema: "aifarm",
                table: "farm_demands");

            migrationBuilder.DropColumn(
                name: "TargetShards",
                schema: "aifarm",
                table: "farm_demands");

            migrationBuilder.CreateIndex(
                name: "IX_farm_jobs_DemandId",
                schema: "aifarm",
                table: "farm_jobs",
                column: "DemandId",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_farm_demands_status",
                schema: "aifarm",
                table: "farm_demands",
                sql: "\"Status\" IN ('Queued', 'Dispatching', 'Submitted', 'Cancelled', 'Expired', 'Failed')");
        }
    }
}
